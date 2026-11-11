using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Protocol;

namespace BalloonMesh.Server.Services;

/// <summary>
/// Expands an abstract light effect into concrete per-balloon LED commands.
/// Schedules timestamped LED_SET commands ahead of execution.
/// </summary>
public sealed class EffectService : BackgroundService
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan LeadTime = TimeSpan.FromSeconds(30);

    private readonly FleetService _fleet;
    private readonly LedDispatchService _dispatch;
    private readonly IMonitorEvents _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<EffectService> _logger;
    private readonly object _gate = new();
    private EffectRun? _run;
    private EffectRun? _pendingRun;
    private EffectRun? _scheduledRun;
    private DateTimeOffset? _switchAt;
    private bool _endScheduled;
    private long _expiredBaseline;
    private string? _scheduleWarning;
    private EffectStateDto? _last;

    public EffectService(
        FleetService fleet,
        LedDispatchService dispatch,
        IMonitorEvents notifier,
        TimeProvider time,
        ILogger<EffectService> logger)
    {
        _fleet = fleet;
        _dispatch = dispatch;
        _notifier = notifier;
        _time = time;
        _logger = logger;
    }

    public EffectStateDto? Current
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    public async Task<EffectStateDto> StartAsync(EffectStartRequest request, CancellationToken cancellationToken)
    {
        var kind = NormalizeKind(request.Kind);
        var balloons = _fleet.GetBalloons();
        if (balloons.Count == 0)
        {
            throw new ArgumentException("No balloons are registered.");
        }

        BalloonDto? origin = null;
        if (!string.IsNullOrWhiteSpace(request.Origin))
        {
            if (!TryParseAddress(request.Origin, out var address))
            {
                throw new ArgumentException($"Origin address '{request.Origin}' is invalid.");
            }
            origin = balloons.FirstOrDefault(balloon => balloon.MeshAddress == address)
                ?? throw new ArgumentException($"Origin balloon '{request.Origin}' was not found.");
            if (origin.Lat is null || origin.Lon is null)
            {
                throw new ArgumentException($"Origin balloon '{request.Origin}' has no position fix.");
            }
        }

        var defaults = DefaultsFor(kind);
        var speed = Math.Clamp(request.Speed ?? defaults.Speed, 0.5, 500);
        var wavelength = Math.Clamp(request.Wavelength ?? defaults.Wavelength, kind == "pulse" ? 0.1 : 5, 5000);
        var width = Math.Clamp(request.Width ?? defaults.Width, 0.05, 1);
        var direction = NormalizeAngle(request.DirectionDeg ?? defaults.DirectionDeg);
        var frameInterval = Math.Clamp(request.FrameIntervalMs ?? 250, 100, 2000);
        var duration = Math.Clamp(request.DurationSeconds ?? 0, 0, 3600);
        if (kind != "pulse" && wavelength / speed < 0.2)
        {
            throw new ArgumentException("The effect cycle must be at least 0.2 seconds.");
        }
        var now = _time.GetUtcNow();

        var run = new EffectRun(
            kind,
            LabelFor(kind),
            origin,
            speed,
            wavelength,
            width,
            direction,
            request.R,
            request.G,
            request.B,
            request.R2,
            request.G2,
            request.B2,
            frameInterval,
            duration,
            now.Add(LeadTime))
        {
            NextFrameAt = now.Add(LeadTime)
        };

        EffectStateDto state;
        lock (_gate)
        {
            if (_run is not null)
            {
                if (_run.StopAt is not null || _scheduledRun is not null)
                {
                    throw new ArgumentException("A change or stop is already scheduled. Wait for it to take effect.");
                }
                _pendingRun = run;
                _switchAt = NextBoundary(_run, _run.LastScheduledAt);
                state = _run.ToState(running: true) with { NextKind = run.Kind, NextAt = _switchAt };
            }
            else
            {
                _run = run;
                _expiredBaseline = _dispatch.ExpiredEffectReservations;
                _scheduleWarning = null;
                state = run.ToState(running: true);
            }
            state = state with { ScheduleWarning = _scheduleWarning };
            _last = state;
        }
        _logger.LogInformation("effect {Kind} scheduled (origin={Origin}, duration={Duration}s)",
            kind, run.OriginAddress ?? "centroid", duration);
        await _notifier.EffectChangedAsync(state);
        return state;
    }

    public async Task<EffectStateDto?> StopAtCycleEndAsync(CancellationToken cancellationToken)
    {
        EffectStateDto? state;
        lock (_gate)
        {
            if (_run is null)
            {
                return _last;
            }

            var run = _run;
            if (_scheduledRun is not null)
            {
                throw new ArgumentException("A change is already scheduled. Wait for it to take effect before stopping.");
            }
            _pendingRun = null;
            _switchAt = null;
            if (run.StopAt is null)
            {
                run.StopAt = NextBoundary(run, run.LastScheduledAt);
                if (run.EndsAt is { } endsAt && endsAt < run.StopAt)
                {
                    run.StopAt = endsAt;
                }
            }
            state = run.ToState(running: true) with { ScheduleWarning = _scheduleWarning };
            _last = state;
        }
        await _notifier.EffectChangedAsync(state);
        return state;
    }

    private static DateTimeOffset NextBoundary(EffectRun run, DateTimeOffset? lastScheduled)
    {
        var elapsed = Math.Max(0, ((lastScheduled ?? run.StartedAt) - run.StartedAt).TotalSeconds);
        return run.StartedAt.AddSeconds((Math.Floor(elapsed / run.PeriodSeconds + 1e-9) + 1) * run.PeriodSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PumpInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PumpAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "effect frame failed");
            }
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        EffectStateDto? changed = null;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_run is null)
            {
                return;
            }

            if (_scheduleWarning is null && _dispatch.ExpiredEffectReservations > _expiredBaseline)
            {
                _scheduleWarning = "一部の予約が送信される前に期限切れになりました。ゲートウェイと通信状況を確認してください。";
                changed = _last! with { ScheduleWarning = _scheduleWarning };
                _last = changed;
            }

            if (_scheduledRun is not null && _switchAt is { } switchAt && now >= switchAt)
            {
                _run = _scheduledRun;
                _scheduledRun = null;
                _switchAt = null;
                changed = _run.ToState(running: true) with { ScheduleWarning = _scheduleWarning };
                _last = changed;
            }

            if (_endScheduled && _run.StopAt is { } stopAt && now >= stopAt)
            {
                changed = _run.ToState(running: false) with { ScheduleWarning = _scheduleWarning };
                _last = changed;
                _run = null;
                _endScheduled = false;
            }

            for (var generated = 0; generated < 2; generated++)
            {
                var plan = _scheduledRun ?? _run;
                if (plan is null || _endScheduled || plan.NextFrameAt > now.Add(LeadTime)) break;
                var nextBoundary = plan.StartedAt.AddSeconds((plan.LastCycle + 1) * plan.PeriodSeconds);
                if (plan.NextFrameAt >= nextBoundary)
                {
                    ScheduleBlackFrame(nextBoundary);
                    plan.LastScheduledAt = nextBoundary;
                    plan.LastCycle++;
                    plan.LastColors.Clear();
                    plan.NextFrameAt = nextBoundary.Add(PumpInterval);
                    if (plan.StopAt is { } scheduledStop && nextBoundary >= scheduledStop ||
                        plan.EndsAt is { } endsAt && nextBoundary >= endsAt && _pendingRun is null)
                    {
                        plan.StopAt = nextBoundary;
                        _endScheduled = true;
                        _last = plan.ToState(running: true) with { ScheduleWarning = _scheduleWarning };
                    }
                    else if (ReferenceEquals(plan, _run) && _pendingRun is { } next && _switchAt <= nextBoundary)
                    {
                        next.BeginAt(nextBoundary, PumpInterval);
                        _scheduledRun = next;
                        _pendingRun = null;
                    }
                }
                else
                {
                    ScheduleFrame(plan, plan.NextFrameAt);
                    plan.LastScheduledAt = plan.NextFrameAt;
                    var nextFrame = plan.NextFrameAt.AddMilliseconds(plan.FrameIntervalMs);
                    plan.NextFrameAt = nextFrame < nextBoundary ? nextFrame : nextBoundary;
                }
            }
        }
        if (changed is not null) await _notifier.EffectChangedAsync(changed);
    }

    private void ScheduleFrame(EffectRun run, DateTimeOffset executeAt)
    {
        var positioned = _fleet.GetBalloons()
            .Where(balloon => balloon.Lat is not null && balloon.Lon is not null)
            .ToArray();
        if (positioned.Length == 0)
        {
            return;
        }

        var (centerLat, centerLon) = Center(positioned);
        var (originLat, originLon) = run.OriginLat is { } oLat && run.OriginLon is { } oLon
            ? (oLat, oLon)
            : (centerLat, centerLon);
        var elapsed = Math.Max(0, (executeAt - run.StartedAt).TotalSeconds);
        var tick = BalloonProtocol.Tick40FromUnixMs(executeAt.ToUnixTimeMilliseconds());
        var allTarget = _fleet.ResolveTarget("all", "all");
        var records = allTarget.Balloons.ToDictionary(balloon => balloon.MeshAddress);
        var colours = positioned.Select(balloon => (Balloon: balloon, Colour: Evaluate(run, balloon, elapsed,
            originLat, originLon, centerLat, centerLon))).ToArray();
        if (colours.Length == records.Count && colours.All(item => records.ContainsKey(item.Balloon.MeshAddress)) &&
            colours.All(item => item.Colour == colours[0].Colour))
        {
            var colour = colours[0].Colour;
            if (colours.All(item => run.LastColors.TryGetValue(item.Balloon.MeshAddress, out var old) && old == colour)) return;
            if (_dispatch.EnqueueScheduled(allTarget, tick, executeAt, colour.R, colour.G, colour.B, isEffect: true))
            {
                foreach (var item in colours) run.LastColors[item.Balloon.MeshAddress] = colour;
            }
            else _dispatch.NoteExpiredEffectReservation();
            return;
        }

        foreach (var item in colours)
        {
            var balloon = item.Balloon;
            var (r, g, b) = item.Colour;
            if (run.LastColors.TryGetValue(balloon.MeshAddress, out var previous)
                && previous == (r, g, b))
            {
                continue;
            }

            if (records.TryGetValue(balloon.MeshAddress, out var record) &&
                _dispatch.EnqueueScheduled(new TargetSpec(balloon.MeshAddress, [record], balloon.Name),
                    tick, executeAt, r, g, b, isEffect: true))
            {
                run.LastColors[balloon.MeshAddress] = (r, g, b);
            }
            else if (executeAt <= _time.GetUtcNow().AddMilliseconds(10))
            {
                _dispatch.NoteExpiredEffectReservation();
            }
        }
    }

    private void ScheduleBlackFrame(DateTimeOffset executeAt)
    {
        var tick = BalloonProtocol.Tick40FromUnixMs(executeAt.ToUnixTimeMilliseconds());
        if (!_dispatch.EnqueueScheduled(_fleet.ResolveTarget("all", "all"), tick, executeAt, 0, 0, 0, isEffect: true))
        {
            _dispatch.NoteExpiredEffectReservation();
        }
    }

    private static (byte R, byte G, byte B) Evaluate(
        EffectRun run,
        BalloonDto balloon,
        double elapsed,
        double originLat,
        double originLon,
        double centerLat,
        double centerLon)
    {
        switch (run.Kind)
        {
            case "flow":
            {
                var distance = DistanceMeters(originLat, originLon, balloon.Lat!.Value, balloon.Lon!.Value);
                var phase = distance / run.Wavelength - elapsed * run.Speed / run.Wavelength;
                return Blend(run, Trail(phase, run.Width));
            }
            case "wave":
            {
                var (dx, dy) = OffsetMeters(centerLat, centerLon, balloon.Lat!.Value, balloon.Lon!.Value);
                var radians = run.DirectionDeg * Math.PI / 180.0;
                var projection = dx * Math.Cos(radians) + dy * Math.Sin(radians);
                var phase = projection / run.Wavelength - elapsed * run.Speed / run.Wavelength;
                return Blend(run, Trail(phase, run.Width));
            }
            case "pulse":
            {
                var period = Math.Max(0.2, run.Wavelength / Math.Max(0.5, run.Speed));
                var value = 0.5 - 0.5 * Math.Cos(2 * Math.PI * elapsed / period);
                return Blend(run, value);
            }
            case "solid":
                return Blend(run, 1);
            case "twinkle":
            case "alternate":
            {
                var period = run.Wavelength / run.Speed;
                var offset = run.Kind == "twinkle"
                    ? (balloon.MeshAddress * 37 % 97) / 97.0
                    : (balloon.MeshAddress % 2) / 2.0;
                var value = 0.5 + 0.5 * Math.Cos(2 * Math.PI * (elapsed / period + offset));
                return Blend(run, value);
            }
            case "rainbow":
            {
                var distance = DistanceMeters(originLat, originLon, balloon.Lat!.Value, balloon.Lon!.Value);
                var hue = elapsed * run.Speed / run.Wavelength + distance / run.Wavelength;
                hue -= Math.Floor(hue);
                var (r, g, b) = HsvToRgb(hue, 0.9, 1.0);
                return (RoundByte(r * 0.85), RoundByte(g * 0.85), RoundByte(b * 0.85));
            }
            default:
                return (0, 0, 0);
        }
    }

    /// <summary>Comet-shaped band: bright at the crest with a fading tail.</summary>
    private static double Trail(double phase, double width)
    {
        var fraction = phase - Math.Floor(phase);
        if (fraction >= width)
        {
            return 0;
        }
        var x = 1 - fraction / width;
        return x * x * (3 - 2 * x);
    }

    private static (byte R, byte G, byte B) Blend(EffectRun run, double value)
    {
        value = Math.Clamp(value, 0, 1);
        var r = (run.R2 + (run.R - run.R2) * value) * value;
        var g = (run.G2 + (run.G - run.G2) * value) * value;
        var b = (run.B2 + (run.B - run.B2) * value) * value;
        return (RoundByte(r), RoundByte(g), RoundByte(b));
    }

    /// <summary>Matches JavaScript's Math.round (midpoints round away from zero).</summary>
    private static byte RoundByte(double value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 255), MidpointRounding.AwayFromZero);

    private static (double R, double G, double B) HsvToRgb(double hue, double saturation, double value)
    {
        var sector = hue * 6.0;
        var index = (int)Math.Floor(sector) % 6;
        var fraction = sector - Math.Floor(sector);
        var p = value * (1 - saturation);
        var q = value * (1 - saturation * fraction);
        var t = value * (1 - saturation * (1 - fraction));
        return index switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
    }

    private static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var (dx, dy) = OffsetMeters(lat1, lon1, lat2, lon2);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static (double Dx, double Dy) OffsetMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var meanLat = (lat1 + lat2) * 0.5 * Math.PI / 180.0;
        var dx = (lon2 - lon1) * 111_320.0 * Math.Cos(meanLat);
        var dy = (lat2 - lat1) * 111_320.0;
        return (dx, dy);
    }

    private static (double Lat, double Lon) Center(IReadOnlyList<BalloonDto> balloons) =>
        (balloons.Average(balloon => balloon.Lat!.Value),
         balloons.Average(balloon => balloon.Lon!.Value));

    private static string NormalizeKind(string kind) =>
        kind.Trim().ToLowerInvariant() switch
        {
            "flow" or "ripple" or "stream" => "flow",
            "wave" or "sweep" => "wave",
            "pulse" or "breathe" => "pulse",
            "rainbow" or "hue" => "rainbow",
            "twinkle" or "alternate" or "solid" => kind.Trim().ToLowerInvariant(),
            _ => throw new ArgumentException(
                $"Unknown effect kind '{kind}'. Use flow, wave, pulse, rainbow, twinkle, alternate or solid.")
        };

    private static string LabelFor(string kind) => kind switch
    {
        "flow" => "Flow from node",
        "wave" => "Wave sweep",
        "pulse" => "Synchronised pulse",
        "rainbow" => "Rainbow drift",
        "twinkle" => "Twinkle",
        "alternate" => "Alternating",
        "solid" => "Steady light",
        _ => kind
    };

    private static (double Speed, double Wavelength, double Width, int DirectionDeg) DefaultsFor(string kind) =>
        kind switch
        {
            "flow" => (55, 110, 0.5, 0),
            "wave" => (45, 150, 0.35, 90),
            "pulse" => (1, 3, 1, 0),
            "rainbow" => (25, 240, 1, 0),
            "twinkle" or "alternate" => (1, 6, 1, 0),
            "solid" => (1, 10, 1, 0),
            _ => (45, 150, 0.35, 0)
        };

    private static int NormalizeAngle(int value)
    {
        var normalized = value % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static bool TryParseAddress(string text, out ushort address)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out address);
        }
        return ushort.TryParse(text, out address);
    }

    private sealed class EffectRun
    {
        public EffectRun(
            string kind,
            string label,
            BalloonDto? origin,
            double speed,
            double wavelength,
            double width,
            int directionDeg,
            byte r,
            byte g,
            byte b,
            byte r2,
            byte g2,
            byte b2,
            int frameIntervalMs,
            double durationSeconds,
            DateTimeOffset startedAt)
        {
            Kind = kind;
            Label = label;
            OriginAddress = origin is null ? null : $"0x{origin.MeshAddress:X4}";
            OriginName = origin?.Name;
            OriginLat = origin?.Lat;
            OriginLon = origin?.Lon;
            Speed = speed;
            Wavelength = wavelength;
            Width = width;
            DirectionDeg = directionDeg;
            R = r;
            G = g;
            B = b;
            R2 = r2;
            G2 = g2;
            B2 = b2;
            FrameIntervalMs = frameIntervalMs;
            DurationSeconds = durationSeconds;
            StartedAt = startedAt;
            var period = Math.Max(kind == "pulse" ? 0.2 : 0, wavelength / speed);
            EndsAt = durationSeconds > 0 ? startedAt.AddSeconds(Math.Ceiling(durationSeconds / period) * period) : null;
        }

        public string Kind { get; }
        public string Label { get; }
        public string? OriginAddress { get; }
        public string? OriginName { get; }
        public double? OriginLat { get; }
        public double? OriginLon { get; }
        public double Speed { get; }
        public double Wavelength { get; }
        public double Width { get; }
        public int DirectionDeg { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public byte R2 { get; }
        public byte G2 { get; }
        public byte B2 { get; }
        public int FrameIntervalMs { get; }
        public double DurationSeconds { get; }
        public DateTimeOffset StartedAt { get; private set; }
        public DateTimeOffset? EndsAt { get; private set; }
        public DateTimeOffset NextFrameAt { get; set; }
        public DateTimeOffset? LastScheduledAt { get; set; }
        public double PeriodSeconds => Math.Max(Kind == "pulse" ? 0.2 : 0, Wavelength / Speed);
        public long LastCycle { get; set; }
        public DateTimeOffset? StopAt { get; set; }
        public Dictionary<ushort, (byte R, byte G, byte B)> LastColors { get; } = [];

        public void BeginAt(DateTimeOffset now, TimeSpan blackout)
        {
            StartedAt = now;
            EndsAt = DurationSeconds > 0 ? now.AddSeconds(Math.Ceiling(DurationSeconds / PeriodSeconds) * PeriodSeconds) : null;
            NextFrameAt = now.Add(blackout);
            LastScheduledAt = now;
        }

        public EffectStateDto ToState(bool running) => new(
            Kind,
            Label,
            running,
            OriginAddress,
            OriginName,
            Speed,
            Wavelength,
            Width,
            DirectionDeg,
            R,
            G,
            B,
            R2,
            G2,
            B2,
            FrameIntervalMs,
            DurationSeconds,
            StartedAt,
            EndsAt,
            StopAt);
    }
}
