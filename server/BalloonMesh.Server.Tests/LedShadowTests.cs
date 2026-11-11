using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Protocol;
using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BalloonMesh.Server.Tests;

[Collection("PostgresDatabase")]
public sealed class LedShadowTests
{
    [Fact]
    public void ExecutesAtReservedTimeAndOverwritesSameTick()
    {
        var time = NewTime();
        var shadow = new LedShadowService(time, new RecordingMonitorEvents());
        var initial = shadow.Capture();
        var nowTick = BalloonProtocol.NowTick40(time);
        shadow.Observe(Set(nowTick + 3000, 255, 0, 0), [0x101, 0x102]);
        shadow.Observe(Set(nowTick + 3000, 0, 0, 255), [0x101]);
        var reserved = shadow.Capture();
        Assert.Equal(initial.InstanceId, reserved.InstanceId);
        Assert.True(reserved.Revision > initial.Revision);
        Assert.All(shadow.Snapshot(), node => { Assert.False(node.Known); Assert.Equal(1, node.QueueCount); });
        time.Advance(TimeSpan.FromMilliseconds(29_990));
        Assert.False(shadow.ConsumeDue());
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.True(shadow.ConsumeDue());
        Assert.True(shadow.Capture().Revision > reserved.Revision);
        var states = shadow.Snapshot().ToDictionary(node => node.MeshAddress);
        Assert.Equal((byte)255, states[0x101].B);
        Assert.Equal((byte)255, states[0x102].R);
        Assert.All(states.Values, node => { Assert.True(node.Known); Assert.Equal(0, node.QueueCount); });
    }

    [Fact]
    public void BlackoutAndNextFrameAreConsumedAtTheirSeparateTicks()
    {
        var time = NewTime();
        var shadow = new LedShadowService(time, new RecordingMonitorEvents());
        var tick = BalloonProtocol.NowTick40(time) + 3000;
        shadow.Observe(Set(tick, 0, 0, 0), [1]);
        shadow.Observe(Set(tick + 4, 255, 0, 0), [1]);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(shadow.ConsumeDue());
        var black = Assert.Single(shadow.Snapshot());
        Assert.True(black.Known);
        Assert.Equal((byte)0, black.R);
        Assert.Equal(1, black.QueueCount);
        time.Advance(TimeSpan.FromMilliseconds(40));
        Assert.True(shadow.ConsumeDue());
        var red = Assert.Single(shadow.Snapshot());
        Assert.Equal((byte)255, red.R);
        Assert.Equal(0, red.QueueCount);
    }

    [Fact]
    public void CancelBeforeSetAndResetMatchQueueSemantics()
    {
        var time = NewTime();
        var shadow = new LedShadowService(time, new RecordingMonitorEvents());
        var tick = BalloonProtocol.NowTick40(time) + 1000;
        shadow.Observe(Command(BalloonProtocol.OpLedCancel, BalloonProtocol.EncodeLedCancel(tick)), [1]);
        shadow.Observe(Set(tick, 1, 2, 3), [1]);
        shadow.Observe(Set(tick + 100, 4, 5, 6), [1]);
        var cancelled = Assert.Single(shadow.Snapshot());
        Assert.Equal(1, cancelled.RejectedCount);
        Assert.Equal(1, cancelled.QueueCount);
        shadow.Observe(Command(BalloonProtocol.OpLedResetAll, BalloonProtocol.EncodeLedReset(tick + 100)), [1]);
        Assert.Equal(0, Assert.Single(shadow.Snapshot()).QueueCount);
    }

    [Fact]
    public void QueueAtCapacityRetainsNearerCommands()
    {
        var time = NewTime();
        var shadow = new LedShadowService(time, new RecordingMonitorEvents());
        var tick = BalloonProtocol.NowTick40(time) + 100;
        for (ulong i = 0; i < 512; i++) shadow.Observe(Set(tick + i, 1, 2, 3), [1]);
        shadow.Observe(Set(tick + 700, 9, 9, 9), [1]);
        shadow.Observe(Set(tick - 1, 8, 8, 8), [1]);
        var state = Assert.Single(shadow.Snapshot());
        Assert.Equal(512, state.QueueCount);
        Assert.Equal(2, state.RejectedCount);
        time.Advance(TimeSpan.FromMilliseconds(990));
        shadow.ConsumeDue();
        Assert.Equal((byte)8, Assert.Single(shadow.Snapshot()).R);
    }

    [Fact]
    public void ImmediateCommandsIgnoreOlderIssuedTicks()
    {
        var time = NewTime();
        var shadow = new LedShadowService(time, new RecordingMonitorEvents());
        var tick = BalloonProtocol.NowTick40(time);
        shadow.Observe(Command(BalloonProtocol.OpLedSetNow, BalloonProtocol.EncodeLedSetNow(tick, 10, 20, 30)), [1]);
        shadow.Observe(Command(BalloonProtocol.OpLedSetNow, BalloonProtocol.EncodeLedSetNow(tick - 1, 90, 90, 90)), [1]);
        Assert.Equal((byte)10, Assert.Single(shadow.Snapshot()).R);
    }

    [Fact]
    public async Task EmulatesOnlyWhenGatewayReceivesAndDeduplicatesAcrossGateways()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = NewTime();
        var registry = new GatewayRegistry(events);
        var shadow = new LedShadowService(time, events);
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time,
            NullLogger<LedDispatchService>.Instance);
        var target = fleet.ResolveTarget("balloon", "0x0101");
        var at = time.GetUtcNow().AddSeconds(30);
        Assert.True(dispatch.EnqueueScheduled(target, BalloonProtocol.Tick40FromUnixMs(at.ToUnixTimeMilliseconds()), at, 10, 20, 30));
        Assert.Equal(0, await dispatch.TickAsync());
        Assert.Empty(shadow.Snapshot());
        registry.GetOrRegister("gateway-test");
        registry.GetOrRegister("gateway-test-2");
        Assert.Equal(1, await dispatch.TickAsync());
        Assert.Empty(shadow.Snapshot());
        Assert.True(registry.TryReadCommand("gateway-test", out var first));
        Assert.True(registry.TryReadCommand("gateway-test-2", out var duplicate));
        Assert.Same(first, duplicate);
        shadow.ObserveDelivered(first!);
        Assert.False(Assert.Single(shadow.Snapshot()).Known);
        time.Advance(TimeSpan.FromSeconds(30));
        shadow.ConsumeDue();
        shadow.ObserveDelivered(duplicate!);
        var state = Assert.Single(shadow.Snapshot());
        Assert.True(state.Known);
        Assert.Equal(((byte)10, (byte)20, (byte)30), (state.R, state.G, state.B));
        Assert.Equal(0, state.QueueCount);
        Assert.Equal(0, state.RejectedCount);
    }

    private static TestTimeProvider NewTime() => new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
    private static DownlinkCommand Set(ulong tick, byte r, byte g, byte b) =>
        Command(BalloonProtocol.OpLedSet, BalloonProtocol.EncodeLedSet(tick, r, g, b));
    private static DownlinkCommand Command(byte op, byte[] payload) =>
        new(0xC010, BalloonProtocol.VendorOpcode(op), payload, 7);
}
