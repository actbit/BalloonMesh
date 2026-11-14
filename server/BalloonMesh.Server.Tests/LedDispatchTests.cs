using BalloonMesh.Server.Models;
using BalloonMesh.Protocol;
using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BalloonMesh.Server.Tests;

[Collection("PostgresDatabase")]
public sealed class LedDispatchTests
{
    [Fact]
    public async Task FullGatewayQueueKeepsReservationUntilCapacityReturns()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = TestRegistry.Create(events);
        registry.GetOrRegister("gw-test");
        var filler = new DownlinkCommand(1, 1, [1], 7);
        for (var i = 0; i < 2048; i++) Assert.Equal(1, registry.Broadcast(filler));
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time, NullLogger<LedDispatchService>.Instance);
        var at = time.GetUtcNow().AddSeconds(30);
        Assert.True(dispatch.EnqueueScheduled(fleet.ResolveTarget("balloon", "0x0101"), BalloonProtocol.Tick40FromUnixMs(at.ToUnixTimeMilliseconds()), at, 1, 2, 3));
        Assert.Equal(0, await dispatch.TickAsync());
        Assert.Equal(1, dispatch.PendingCount);
        Assert.True(registry.TryReadCommand("gw-test", out _));
        Assert.Equal(1, await dispatch.TickAsync());
        Assert.Equal(0, dispatch.PendingCount);
    }

    [Fact]
    public async Task ScheduledBatchRespectsPerNodeReservationCapacity()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = TestRegistry.Create(events);
        registry.GetOrRegister("gw-test");
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time, NullLogger<LedDispatchService>.Instance);
        var target = fleet.ResolveTarget("balloon", "0x0101");
        for (var i = 1; i <= 351; i++)
        {
            var at = time.GetUtcNow().AddSeconds(30).AddMilliseconds(i * 10);
            Assert.True(dispatch.EnqueueScheduled(target, BalloonProtocol.Tick40FromUnixMs(at.ToUnixTimeMilliseconds()), at, 1, 2, 3));
        }
        Assert.Equal(350, await dispatch.TickAsync());
        Assert.Equal(1, dispatch.PendingCount);
        Assert.Equal(0, await dispatch.TickAsync());
    }

    [Fact]
    public async Task RecoveryExpiresQueuedStepsThatAreAlreadyPast()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = TestRegistry.Create(events);
        var dispatch = new LedDispatchService(
            registry, fleet, database.Store, events, time, NullLogger<LedDispatchService>.Instance);
        var worker = new ShowRecoveryWorker(
            database.Store, fleet, dispatch, time, NullLogger<ShowRecoveryWorker>.Instance);
        var id = database.Store.AddShowStep("past", "all", "all",
            time.GetUtcNow().AddSeconds(-1), 1, 2, 3, "queued");

        Assert.Equal(0, await worker.RecoverAsync());
        Assert.Equal("expired", database.Store.GetShowStep(id)!.Status);
    }

    [Fact]
    public async Task ScheduledReservationWaitsForAGateway()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = TestRegistry.Create(events);
        var dispatch = new LedDispatchService(
            registry, fleet, database.Store, events, time, NullLogger<LedDispatchService>.Instance);
        var target = fleet.ResolveTarget("all", "all");
        var executeAt = time.GetUtcNow().AddMinutes(2);
        var tick = BalloonProtocol.Tick40FromUnixMs(executeAt.ToUnixTimeMilliseconds());

        Assert.True(dispatch.EnqueueScheduled(target, tick, executeAt, 10, 20, 30));
        Assert.Equal(1, dispatch.PendingCount);
        Assert.Equal(0, await dispatch.TickAsync());
        Assert.Equal(1, dispatch.PendingCount);

        var first = dispatch.NextIssuedTick([0x0101]);
        var second = dispatch.NextIssuedTick([0x0101]);
        Assert.True(second > first);
        var cutoff = dispatch.NextCutoffTick([0x0101], 10);
        dispatch.NoteCutoff([0x0101], cutoff);
        var laterCutoff = dispatch.NextCutoffTick([0x0101], 10);
        Assert.True(laterCutoff > cutoff);
    }

    [Fact]
    public async Task ImmediateCommandsRequireAReachableGateway()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = TestRegistry.Create(events);
        var dispatch = new LedDispatchService(
            registry, fleet, database.Store, events, time, NullLogger<LedDispatchService>.Instance);
        var commands = new CommandService(
            fleet, dispatch, registry, database.Store, events, time,
            NullLogger<CommandService>.Instance);

        // Without a reachable gateway the immediate commands are rejected and
        // must NOT be applied optimistically to the fleet colours.
        var now = await commands.SetNowAsync(new LedNowRequest("all", "all", 1, 2, 3), default);
        Assert.False(now.Accepted);
        Assert.Equal(0, now.Gateways);
        Assert.Equal(0, (int)fleet.GetBalloon(0x0101)!.R);

        var reset = await commands.ResetAsync(
            new LedResetRequest("all", "all", null, true, 7, 8, 9), default);
        Assert.False(reset.Accepted);
        Assert.NotNull(reset.Payload);
        Assert.Equal(0, (int)fleet.GetBalloon(0x0101)!.R);

        // Scheduled reservations are different: they stay pending until a
        // gateway appears (or they expire at ExecuteAt).
        var scheduled = await commands.ScheduleAsync(
            new LedSetRequest("all", "all", time.GetUtcNow().AddMinutes(2), 4, 5, 6), default);
        Assert.True(scheduled.Accepted);
        Assert.NotNull(scheduled.Payload);
        Assert.Equal(16, scheduled.Payload!.Length); // 8 bytes in hex

        var showSteps = database.Store.GetShowSteps(10);
        Assert.Single(showSteps);

        var recoveryDispatch = new LedDispatchService(
            registry, fleet, database.Store, events, time, NullLogger<LedDispatchService>.Instance);
        var recovery = new ShowRecoveryWorker(
            database.Store, fleet, recoveryDispatch, time, NullLogger<ShowRecoveryWorker>.Instance);
        Assert.Equal(1, await recovery.RecoverAsync());
        Assert.Equal(1, recoveryDispatch.PendingCount);

        // A queued show step cannot be cancelled while the tombstone cannot
        // be delivered; it stays queued.
        var failedCancel = await commands.CancelShowStepAsync(showSteps[0].Id, default);
        Assert.NotNull(failedCancel);
        Assert.False(failedCancel!.Accepted);
        Assert.Equal("queued", database.Store.GetShowStep(showSteps[0].Id)!.Status);

        // Once a gateway is reachable the same commands are delivered and the
        // colour is applied.
        registry.GetOrRegister("gw-test");
        var delivered = await commands.SetNowAsync(new LedNowRequest("all", "all", 1, 2, 3), default);
        Assert.True(delivered.Accepted);
        Assert.Equal(1, delivered.Gateways);
        Assert.Contains("via 1 gateway", delivered.Message);
        Assert.Equal(1, (int)fleet.GetBalloon(0x0101)!.R);
        Assert.True(registry.TryReadCommand("gw-test", out var command));
        Assert.Equal(BalloonProtocol.VendorOpcode(BalloonProtocol.OpLedSetNow), command!.Opcode);

        var cancelled = await commands.CancelShowStepAsync(showSteps[0].Id, default);
        Assert.NotNull(cancelled);
        Assert.True(cancelled!.Accepted);
        Assert.Equal("canceled", database.Store.GetShowStep(showSteps[0].Id)!.Status);
    }

    [Fact]
    public void ZoneCommandsAreRejectedRatherThanBroadcast()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        Assert.Throws<ArgumentException>(() => fleet.ResolveTarget("group", "1"));
        Assert.Equal(BalloonProtocol.GroupLedAll, fleet.ResolveTarget("all", "all").Destination);
        Assert.Equal((ushort)0x0101, fleet.ResolveTarget("balloon", "0x0101").Destination);
    }

    [Fact]
    public void BroadcastGroupsAreSplitByReceiverRole()
    {
        Assert.Equal((ushort)0xC010, BalloonProtocol.GroupLedAll);
        Assert.Equal((ushort)0xC010, BalloonProtocol.GroupTime);
        Assert.Equal((ushort)0xC010, BalloonProtocol.GroupNeighborProbe);
        Assert.Equal((ushort)0xC011, BalloonProtocol.GroupFtmReport);
        Assert.Equal((ushort)0xC011, BalloonProtocol.GroupNeighborReport);
        Assert.Equal((ushort)0xC011, BalloonProtocol.GroupQueueStatus);
    }

    [Fact]
    public void GroupsCanBeCreatedAssignedAndDeletedWhenEmpty()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);

        var created = fleet.CreateGroup("Zone E");
        Assert.Equal((ushort)0xC104, created.MeshGroupAddress);
        Assert.Equal(0, created.BalloonCount);

        var moved = fleet.AssignBalloonToGroup(0x0101, created.Id);
        Assert.Equal(created.Id, moved.GroupId);
        Assert.Equal("Zone E", moved.GroupName);
        Assert.False(fleet.DeleteGroup(created.Id));

        fleet.AssignBalloonToGroup(0x0101, 1);
        Assert.True(fleet.DeleteGroup(created.Id));
        Assert.False(fleet.DeleteGroup(5)); // virtual all-balloons group
    }
}
