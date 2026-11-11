using System.Reflection;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Protocol;
using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BalloonMesh.Server.Tests;

[Collection("PostgresDatabase")]
public sealed class EffectTests
{
    [Fact]
    public async Task StartsThirtySecondsAheadAndRefillsGradually()
    {
        using var h = new Harness();
        var state = await h.Effects.StartAsync(new EffectStartRequest("flow", Speed: 50, Wavelength: 100), default);
        Assert.Equal(h.Now.AddSeconds(30), state.StartedAt);
        Assert.Equal(0, state.DurationSeconds);
        var first = await h.PumpAndDispatch();
        Assert.Equal(50, first.Count);
        Assert.All(first, c =>
        {
            Assert.Equal(BalloonProtocol.VendorOpcode(BalloonProtocol.OpLedSet), c.Opcode);
            Assert.Equal(BalloonProtocol.Tick40FromUnixMs(state.StartedAt.ToUnixTimeMilliseconds()), Tick(c));
        });
        Assert.Empty(await h.PumpAndDispatch());
        h.Advance(TimeSpan.FromMilliseconds(250));
        var next = await h.PumpAndDispatch();
        Assert.NotEmpty(next);
        Assert.All(next, c => Assert.Equal(BalloonProtocol.Tick40FromUnixMs(state.StartedAt.AddMilliseconds(250).ToUnixTimeMilliseconds()), Tick(c)));
    }

    [Fact]
    public async Task ExpiredEffectReservationsAreReported()
    {
        using var h = new Harness(withGateway: false);
        await h.Effects.StartAsync(new EffectStartRequest("flow"), default);
        await h.PumpAndDispatch();
        h.Advance(TimeSpan.FromSeconds(31));
        await h.PumpAndDispatch();
        await h.PumpAndDispatch();
        Assert.NotNull(h.Effects.Current!.ScheduleWarning);
        Assert.Contains("期限切れ", h.Effects.Current.ScheduleWarning);
    }

    [Theory]
    [InlineData("flow")]
    [InlineData("wave")]
    [InlineData("pulse")]
    [InlineData("rainbow")]
    [InlineData("twinkle")]
    [InlineData("alternate")]
    [InlineData("solid")]
    public async Task EachCycleSchedulesBlackoutThenResumes(string kind)
    {
        using var h = new Harness();
        var state = await h.Effects.StartAsync(new EffectStartRequest(kind, Speed: 50, Wavelength: 100), default);
        var sent = await h.ScheduleUntil(TimeSpan.FromSeconds(2));
        var boundaryTick = BalloonProtocol.Tick40FromUnixMs(state.StartedAt.AddSeconds(2).ToUnixTimeMilliseconds());
        var blackout = Assert.Single(sent, c => c.Dst == BalloonProtocol.GroupLedAll && Tick(c) == boundaryTick);
        Assert.Equal(boundaryTick, Tick(blackout));
        Assert.Equal(new byte[] { 0, 0, 0 }, blackout.Payload[5..8]);
        h.Advance(TimeSpan.FromMilliseconds(40));
        var resumed = await h.PumpAndDispatch();
        Assert.NotEmpty(resumed);
        Assert.All(resumed, c => Assert.Equal(BalloonProtocol.Tick40FromUnixMs(state.StartedAt.AddSeconds(2).AddMilliseconds(40).ToUnixTimeMilliseconds()), Tick(c)));
        Assert.True(h.Effects.Current!.Running);
    }

    [Fact]
    public async Task ContinuouslyRefillsReservationsThirtySecondsAhead()
    {
        using var h = new Harness();
        await h.Effects.StartAsync(new EffectStartRequest("pulse"), default);
        var sent = await h.ScheduleUntil(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(sent);
        var latest = sent.Max(Tick);
        var lead = BalloonProtocol.UnixMsFromTick40(latest) - h.Now.ToUnixTimeMilliseconds();
        Assert.InRange(lead, 29_700, 30_000);
        Assert.True(h.Effects.Current!.Running);
    }

    [Theory]
    [InlineData("flow")]
    [InlineData("pulse")]
    public async Task ChangesWaitForAlreadyScheduledCycle(string nextKind)
    {
        using var h = new Harness();
        var initial = await h.Effects.StartAsync(new EffectStartRequest("flow", Speed: 50, Wavelength: 100), default);
        await h.ScheduleUntil(TimeSpan.FromSeconds(1));
        var requested = await h.Effects.StartAsync(new EffectStartRequest(nextKind, R: 255, G: 0, B: 0), default);
        Assert.Equal("flow", requested.Kind);
        Assert.Equal(nextKind, requested.NextKind);
        Assert.Equal(initial.StartedAt.AddSeconds(2), requested.NextAt);
        var remaining = await h.ScheduleUntil(TimeSpan.FromSeconds(2));
        Assert.Single(remaining, c => c.Dst == BalloonProtocol.GroupLedAll && Tick(c) == BalloonProtocol.Tick40FromUnixMs(requested.NextAt!.Value.ToUnixTimeMilliseconds()));
        Assert.Equal("flow", h.Effects.Current!.Kind);
        h.Advance(TimeSpan.FromMilliseconds(40));
        var newCommands = await h.PumpAndDispatch();
        Assert.NotEmpty(newCommands);
        Assert.All(newCommands, c => Assert.Equal(BalloonProtocol.Tick40FromUnixMs(requested.NextAt!.Value.AddMilliseconds(40).ToUnixTimeMilliseconds()), Tick(c)));
        Assert.Equal("flow", h.Effects.Current!.Kind);
        h.Advance(requested.NextAt!.Value - h.Now);
        await h.PumpAndDispatch();
        Assert.Equal(nextKind, h.Effects.Current!.Kind);
        Assert.Equal(255, h.Effects.Current.R);
        Assert.Null(h.Effects.Current.NextKind);
    }

    [Fact]
    public async Task StopWaitsForCommittedCycleAndDoesNotSendImmediateCommand()
    {
        using var h = new Harness();
        var initial = await h.Effects.StartAsync(new EffectStartRequest("flow", Speed: 50, Wavelength: 100), default);
        await h.ScheduleUntil(TimeSpan.FromSeconds(1));
        var scheduled = await h.Effects.StopAtCycleEndAsync(default);
        Assert.Equal(initial.StartedAt.AddSeconds(2), scheduled!.StopAt);
        Assert.Equal(scheduled.StopAt, (await h.Effects.StopAtCycleEndAsync(default))!.StopAt);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Effects.StartAsync(new EffectStartRequest("wave"), default));
        var commands = await h.ScheduleUntil(TimeSpan.FromSeconds(3));
        var black = Assert.Single(commands, c => c.Dst == BalloonProtocol.GroupLedAll && Tick(c) == BalloonProtocol.Tick40FromUnixMs(scheduled.StopAt!.Value.ToUnixTimeMilliseconds()));
        Assert.Equal(BalloonProtocol.Tick40FromUnixMs(scheduled.StopAt!.Value.ToUnixTimeMilliseconds()), Tick(black));
        Assert.All(commands, c => Assert.Equal(BalloonProtocol.VendorOpcode(BalloonProtocol.OpLedSet), c.Opcode));
        Assert.True(h.Effects.Current!.Running);
        h.Advance(scheduled.StopAt.Value - h.Now);
        Assert.Empty(await h.PumpAndDispatch());
        Assert.False(h.Effects.Current!.Running);
        var restarted = await h.Effects.StartAsync(new EffectStartRequest("wave"), default);
        Assert.Equal(h.Now.AddSeconds(30), restarted.StartedAt);
    }

    [Theory]
    [InlineData("solid")]
    [InlineData("alternate")]
    [InlineData("twinkle")]
    public async Task AddedEffectsProduceExpectedNodeColours(string kind)
    {
        using var h = new Harness();
        await h.Effects.StartAsync(new EffectStartRequest(kind, R: 255, G: 0, B: 0), default);
        var colours = (await h.PumpAndDispatch()).ToDictionary(c => c.Dst, c => c.Payload[5..8]);
        if (kind == "solid")
        {
            Assert.Single(colours);
            Assert.Equal(new byte[] { 255, 0, 0 }, colours[BalloonProtocol.GroupLedAll]);
        }
        else if (kind == "alternate")
        {
            Assert.Equal(new byte[] { 0, 0, 0 }, colours[0x0101]);
            Assert.Equal(new byte[] { 255, 0, 0 }, colours[0x0102]);
        }
        else Assert.NotEqual(colours[0x0101][0], colours[0x0102][0]);
    }

    [Fact]
    public async Task ValidationAndDefaultsArePreserved()
    {
        using var h = new Harness();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Effects.StartAsync(new EffectStartRequest("sparkle"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Effects.StartAsync(new EffectStartRequest("flow", Origin: "0x7FFF"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Effects.StartAsync(new EffectStartRequest("wave", Speed: 500, Wavelength: 5), default));
        var state = await h.Effects.StartAsync(new EffectStartRequest("flow", Origin: "0x0101"), default);
        Assert.Equal("B-01", state.OriginName);
        Assert.Equal("0x0101", state.OriginAddress);
        Assert.Equal(0, state.DurationSeconds);
        Assert.Null(state.EndsAt);
    }

    [Fact]
    public async Task PulseUsesTheRequestedShortPeriod()
    {
        using var h = new Harness();
        var state = await h.Effects.StartAsync(new EffectStartRequest("pulse", Speed: 1, Wavelength: 3), default);
        Assert.Equal(3, state.Wavelength);
        var commands = await h.ScheduleUntil(TimeSpan.FromSeconds(3));
        var tick = BalloonProtocol.Tick40FromUnixMs(state.StartedAt.AddSeconds(3).ToUnixTimeMilliseconds());
        var blackout = Assert.Single(commands, c => c.Dst == BalloonProtocol.GroupLedAll && Tick(c) == tick);
        Assert.Equal(new byte[] { 0, 0, 0 }, blackout.Payload[5..8]);
    }

    private static ulong Tick(DownlinkCommand command) => BalloonProtocol.DecodeTick40(command.Payload.AsSpan(0, 5));

    private sealed class Harness : IDisposable
    {
        private readonly TemporaryDatabase _database = new();
        private readonly TestTimeProvider _time = new(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        private readonly LedDispatchService _dispatch;
        private readonly GatewayClient _gateway;
        private readonly MethodInfo _pump = typeof(EffectService).GetMethod("PumpAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly DateTimeOffset _start;
        public EffectService Effects { get; }
        public DateTimeOffset Now => _time.GetUtcNow();

        public Harness(bool withGateway = true)
        {
            _start = Now;
            var fleet = new FleetService(_database.Store);
            var events = new RecordingMonitorEvents();
            var registry = new GatewayRegistry(events);
            _gateway = withGateway ? registry.GetOrRegister("test") : new GatewayClient("unconnected");
            _dispatch = new LedDispatchService(registry, fleet, _database.Store, events, _time, NullLogger<LedDispatchService>.Instance);
            Effects = new EffectService(fleet, _dispatch, events, _time, NullLogger<EffectService>.Instance);
        }

        public void Advance(TimeSpan amount) => _time.Advance(amount);
        public async Task<List<DownlinkCommand>> PumpAndDispatch()
        {
            await (Task)_pump.Invoke(Effects, [CancellationToken.None])!;
            await _dispatch.TickAsync();
            var result = new List<DownlinkCommand>();
            while (_gateway.Commands.Reader.TryRead(out var command)) result.Add(command);
            return result;
        }

        public async Task<List<DownlinkCommand>> ScheduleUntil(TimeSpan offset)
        {
            var result = new List<DownlinkCommand>();
            do
            {
                result.AddRange(await PumpAndDispatch());
                if (Now >= _start + offset) break;
                Advance(TimeSpan.FromMilliseconds(250));
            } while (true);
            return result;
        }
        public void Dispose() => _database.Dispose();
    }
}
