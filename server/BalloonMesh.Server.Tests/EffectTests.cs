using BalloonMesh.Server.Models;
using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace BalloonMesh.Server.Tests;

[Collection("PostgresDatabase")]
public sealed class EffectTests
{
    [Theory]
    [InlineData("flow")]
    [InlineData("pulse")]
    public async Task ChangedColourAndEffectTakeEffectAfterBlackout(string nextKind)
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = new GatewayRegistry(events);
        var gateway = registry.GetOrRegister("test");
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time,
            NullLogger<LedDispatchService>.Instance);
        var effects = new EffectService(fleet, dispatch, registry, events, time,
            NullLogger<EffectService>.Instance);
        await effects.StartAsync(new EffectStartRequest("flow", Speed: 50, Wavelength: 100), default);
        time.Advance(TimeSpan.FromSeconds(1));
        var queued = await effects.StartAsync(new EffectStartRequest(nextKind, R: 255, G: 0, B: 0), default);
        Assert.Equal("flow", queued.Kind);
        Assert.Equal(nextKind, queued.NextKind);
        Assert.Equal(0, gateway.PendingCommands);
        var pump = typeof(EffectService).GetMethod("PumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        time.Advance(TimeSpan.FromSeconds(1));
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;
        Assert.Equal(nextKind, effects.Current!.Kind);
        Assert.Equal(255, effects.Current.R);
        Assert.Null(effects.Current.NextKind);
        var commands = new List<DownlinkCommand>();
        while (gateway.Commands.Reader.TryRead(out var command)) commands.Add(command);
        Assert.Equal(fleet.GetBalloons().Count, commands.Count);
        Assert.All(commands, command => Assert.Equal(new byte[] { 0, 0, 0 }, command.Payload[5..8]));
        time.Advance(TimeSpan.FromMilliseconds(40));
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;
        Assert.True(gateway.PendingCommands > 0);
    }

    [Theory]
    [InlineData("flow")]
    [InlineData("wave")]
    [InlineData("pulse")]
    [InlineData("rainbow")]
    public async Task EachCycleClearsThenResumesWithoutAStopRequest(string kind)
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = new GatewayRegistry(events);
        var gateway = registry.GetOrRegister("test");
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time,
            NullLogger<LedDispatchService>.Instance);
        var effects = new EffectService(fleet, dispatch, registry, events, time,
            NullLogger<EffectService>.Instance);
        await effects.StartAsync(new EffectStartRequest(kind, Speed: 50, Wavelength: 100, DurationSeconds: 0), default);
        var pump = typeof(EffectService).GetMethod("PumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;
        while (gateway.Commands.Reader.TryRead(out _)) { }

        time.Advance(TimeSpan.FromSeconds(2));
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;
        var black = new List<DownlinkCommand>();
        while (gateway.Commands.Reader.TryRead(out var command)) black.Add(command);
        Assert.Equal(fleet.GetBalloons().Count, black.Count);
        Assert.All(black, command => Assert.Equal(new byte[] { 0, 0, 0 }, command.Payload[5..8]));
        Assert.True(effects.Current!.Running);
        time.Advance(TimeSpan.FromMilliseconds(20));
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;
        Assert.Equal(0, gateway.PendingCommands);
        time.Advance(TimeSpan.FromMilliseconds(20));
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;
        Assert.True(gateway.PendingCommands > 0);
        Assert.True(effects.Current!.Running);
    }

    [Theory]
    [InlineData("flow")]
    [InlineData("wave")]
    [InlineData("pulse")]
    [InlineData("rainbow")]
    public async Task ScheduledStopWaitsForCycleThenClearsAllNodes(string kind)
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = new GatewayRegistry(events);
        var gateway = registry.GetOrRegister("test");
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time,
            NullLogger<LedDispatchService>.Instance);
        var effects = new EffectService(fleet, dispatch, registry, events, time,
            NullLogger<EffectService>.Instance);
        var started = await effects.StartAsync(new EffectStartRequest(kind, Speed: 50,
            Wavelength: 100, DurationSeconds: 0), default);
        time.Advance(TimeSpan.FromMilliseconds(500));

        var scheduled = await effects.StopAtCycleEndAsync(default);
        Assert.True(scheduled!.Running);
        Assert.Equal(started.StartedAt.AddSeconds(2), scheduled.StopAt);
        Assert.Equal(scheduled.StopAt, (await effects.StopAtCycleEndAsync(default))!.StopAt);

        var pump = typeof(EffectService).GetMethod("PumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;
        Assert.True(effects.Current!.Running);
        while (gateway.Commands.Reader.TryRead(out _)) { }
        time.Advance(TimeSpan.FromMilliseconds(1500));
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;

        Assert.False(effects.Current!.Running);
        Assert.Null(effects.Current.StopAt);
        var commands = new List<DownlinkCommand>();
        while (gateway.Commands.Reader.TryRead(out var command)) commands.Add(command);
        Assert.Equal(fleet.GetBalloons().Count, commands.Count);
        Assert.All(commands, command => Assert.Equal(new byte[] { 0, 0, 0 }, command.Payload[5..8]));
    }

    [Fact]
    public async Task NewEffectWaitsUntilScheduledStopCompletes()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = new GatewayRegistry(events);
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time,
            NullLogger<LedDispatchService>.Instance);
        var effects = new EffectService(fleet, dispatch, registry, events, time,
            NullLogger<EffectService>.Instance);
        await effects.StartAsync(new EffectStartRequest("flow", Speed: 50, Wavelength: 100, DurationSeconds: 0), default);
        await effects.StopAtCycleEndAsync(default);
        await Assert.ThrowsAsync<ArgumentException>(() => effects.StartAsync(new EffectStartRequest("wave", DurationSeconds: 0), default));
        time.Advance(TimeSpan.FromSeconds(3));
        var pump = typeof(EffectService).GetMethod("PumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)pump.Invoke(effects, [CancellationToken.None])!;
        Assert.False(effects.Current!.Running);
        await effects.StartAsync(new EffectStartRequest("wave", DurationSeconds: 0), default);
        Assert.True(effects.Current!.Running);
        Assert.Equal("wave", effects.Current.Kind);
        Assert.Null(effects.Current.StopAt);
    }

    [Fact]
    public async Task ExpirationOfPreviousRunDoesNotStopOrClearItsReplacement()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = new GatewayRegistry(events);
        var gateway = registry.GetOrRegister("test");
        var dispatch = new LedDispatchService(registry, fleet, database.Store, events, time,
            NullLogger<LedDispatchService>.Instance);
        var effects = new EffectService(fleet, dispatch, registry, events, time,
            NullLogger<EffectService>.Instance);

        await effects.StartAsync(new EffectStartRequest("flow", DurationSeconds: 1, ClearOnStop: true), default);
        var oldRun = typeof(EffectService).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(effects);
        var stop = typeof(EffectService).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "StopAsync");
        await (Task<EffectStateDto?>)stop.Invoke(effects, [false, oldRun])!;
        await effects.StartAsync(new EffectStartRequest("wave"), default);
        var result = await (Task<EffectStateDto?>)stop.Invoke(effects, [true, oldRun])!;

        Assert.Null(result);
        Assert.True(effects.Current!.Running);
        Assert.Equal("wave", effects.Current.Kind);
        Assert.Equal(0, gateway.PendingCommands);
        Assert.Equal(3, events.Effects.Count);
    }

    [Fact]
    public async Task FlowStartsFromTheSelectedNodeAndStops()
    {
        using var database = new TemporaryDatabase();
        var effects = CreateService(database, out var events);

        var state = await effects.StartAsync(
            new EffectStartRequest("flow", Origin: "0x0101"), default);

        Assert.True(state.Running);
        Assert.Equal("flow", state.Kind);
        Assert.Equal("0x0101", state.OriginAddress);
        Assert.Equal("B-01", state.OriginName);
        Assert.Equal(0, state.DurationSeconds);
        Assert.Single(events.Effects);
        Assert.True(events.Effects[0]!.Running);

        var stopped = await effects.StopAtCycleEndAsync(default);

        Assert.NotNull(stopped);
        Assert.True(stopped!.Running);
        Assert.NotNull(stopped.StopAt);
        Assert.True(effects.Current!.Running);
        Assert.Equal(2, events.Effects.Count);
    }

    [Fact]
    public async Task WaveUsesItsOwnDefaultsAndClampsValues()
    {
        using var database = new TemporaryDatabase();
        var effects = CreateService(database, out _);

        var state = await effects.StartAsync(
            new EffectStartRequest("wave", Speed: 9000, Wavelength: 1, Width: 0.001,
                DirectionDeg: -90, DurationSeconds: 0), default);

        Assert.Equal(270, state.DirectionDeg);
        Assert.Equal(500, state.Speed);
        Assert.Equal(5, state.Wavelength);
        Assert.Equal(0.05, state.Width);
        Assert.Equal(0, state.DurationSeconds);
        Assert.Null(state.EndsAt);
    }

    [Fact]
    public async Task UnknownKindAndUnknownOriginAreRejected()
    {
        using var database = new TemporaryDatabase();
        var effects = CreateService(database, out var events);

        await Assert.ThrowsAsync<ArgumentException>(
            () => effects.StartAsync(new EffectStartRequest("sparkle"), default));
        await Assert.ThrowsAsync<ArgumentException>(
            () => effects.StartAsync(new EffectStartRequest("flow", Origin: "0x7FFF"), default));
        Assert.Empty(events.Effects);
        Assert.Null(effects.Current);
    }

    private static EffectService CreateService(TemporaryDatabase database,
        out RecordingMonitorEvents events)
    {
        var fleet = new FleetService(database.Store);
        events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var registry = new GatewayRegistry(events);
        var dispatch = new LedDispatchService(
            registry, fleet, database.Store, events, time,
            NullLogger<LedDispatchService>.Instance);
        return new EffectService(
            fleet, dispatch, registry, events, time,
            NullLogger<EffectService>.Instance);
    }
}
