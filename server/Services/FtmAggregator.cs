using System.Collections.Concurrent;
using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Protocol;

namespace BalloonMesh.Server.Services;

/// <summary>
/// Implements the server-side FTM batch rules:
///  - t0 is the first arrival for (src, batch_id);
///  - the first calculation happens at t0 + 10 s;
///  - late reports are re-calculated until t0 + 30 s;
///  - an older batch is allowed to overwrite the displayed position.
/// </summary>
public sealed class FtmAggregator
{
    private readonly object _gate = new();
    private readonly Dictionary<BatchKey, BatchState> _batches = new();
    private readonly ConcurrentDictionary<BatchKey, DateTimeOffset> _closed = new();
    private readonly FleetService _fleet;
    private readonly NeighborStore _neighbors;
    private readonly PositionSolver _solver;
    private readonly IMonitorEvents _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<FtmAggregator> _logger;

    public FtmAggregator(
        FleetService fleet,
        NeighborStore neighbors,
        PositionSolver solver,
        IMonitorEvents notifier,
        TimeProvider time,
        ILogger<FtmAggregator> logger)
    {
        _fleet = fleet;
        _neighbors = neighbors;
        _solver = solver;
        _notifier = notifier;
        _time = time;
        _logger = logger;
    }

    public void AddReport(ushort source, FtmReport report, string gatewayId, DateTimeOffset receivedAt)
    {
        var key = new BatchKey(source, report.BatchId);
        if (_closed.TryGetValue(key, out var closedAt)
            && receivedAt - closedAt < TimeSpan.FromMinutes(2))
        {
            return;
        }

        lock (_gate)
        {
            if (_batches.TryGetValue(key, out var closed) && closed.Closed)
            {
                return;
            }

            if (!_batches.TryGetValue(key, out var batch))
            {
                batch = new BatchState(receivedAt);
                _batches[key] = batch;
                _logger.LogInformation("FTM batch {Source:X4}/{Batch} opened at {T0:O}",
                    source, report.BatchId, receivedAt);
            }

            if (receivedAt - batch.T0 > TimeSpan.FromSeconds(30))
            {
                return;
            }

            var reportKey = (report.AnchorId, report.DistanceCm, report.Timestamp);
            if (!batch.Seen.Add(reportKey))
            {
                return;
            }

            if (!batch.Reports.TryGetValue(report.AnchorId, out var existing))
            {
                batch.Reports[report.AnchorId] = report;
                batch.Changed = true;
            }
            else if (IsNewer(report.Timestamp, existing.Timestamp))
            {
                batch.Reports[report.AnchorId] = report;
                batch.Changed = true;
            }

        }
    }

    public Task<int> ProcessDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        List<PendingCompute>? due;
        lock (_gate)
        {
            due = TickLocked(now);
        }
        return due is null ? Task.FromResult(0) : ComputeAsync(due, cancellationToken);
    }

    private List<PendingCompute>? TickLocked(DateTimeOffset now)
    {
        List<PendingCompute>? due = null;
        foreach (var pair in _batches.ToArray())
        {
            var key = pair.Key;
            var batch = pair.Value;
            if (batch.Closed)
            {
                continue;
            }

            var initialDue = !batch.InitialComputed && now - batch.T0 >= TimeSpan.FromSeconds(10);
            var refreshDue = batch.InitialComputed && batch.Changed && now - batch.T0 < TimeSpan.FromSeconds(30);
            var closeDue = now - batch.T0 >= TimeSpan.FromSeconds(30);
            if (!initialDue && !refreshDue && !closeDue)
            {
                continue;
            }

            if (batch.Reports.Count > 0)
            {
                due ??= [];
                due.Add(new PendingCompute(
                    key,
                    batch.Reports.Values.ToArray(),
                    batch.InitialComputed,
                    now,
                    closeDue));
            }

            batch.InitialComputed = true;
            batch.Changed = false;
            if (closeDue)
            {
                batch.Closed = true;
                _batches.Remove(key);
                _closed[key] = now;
            }
        }

        foreach (var closed in _closed.ToArray())
        {
            if (now - closed.Value > TimeSpan.FromHours(1))
            {
                _closed.TryRemove(closed.Key, out _);
            }
        }
        return due;
    }

    private async Task<int> ComputeAsync(IReadOnlyList<PendingCompute> due, CancellationToken cancellationToken)
    {
        var anchors = _fleet.GetAnchors().ToDictionary(anchor => (byte)anchor.AnchorId);
        var computed = 0;
        foreach (var item in due)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previous = _fleet.GetBalloonRecord(item.Key.Source);
                var constraints = new List<RangeConstraint>();
                foreach (var report in item.Reports)
                {
                    if (!anchors.TryGetValue(report.AnchorId, out var anchor))
                    {
                        continue;
                    }
                    constraints.Add(new RangeConstraint(
                        new GeoPoint(anchor.Lat, anchor.Lon),
                        report.DistanceCm / 100.0,
                        1.0,
                        "ftm"));
                }

                foreach (var neighbor in _neighbors.Get(item.Key.Source, item.Key.Batch))
                {
                    var record = _fleet.GetBalloonRecord(neighbor.NeighborAddress);
                    if (record?.Lat is not { } lat || record.Lon is not { } lon)
                    {
                        continue;
                    }
                    constraints.Add(new RangeConstraint(
                        new GeoPoint(lat, lon),
                        PositionSolver.RssiToDistanceMeters(neighbor.RssiDbm),
                        0.15,
                        "neighbor"));
                }

                if (constraints.Count == 0)
                {
                    continue;
                }

                GeoPoint? prior = previous?.Lat is { } previousLat && previous.Lon is { } previousLon
                    ? new GeoPoint(previousLat, previousLon)
                    : null;
                var solution = _solver.Solve(constraints, prior);
                if (solution is null)
                {
                    continue;
                }

                var dto = _fleet.ApplyPosition(
                    item.Key.Source,
                    solution.Position.Latitude,
                    solution.Position.Longitude,
                    solution.Confidence,
                    item.Key.Batch,
                    solution.Method,
                    item.ComputedAt);
                _ = _notifier.BalloonUpdatedAsync(dto);
                _logger.LogInformation(
                    "position {Source:X4} batch {Batch}: {Lat:F6},{Lon:F6} conf={Confidence:F2} method={Method} anchors={Anchors}",
                    item.Key.Source, item.Key.Batch, solution.Position.Latitude,
                    solution.Position.Longitude, solution.Confidence, solution.Method,
                    constraints.Count(c => c.Source == "ftm"));
                computed++;
            }
            finally
            {
                if (item.IsFinal)
                {
                    _neighbors.Remove(item.Key.Source, item.Key.Batch);
                }
            }
        }
        return computed;
    }

    private static bool IsNewer(uint candidate, uint existing)
    {
        if (existing == BalloonProtocol.TimestampUnknown)
        {
            return candidate != BalloonProtocol.TimestampUnknown;
        }
        if (candidate == BalloonProtocol.TimestampUnknown)
        {
            return false;
        }
        return !BalloonProtocol.TimestampNotNewer(candidate, existing);
    }

    private sealed class BatchState
    {
        public BatchState(DateTimeOffset t0)
        {
            T0 = t0;
        }

        public DateTimeOffset T0 { get; }
        public Dictionary<byte, FtmReport> Reports { get; } = [];
        public HashSet<(byte Anchor, ushort Distance, uint Timestamp)> Seen { get; } = [];
        public bool InitialComputed { get; set; }
        public bool Changed { get; set; }
        public bool Closed { get; set; }
    }

    private readonly record struct BatchKey(ushort Source, byte Batch);

    private sealed record PendingCompute(
        BatchKey Key,
        IReadOnlyList<FtmReport> Reports,
        bool WasRefresh,
        DateTimeOffset ComputedAt,
        bool IsFinal);
}
