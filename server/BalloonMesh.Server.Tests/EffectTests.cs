using BalloonMesh.Server.Models;
using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BalloonMesh.Server.Tests;

[Collection("PostgresDatabase")]
public sealed class EffectTests
{
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
        Assert.True(state.DurationSeconds > 0);
        Assert.Single(events.Effects);
        Assert.True(events.Effects[0]!.Running);

        var stopped = await effects.StopAsync(clear: false, default);

        Assert.NotNull(stopped);
        Assert.False(stopped!.Running);
        Assert.False(effects.Current!.Running);
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
