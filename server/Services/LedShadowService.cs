using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Protocol;
using System.Runtime.CompilerServices;

namespace BalloonMesh.Server.Services;

/// <summary>Models LED commands after gateway dispatch; it is not a receipt from a device.</summary>
public sealed class LedShadowService : BackgroundService
{
    private const int Capacity = 512;
    private const int TombstoneCapacity = 64;
    private readonly object _gate = new();
    private readonly Dictionary<ushort, Node> _nodes = [];
    private readonly ConditionalWeakTable<DownlinkCommand, object> _delivered = new();
    private readonly TimeProvider _time;
    private readonly IMonitorEvents _notifier;
    private readonly ILogger<LedShadowService>? _logger;
    private bool _dirty;
    private bool _colourDirty;
    private readonly Guid _instanceId = Guid.NewGuid();
    private long _revision;

    public LedShadowService(TimeProvider time, IMonitorEvents notifier, ILogger<LedShadowService>? logger = null)
    {
        _time = time;
        _notifier = notifier;
        _logger = logger;
    }

    public void Observe(DownlinkCommand command, IEnumerable<ushort> addresses)
    {
        if (!BalloonProtocol.TryGetMessageOp(command.Opcode, out var op) ||
            command.Payload.Length < 5 ||
            op is not (BalloonProtocol.OpLedSet or BalloonProtocol.OpLedSetNow or
                BalloonProtocol.OpLedCancel or BalloonProtocol.OpLedResetAll or BalloonProtocol.OpLedResetAndSet) ||
            ((op is BalloonProtocol.OpLedSet or BalloonProtocol.OpLedSetNow or BalloonProtocol.OpLedResetAndSet) && command.Payload.Length != 8) ||
            ((op is BalloonProtocol.OpLedCancel or BalloonProtocol.OpLedResetAll) && command.Payload.Length != 5))
            return;
        var tick = BalloonProtocol.DecodeTick40(command.Payload);
        var now = BalloonProtocol.NowTick40(_time);
        lock (_gate)
        {
            ConsumeDueLocked(now);
            foreach (var address in addresses.Distinct())
            {
                if (!_nodes.TryGetValue(address, out var node)) _nodes[address] = node = new Node();
                var rgb = command.Payload.Length == 8 ? (command.Payload[5], command.Payload[6], command.Payload[7]) : default;
                switch (op)
                {
                    case BalloonProtocol.OpLedSet:
                        if (tick <= now || node.LastExecuted is { } executed && tick <= executed ||
                            node.Cutoff is { } cutoff && tick <= cutoff || node.Tombstones.Contains(tick))
                        {
                            node.Rejected++;
                            break;
                        }
                        if (!node.Queue.ContainsKey(tick) && node.Queue.Count >= Capacity)
                        {
                            var furthest = node.Queue.Last().Key;
                            if (tick >= furthest) { node.Rejected++; break; }
                            node.Queue.Remove(furthest);
                            node.Rejected++;
                        }
                        node.Queue[tick] = rgb;
                        break;
                    case BalloonProtocol.OpLedSetNow:
                        if (node.LastNow is null || tick > node.LastNow)
                        {
                            node.LastNow = tick;
                            node.Colour = rgb;
                            node.Known = true;
                            _colourDirty = true;
                        }
                        break;
                    case BalloonProtocol.OpLedCancel:
                        if (tick <= now || node.LastExecuted is { } last && tick <= last || node.Cutoff is { } c && tick <= c) break;
                        node.Queue.Remove(tick);
                        if (!node.Tombstones.Contains(tick))
                        {
                            if (node.Tombstones.Count < TombstoneCapacity) node.Tombstones.Add(tick);
                            else
                            {
                                node.Tombstones[node.TombstoneNext] = tick;
                                node.TombstoneNext = (node.TombstoneNext + 1) % TombstoneCapacity;
                            }
                        }
                        break;
                    case BalloonProtocol.OpLedResetAll:
                    case BalloonProtocol.OpLedResetAndSet:
                        if (node.Cutoff is { } previous && tick <= previous) break;
                        node.Cutoff = tick;
                        foreach (var key in node.Queue.Keys.TakeWhile(key => key <= tick).ToArray()) node.Queue.Remove(key);
                        node.Tombstones.RemoveAll(value => value <= tick);
                        if (node.TombstoneNext >= node.Tombstones.Count) node.TombstoneNext = 0;
                        if (op == BalloonProtocol.OpLedResetAndSet) { node.Colour = rgb; node.Known = true; _colourDirty = true; }
                        break;
                }
            }
            _dirty = true;
            _revision++;
        }
    }

    public void ObserveDelivered(DownlinkCommand command)
    {
        if (command.ShadowRecipients is not { Count: > 0 } recipients) return;
        lock (_gate)
        {
            if (_delivered.TryGetValue(command, out _)) return;
            _delivered.Add(command, new object());
            Observe(command, recipients);
        }
    }

    public IReadOnlyList<LedShadowDto> Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    public LedShadowSnapshotDto Capture()
    {
        lock (_gate) return CaptureLocked();
    }

    public bool ConsumeDue()
    {
        var tick = BalloonProtocol.NowTick40(_time);
        lock (_gate) return ConsumeDueLocked(tick);
    }

    private bool ConsumeDueLocked(ulong tick)
    {
        var consumed = false;
        foreach (var node in _nodes.Values)
        {
            var count = 0;
            while (count++ < 32 && node.Queue.Count > 0 && node.Queue.First().Key <= tick)
            {
                var entry = node.Queue.First();
                node.Queue.Remove(entry.Key);
                node.LastExecuted = entry.Key;
                node.Colour = entry.Value;
                node.Known = true;
                _dirty = true;
                _colourDirty = true;
                consumed = true;
            }
        }
        if (consumed) _revision++;
        return consumed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        var lastSent = _time.GetUtcNow();
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var consumed = ConsumeDue();
            LedShadowSnapshotDto? snapshot = null;
            lock (_gate)
            {
                if (_dirty && (_colourDirty || consumed || _time.GetUtcNow() - lastSent >= TimeSpan.FromMilliseconds(250)))
                {
                    snapshot = CaptureLocked();
                    _dirty = false;
                    _colourDirty = false;
                    lastSent = _time.GetUtcNow();
                }
            }
            if (snapshot is not null)
            {
                try { await _notifier.LedShadowChangedAsync(snapshot); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { _logger?.LogWarning(exception, "LED shadow update failed"); }
            }
        }
    }

    private LedShadowDto[] SnapshotLocked() => _nodes.Select(item => new LedShadowDto(
        item.Key, item.Value.Known, item.Value.Colour.R, item.Value.Colour.G, item.Value.Colour.B,
        item.Value.Queue.Count, item.Value.Rejected)).OrderBy(item => item.MeshAddress).ToArray();

    private LedShadowSnapshotDto CaptureLocked() => new(_instanceId, _revision, SnapshotLocked());

    private sealed class Node
    {
        public SortedDictionary<ulong, (byte R, byte G, byte B)> Queue { get; } = [];
        public List<ulong> Tombstones { get; } = [];
        public int TombstoneNext { get; set; }
        public (byte R, byte G, byte B) Colour { get; set; }
        public bool Known { get; set; }
        public ulong? LastExecuted { get; set; }
        public ulong? LastNow { get; set; }
        public ulong? Cutoff { get; set; }
        public long Rejected { get; set; }
    }
}
