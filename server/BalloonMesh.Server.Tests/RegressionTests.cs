using System.Net.WebSockets;
using BalloonMesh.Protocol;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BalloonMesh.Server.Tests;

[Collection("PostgresDatabase")]
public sealed class RegressionTests
{
    private static (FleetService Fleet, FtmAggregator Aggregator, NeighborStore Neighbors,
        LedDispatchService Dispatch, MeshReportRouter Router, TestTimeProvider Time,
        RecordingMonitorEvents Events) CreateServices(TemporaryDatabase database)
    {
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var neighbors = new NeighborStore();
        var registry = TestRegistry.Create(events);
        var aggregator = new FtmAggregator(fleet, neighbors, new PositionSolver(), events, time,
            NullLogger<FtmAggregator>.Instance);
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time,
            NullLogger<LedDispatchService>.Instance);
        var router = new MeshReportRouter(fleet, aggregator, neighbors, dispatch, events,
            NullLogger<MeshReportRouter>.Instance);
        return (fleet, aggregator, neighbors, dispatch, router, time, events);
    }

    [Fact]
    public void UnknownBalloonQueueStatusIsIgnoredInsteadOfFailingTheBatch()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var message = new GatewayEventDto(
            0x0999,
            BalloonProtocol.GroupQueueStatus,
            BalloonProtocol.VendorOpcode(BalloonProtocol.OpQueueStatus),
            Convert.ToHexString(BalloonProtocol.EncodeQueueStatus(
                new QueueStatus(BalloonProtocol.OpQueueStatusNearFull, 401))),
            -50,
            7,
            null);

        Assert.Equal(0, services.Router.Handle(message, "gw-test"));
    }

    [Fact]
    public async Task RecoveredQueueReportDoesNotBlockFutureDispatchForever()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var registry = TestRegistry.Create(services.Events);
        registry.GetOrRegister("gw-test");
        var dispatch = new LedDispatchService(registry, services.Fleet, database.Store,
            services.Events, services.Time, NullLogger<LedDispatchService>.Instance);

        dispatch.NoteQueueStatus(0x0101, 401);
        var at = services.Time.GetUtcNow().AddMinutes(1);
        Assert.True(dispatch.EnqueueScheduled(
            services.Fleet.ResolveTarget("balloon", "0x0101"),
            BalloonProtocol.Tick40FromUnixMs(at.ToUnixTimeMilliseconds()), at, 1, 2, 3));
        Assert.Equal(0, await dispatch.TickAsync());

        // The device reports periodically, so a value at the target waits for
        // the next report instead of either blocking forever or being treated
        // as an empty queue.
        dispatch.NoteQueueStatus(0x0101, 350);
        Assert.Equal(0, await dispatch.TickAsync());

        dispatch.NoteQueueStatus(0x0101, 340);
        Assert.Equal(1, await dispatch.TickAsync());
        Assert.Equal(0, dispatch.PendingCount);
    }

    [Fact]
    public void ReportedDepthDecaysAsKnownReservationsExecute()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var at = services.Time.GetUtcNow().AddSeconds(1);
        var target = services.Fleet.ResolveTarget("balloon", "0x0101");
        Assert.Single(target.Balloons);
        services.Dispatch.SeedSentReservations(
        [
            (target, BalloonProtocol.Tick40FromUnixMs(at.ToUnixTimeMilliseconds()))
        ]);
        Assert.Equal(1, services.Dispatch.GetEstimatedCount(0x0101));
        services.Dispatch.NoteQueueStatus(0x0101, 350);
        Assert.Equal(350, services.Dispatch.GetEstimatedCount(0x0101));

        services.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(349, services.Dispatch.GetEstimatedCount(0x0101));
    }

    [Fact]
    public void CancellingASentReservationReducesTheReportedDepth()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var at = services.Time.GetUtcNow().AddMinutes(1);
        var tick = BalloonProtocol.Tick40FromUnixMs(at.ToUnixTimeMilliseconds());
        services.Dispatch.SeedSentReservations(
        [
            (services.Fleet.ResolveTarget("balloon", "0x0101"), tick)
        ]);
        services.Dispatch.NoteQueueStatus(0x0101, 10);

        services.Dispatch.NoteCancel([0x0101], tick);

        Assert.Equal(9, services.Dispatch.GetEstimatedCount(0x0101));
    }

    [Fact]
    public void CancelRemovesTheMatchingPendingReservation()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var at = services.Time.GetUtcNow().AddMinutes(2);
        var tick = BalloonProtocol.Tick40FromUnixMs(at.ToUnixTimeMilliseconds());
        Assert.True(services.Dispatch.EnqueueScheduled(
            services.Fleet.ResolveTarget("balloon", "0x0101"), tick, at, 1, 2, 3,
            showStepId: 42));

        var removed = services.Dispatch.NoteCancel([0x0101], tick);

        Assert.Equal([42L], removed);
        Assert.Equal(0, services.Dispatch.PendingCount);
    }

    [Fact]
    public void UnicastCancelDoesNotRemoveAnAllBalloonReservation()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var at = services.Time.GetUtcNow().AddMinutes(2);
        var tick = BalloonProtocol.Tick40FromUnixMs(at.ToUnixTimeMilliseconds());
        Assert.True(services.Dispatch.EnqueueScheduled(
            services.Fleet.ResolveTarget("all", "all"), tick, at, 1, 2, 3,
            showStepId: 43));

        var removed = services.Dispatch.NoteCancel([0x0101], tick);

        Assert.Empty(removed);
        Assert.Equal(1, services.Dispatch.PendingCount);
    }

    [Fact]
    public async Task CancellingAPastReservationIsRejected()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var registry = TestRegistry.Create(services.Events);
        registry.GetOrRegister("gw-test");
        var dispatch = new LedDispatchService(registry, services.Fleet, database.Store,
            services.Events, services.Time, NullLogger<LedDispatchService>.Instance);
        var commands = new CommandService(services.Fleet, dispatch, registry, database.Store,
            services.Events, services.Time, NullLogger<CommandService>.Instance);

        var result = await commands.CancelAsync(new LedCancelRequest(
            "balloon", "0x0101", services.Time.GetUtcNow().AddSeconds(-1)), default);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task NeighborOnlyBatchCanProduceAPosition()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var now = services.Time.GetUtcNow();
        Assert.True(services.Neighbors.Add(0x0101, 7,
            [new NeighborEntry(0x0102, -50)], now));
        services.Aggregator.NoteNeighborReport(0x0101, 7, now, observationsChanged: true);

        services.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await services.Aggregator.ProcessDueAsync(services.Time.GetUtcNow()));
        Assert.NotEqual("seed", services.Fleet.GetBalloon(0x0101)!.LastMethod);
    }

    [Fact]
    public async Task DuplicateNeighborReportDoesNotRecomputeTheSamePosition()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var now = services.Time.GetUtcNow();
        var entry = new NeighborEntry(0x0102, -50);
        Assert.True(services.Neighbors.Add(0x0101, 7, [entry], now));
        services.Aggregator.NoteNeighborReport(0x0101, 7, now, observationsChanged: true);
        services.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, await services.Aggregator.ProcessDueAsync(services.Time.GetUtcNow()));
        var history = services.Fleet.GetHistory(0x0101, 100).Count;

        Assert.False(services.Neighbors.Add(0x0101, 7, [entry],
            services.Time.GetUtcNow()));
        services.Aggregator.NoteNeighborReport(0x0101, 7, services.Time.GetUtcNow(),
            observationsChanged: false);

        Assert.Equal(0, await services.Aggregator.ProcessDueAsync(services.Time.GetUtcNow()));
        Assert.Equal(history, services.Fleet.GetHistory(0x0101, 100).Count);
    }

    [Fact]
    public async Task RecoverySeedsAlreadySentReservationsIntoTheEstimate()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var at = services.Time.GetUtcNow().AddMinutes(2);
        var id = database.Store.AddShowStep("sent", "balloon", "0x0101", at,
            1, 2, 3, "sent");

        var registry = TestRegistry.Create(services.Events);
        var dispatcher = new LedDispatchService(registry, services.Fleet, database.Store,
            services.Events, services.Time, NullLogger<LedDispatchService>.Instance);
        var worker = new ShowRecoveryWorker(database.Store, services.Fleet, dispatcher,
            services.Time, NullLogger<ShowRecoveryWorker>.Instance);
        Assert.Single(database.Store.GetFutureShowSteps("sent",
            services.Time.GetUtcNow()));

        Assert.Equal(0, await worker.RecoverAsync());
        Assert.Equal(1, dispatcher.GetEstimatedCount(0x0101));
        Assert.Equal("sent", database.Store.GetShowStep(id)!.Status);
    }

    [Fact]
    public async Task CancellingASentReservationMarksItCanceledForRecovery()
    {
        using var database = new TemporaryDatabase();
        var services = CreateServices(database);
        var at = services.Time.GetUtcNow().AddMinutes(2);
        var id = database.Store.AddShowStep("sent", "balloon", "0x0101", at,
            1, 2, 3, "sent");
        var registry = TestRegistry.Create(services.Events);
        registry.GetOrRegister("gw-test");
        var dispatcher = new LedDispatchService(registry, services.Fleet, database.Store,
            services.Events, services.Time, NullLogger<LedDispatchService>.Instance);
        var commands = new CommandService(services.Fleet, dispatcher, registry,
            database.Store, services.Events, services.Time,
            NullLogger<CommandService>.Instance);

        var result = await commands.CancelAsync(
            new LedCancelRequest("balloon", "0x0101", at), default);

        Assert.True(result.Accepted);
        Assert.Equal("canceled", database.Store.GetShowStep(id)!.Status);

        var recoveryDispatcher = new LedDispatchService(registry, services.Fleet,
            database.Store, services.Events, services.Time,
            NullLogger<LedDispatchService>.Instance);
        var worker = new ShowRecoveryWorker(database.Store, services.Fleet,
            recoveryDispatcher, services.Time,
            NullLogger<ShowRecoveryWorker>.Instance);
        await worker.RecoverAsync();
        Assert.Equal(0, recoveryDispatcher.GetEstimatedCount(0x0101));
    }

    [Fact]
    public void SettingAnInitialPositionMovesTheDisplayedPosition()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);

        var updated = fleet.SetInitialPosition(0x0101, 12.345, 67.890);

        Assert.Equal(12.345, updated.Lat!.Value, 6);
        Assert.Equal(67.890, updated.Lon!.Value, 6);
        Assert.Equal("initial", updated.LastMethod);

        // A real fix must not be overwritten by a later initial-position edit.
        database.Store.UpdatePosition(0x0101, 1.0, 2.0, 0.9, 1, "ftm",
            DateTimeOffset.UtcNow);
        var afterFix = fleet.SetInitialPosition(0x0101, 3.0, 4.0);
        Assert.Equal(1.0, afterFix.Lat!.Value, 6);
        Assert.Equal(2.0, afterFix.Lon!.Value, 6);
        Assert.Equal("ftm", afterFix.LastMethod);
    }

    [Fact]
    public async Task UnprovisionedWebSocketGatewayIsNotCountedAsDelivery()
    {
        var registry = TestRegistry.Create(new RecordingMonitorEvents());
        var generation = registry.AttachWebSocket("gw-test", null, "0.1.0",
            provisioned: false, spoolPending: 0, new ClientWebSocket(), new CancellationTokenSource());
        var command = new DownlinkCommand(0xC010, 1, [1], 7);

        Assert.Equal(0, registry.Broadcast(command));

        await registry.DetachWebSocketAsync("gw-test", generation);
        registry.AttachWebSocket("gw-test", null, "0.1.0", provisioned: true, spoolPending: 0, new ClientWebSocket(), new CancellationTokenSource());
        Assert.Equal(1, registry.Broadcast(command));
    }

    [Fact]
    public async Task DetachedWebSocketGatewayIsNotTreatedAsAnHttpGateway()
    {
        var registry = TestRegistry.Create(new RecordingMonitorEvents());
        var generation = registry.AttachWebSocket("gw-test", null, "0.1.0",
            provisioned: true, spoolPending: 0, new ClientWebSocket(), new CancellationTokenSource());
        var command = new DownlinkCommand(0xC010, 1, [1], 7);
        Assert.Equal(1, registry.Broadcast(command));

        await registry.DetachWebSocketAsync("gw-test", generation);

        Assert.Equal(0, registry.Broadcast(command));
    }

    [Fact]
    public void ExplicitlyUnreadyHttpGatewayIsNotCounted()
    {
        var registry = TestRegistry.Create(new RecordingMonitorEvents());
        var command = new DownlinkCommand(0xC010, 1, [1], 7);
        registry.Touch("gw-test");
        registry.UpdateRuntimeState("gw-test", provisioned: false, spoolPending: 0);
        Assert.Equal(0, registry.Broadcast(command));

        registry.UpdateRuntimeState("gw-test", provisioned: true, spoolPending: 0);
        Assert.Equal(1, registry.Broadcast(command));
    }

    [Fact]
    public void StaleWebSocketStateUpdateIsIgnored()
    {
        var registry = TestRegistry.Create(new RecordingMonitorEvents());
        var oldGeneration = registry.AttachWebSocket("gw-test", null, "0.1.0",
            provisioned: true, spoolPending: 0, new ClientWebSocket(), new CancellationTokenSource());
        registry.AttachWebSocket("gw-test", null, "0.1.0", provisioned: true,
            spoolPending: 0, new ClientWebSocket(), new CancellationTokenSource());

        registry.UpdateRuntimeState("gw-test", provisioned: false, spoolPending: 9,
            oldGeneration);

        var gateway = Assert.Single(registry.Snapshot());
        Assert.True(gateway.Provisioned);
        Assert.Equal(0, gateway.SpoolPending);
    }

    [Fact]
    public async Task StaleWebSocketDetachDoesNotClearANewerConnection()
    {
        var registry = TestRegistry.Create(new RecordingMonitorEvents());
        var oldGeneration = registry.AttachWebSocket("gw-test", null, "0.1.0",
            provisioned: true, spoolPending: 0, new ClientWebSocket(), new CancellationTokenSource());
        registry.AttachWebSocket("gw-test", null, "0.1.0", provisioned: true, spoolPending: 0, new ClientWebSocket(), new CancellationTokenSource());

        await registry.DetachWebSocketAsync("gw-test", oldGeneration);

        var gateway = Assert.Single(registry.Snapshot());
        Assert.True(gateway.WebSocket);
    }
}
