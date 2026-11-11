using BalloonMesh.Server.Data;
using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Protocol;

namespace BalloonMesh.Server.Services;

/// <summary>
/// Rolling downlink scheduler.  The server estimates each balloon's reservation
/// queue (target 350 of the device hard limit 512), sends reservations to all
/// gateways, and supplements capacity as it becomes available.
/// </summary>
public sealed class LedDispatchService
{
    private const int TargetQueueDepth = 350;
    private const int HardQueueDepth = 512;

    private readonly object _gate = new();
    private readonly List<PendingReservation> _pending = [];
    private readonly Dictionary<ushort, QueueEstimate> _estimates = [];
    private readonly Dictionary<ushort, ulong> _lastIssued = [];
    private readonly Dictionary<ushort, ulong> _lastCutoff = [];
    private readonly GatewayRegistry _gateways;
    private readonly FleetService _fleet;
    private readonly DataStore _store;
    private readonly IMonitorEvents _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<LedDispatchService> _logger;
    private long _expiredEffectReservations;

    public LedDispatchService(
        GatewayRegistry gateways,
        FleetService fleet,
        DataStore store,
        IMonitorEvents notifier,
        TimeProvider time,
        ILogger<LedDispatchService> logger)
    {
        _gateways = gateways;
        _fleet = fleet;
        _store = store;
        _notifier = notifier;
        _time = time;
        _logger = logger;
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public long ExpiredEffectReservations
    {
        get { lock (_gate) return _expiredEffectReservations; }
    }

    public void NoteExpiredEffectReservation()
    {
        lock (_gate) _expiredEffectReservations++;
    }

    public bool EnqueueScheduled(TargetSpec target, ulong executeTick, DateTimeOffset executeAt,
        byte r, byte g, byte b, long? showStepId = null, bool isEffect = false)
    {
        var nowTick = BalloonProtocol.NowTick40(_time);
        if (executeTick <= nowTick)
        {
            return false;
        }

        lock (_gate)
        {
            InsertPending(new PendingReservation(
                target,
                BalloonProtocol.OpLedSet,
                BalloonProtocol.EncodeLedSet(executeTick, r, g, b),
                executeTick,
                executeAt,
                r, g, b,
                showStepId, isEffect));
        }
        return true;
    }

    public int SendImmediate(TargetSpec target, byte opcode, byte[] payload)
    {
        var command = new DownlinkCommand(target.Destination, BalloonProtocol.VendorOpcode(opcode), payload, 7);
        return _gateways.Broadcast(command);
    }

    public ulong NextIssuedTick(IEnumerable<ushort> addresses)
    {
        var now = BalloonProtocol.NowTick40(_time);
        lock (_gate)
        {
            ulong value = now;
            foreach (var address in addresses)
            {
                if (_lastIssued.TryGetValue(address, out var last) && last >= value)
                {
                    value = last + 1;
                }
            }
            foreach (var address in addresses)
            {
                _lastIssued[address] = value;
            }
            return value;
        }
    }

    public ulong NextCutoffTick(IEnumerable<ushort> addresses, ulong requested)
    {
        lock (_gate)
        {
            ulong value = requested;
            foreach (var address in addresses)
            {
                if (_lastCutoff.TryGetValue(address, out var last) && last >= value)
                {
                    value = last + 1;
                }
            }
            foreach (var address in addresses)
            {
                _lastCutoff[address] = value;
            }
            return value;
        }
    }

    public void NoteCutoff(IEnumerable<ushort> addresses, ulong cutoffTick)
    {
        lock (_gate)
        {
            foreach (var address in addresses)
            {
                var estimate = GetEstimate(address);
                estimate.SentTicks.RemoveAll(tick => tick <= cutoffTick);
                estimate.LastCutoff = Math.Max(estimate.LastCutoff, cutoffTick);
                WriteEstimate(address, estimate);
            }
        }
    }

    public void NoteCancel(IEnumerable<ushort> addresses, ulong executeTick)
    {
        lock (_gate)
        {
            foreach (var address in addresses)
            {
                var estimate = GetEstimate(address);
                estimate.SentTicks.RemoveAll(tick => tick == executeTick);
                WriteEstimate(address, estimate);
            }
        }
    }

    public void NoteQueueStatus(ushort address, ushort count)
    {
        lock (_gate)
        {
            var estimate = GetEstimate(address);
            estimate.ReportedCount = count;
            WriteEstimate(address, estimate);
        }
    }

    public int GetEstimatedCount(ushort address)
    {
        lock (_gate)
        {
            return GetEstimate(address).EffectiveCount(BalloonProtocol.NowTick40(_time));
        }
    }

    public async Task<int> TickAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var nowTick = BalloonProtocol.NowTick40(_time);
        var sent = 0;
        List<PendingReservation> sendList = [];
        List<PendingReservation> expired = [];

        lock (_gate)
        {
            var selectedCounts = new Dictionary<ushort, int>();
            foreach (var address in _estimates.Keys.ToArray())
            {
                var estimate = GetEstimate(address);
                estimate.SentTicks.RemoveAll(tick => tick + 300 <= nowTick); // 3 s grace
            }

            foreach (var item in _pending)
            {
                if (item.ExecuteAt <= now)
                {
                    expired.Add(item);
                    continue;
                }

                var addresses = item.Target.Balloons.Select(balloon => balloon.MeshAddress).ToArray();
                if (addresses.Length == 0)
                {
                    sendList.Add(item);
                    continue;
                }

                if (addresses.All(address => GetEstimate(address).EffectiveCount(nowTick) + selectedCounts.GetValueOrDefault(address) < TargetQueueDepth))
                {
                    sendList.Add(item);
                    foreach (var address in addresses)
                    {
                        selectedCounts[address] = selectedCounts.GetValueOrDefault(address) + 1;
                    }
                }
            }

            foreach (var item in sendList)
            {
                _pending.Remove(item);
            }
            foreach (var item in expired)
            {
                _pending.Remove(item);
                if (item.IsEffect) _expiredEffectReservations++;
            }
        }

        foreach (var item in expired)
        {
            if (item.ShowStepId is { } showStepId)
            {
                _store.UpdateShowStepStatus(showStepId, "expired");
            }
        }
        if (expired.Count > 0)
        {
            _logger.LogWarning("{Count} reservation(s) expired before reaching a gateway; earliest execution time {ExecuteAt:O}",
                expired.Count, expired.Min(item => item.ExecuteAt));
        }

        foreach (var item in sendList)
        {
            var command = new DownlinkCommand(
                item.Target.Destination,
                BalloonProtocol.VendorOpcode(item.Opcode),
                item.Payload,
                7);
            var gateways = _gateways.Broadcast(command);
            if (gateways == 0)
            {
                lock (_gate)
                {
                    InsertPending(item);
                }
                continue;
            }

            lock (_gate)
            {
                foreach (var balloon in item.Target.Balloons)
                {
                    var estimate = GetEstimate(balloon.MeshAddress);
                    if (!estimate.SentTicks.Contains(item.ExecuteTick))
                    {
                        estimate.SentTicks.Add(item.ExecuteTick);
                    }
                    WriteEstimate(balloon.MeshAddress, estimate);
                }
            }
            sent++;
            if (item.ShowStepId is { } showStepId)
            {
                _store.UpdateShowStepStatus(showStepId, "sent");
            }
            _logger.LogInformation("sent {Kind} for {Target} tick={Tick} gateways={Gateways}",
                item.Opcode == BalloonProtocol.OpLedSet ? "LED_SET" : "command",
                item.Target.Description, item.ExecuteTick, gateways);
        }

        if (sent > 0)
        {
            await _notifier.CommandSentAsync(new CommandResultDto(
                true,
                $"Dispatched {sent} reservation(s).",
                _gateways.Snapshot().Count,
                sendList.Sum(item => item.Target.Balloons.Count),
                null,
                null));
        }
        return sent;
    }

    private QueueEstimate GetEstimate(ushort address) =>
        _estimates.TryGetValue(address, out var estimate) ? estimate : new QueueEstimate();

    private void InsertPending(PendingReservation reservation)
    {
        var index = _pending.Count;
        while (index > 0 && _pending[index - 1].ExecuteAt > reservation.ExecuteAt) index--;
        _pending.Insert(index, reservation);
    }

    private void WriteEstimate(ushort address, QueueEstimate estimate)
    {
        _estimates[address] = estimate;
        _fleet.UpdateEstimatedQueue(address, estimate.EffectiveCount(BalloonProtocol.NowTick40(_time)));
    }

    private sealed class QueueEstimate
    {
        public List<ulong> SentTicks { get; } = [];
        public int ReportedCount { get; set; }
        public ulong LastCutoff { get; set; }

        public int EffectiveCount(ulong nowTick)
        {
            SentTicks.RemoveAll(tick => tick <= nowTick);
            return Math.Clamp(Math.Max(ReportedCount, SentTicks.Count), 0, HardQueueDepth);
        }
    }

    private sealed record PendingReservation(
        TargetSpec Target,
        byte Opcode,
        byte[] Payload,
        ulong ExecuteTick,
        DateTimeOffset ExecuteAt,
        byte R,
        byte G,
        byte B,
        long? ShowStepId,
        bool IsEffect);
}

public sealed class LedDispatchWorker : BackgroundService
{
    private readonly LedDispatchService _dispatch;
    private readonly ILogger<LedDispatchWorker> _logger;

    public LedDispatchWorker(LedDispatchService dispatch, ILogger<LedDispatchWorker> logger)
    {
        _dispatch = dispatch;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await _dispatch.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "LED dispatch tick failed");
            }
        }
    }
}
