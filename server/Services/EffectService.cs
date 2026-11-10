using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Protocol;

namespace BalloonMesh.Server.Services;

/// <summary>
/// Expands an abstract light effect into concrete per-balloon LED commands.
/// The firmware only needs the existing LED_SET_NOW opcode: the server samples
/// the effect at a fixed frame interval and streams the resulting colours.
/// </summary>
public sealed class EffectService : BackgroundService
{
    private const byte LedOpcode = BalloonProtocol.OpLedSetNow;
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(40);

    private readonly FleetService _fleet;
    private readonly LedDispatchService _dispatch;
    private readonly GatewayRegistry _gateways;
    private readonly IMonitorEvents _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<EffectService> _logger;
    private readonly object _gate = new();
    private EffectRun? _run;
    private EffectRun? _pendingRun;
    private EffectStateDto? _last;

    public EffectService(
        FleetService fleet,
        LedDispatchService dispatch,
        GatewayRegistry gateways,
        IMonitorEvents notifier,
        TimeProvider time,
        ILogger<EffectService> logger)
    {
        _fleet = fleet;
        _dispatch = dispatch;
        _gateways = gateways;
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
        }

        var defaults = DefaultsFor(kind);
        var speed = Math.Clamp(request.Speed ?? defaults.Speed, 0.5, 500);
        var wavelength = Math.Clamp(request.Wavelength ?? defaults.Wavelength, 5, 5000);
        var width = Math.Clamp(request.Width ?? defaults.Width, 0.05, 1);
        var direction = NormalizeAngle(request.DirectionDeg ?? defaults.DirectionDeg);
        var frameInterval = Math.Clamp(request.FrameIntervalMs ?? 250, 100, 2000);
        var duration = Math.Clamp(request.DurationSeconds ?? 0, 0, 3600);
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
            request.ClearOnStop,
            now)
        {
            NextFrameAt = now
        };

        EffectStateDto state;
        lock (_gate)
        {
            if (_run is not null)
            {
                if (_run.StopAt is not null)
                {
                    throw new ArgumentException("The effect is stopping. Wait until it has stopped.");
                }
                _pendingRun = run;
                state = _run.ToState(running: true) with { NextKind = run.Kind };
            }
            else
            {
                _run = run;
                state = run.ToState(running: true);
            }
            _last = state;
        }
        _logger.LogInformation("effect {Kind} started (origin={Origin}, duration={Duration}s)",
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
            _pendingRun = null;
            if (run.StopAt is null)
            {
                var period = run.PeriodSeconds;
                var now = _time.GetUtcNow();
                var elapsed = Math.Max(0, (now - run.StartedAt).TotalSeconds);
                var cycles = Math.Floor(elapsed / period) + 1;
                run.StopAt = run.StartedAt.AddSeconds(cycles * period);
                if (run.EndsAt is { } endsAt && endsAt < run.StopAt)
                {
                    run.StopAt = endsAt;
                }
            }
            state = run.ToState(running: true);
            _last = state;
        }
        await _notifier.EffectChangedAsync(state);
        return state;
    }

    private async Task<EffectStateDto?> StopAsync(bool clear, EffectRun? expectedRun)
    {
        EffectStateDto? state;
        lock (_gate)
        {
            if (expectedRun is not null && !ReferenceEquals(_run, expectedRun))
            {
                return null;
            }
            _run = null;
            _pendingRun = null;
            state = _last is null ? null : _last with { Running = false, StopAt = null, NextKind = null };
            _last = state;
            if (state is not null && clear)
            {
                SendBlackFrame();
            }
        }

        if (state is null)
        {
            return null;
        }

        _logger.LogInformation("effect {Kind} stopped", state.Kind);
        await _notifier.EffectChangedAsync(state);
        return state;
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
        EffectRun? run;
        DateTimeOffset? scheduledStop;
        bool hasPending;
        lock (_gate)
        {
            run = _run;
            scheduledStop = run?.StopAt;
            hasPending = _pendingRun is not null;
        }
        if (run is null)
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (scheduledStop is { } stopAt && now >= stopAt)
        {
            await StopAsync(clear: true, run);
            return;
        }
        if (!hasPending && run.EndsAt is { } endsAt && now >= endsAt)
        {
            await StopAsync(clear: true, run);
            return;
        }

        EffectStateDto? changed = null;
        lock (_gate)
        {
            if (!ReferenceEquals(_run, run))
            {
                return;
            }
            var elapsed = Math.Max(0, (now - run.StartedAt).TotalSeconds);
            var cycle = (long)Math.Floor(elapsed / run.PeriodSeconds);
            if (run.InBlackout)
            {
                if (now >= run.NextFrameAt)
                {
                    run.InBlackout = false;
                    run.LastCycle = cycle;
                    run.NextFrameAt = now.AddMilliseconds(run.FrameIntervalMs);
                    RenderFrame(run, now);
                }
            }
            else if (cycle > run.LastCycle)
            {
                run.LastCycle = cycle;
                SendBlackFrame();
                run.LastColors.Clear();
                run.NextFrameAt = now.Add(PumpInterval);
                run.InBlackout = true;
                if (_pendingRun is { } next)
                {
                    next.BeginAt(now, PumpInterval);
                    _run = next;
                    _pendingRun = null;
                    changed = next.ToState(running: true);
                    _last = changed;
                }
            }
            else if (now >= run.NextFrameAt)
            {
                run.NextFrameAt = now.AddMilliseconds(run.FrameIntervalMs);
                RenderFrame(run, now);
            }
        }
        if (changed is not null) await _notifier.EffectChangedAsync(changed);
    }

    private void RenderFrame(EffectRun run, DateTimeOffset now)
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
        var elapsed = Math.Max(0, (now - run.StartedAt).TotalSeconds);
        var addresses = positioned.Select(balloon => balloon.MeshAddress).ToArray();
        var tick = _dispatch.NextIssuedTick(addresses);
        var opcode = BalloonProtocol.VendorOpcode(LedOpcode);

        foreach (var balloon in positioned)
        {
            var (r, g, b) = Evaluate(run, balloon, elapsed,
                originLat, originLon, centerLat, centerLon);
            if (run.LastColors.TryGetValue(balloon.MeshAddress, out var previous)
                && previous == (r, g, b))
            {
                continue;
            }

            run.LastColors[balloon.MeshAddress] = (r, g, b);
            var payload = BalloonProtocol.EncodeLedSetNow(tick, r, g, b);
            _gateways.Broadcast(new DownlinkCommand(balloon.MeshAddress, opcode, payload, 7));
        }
    }

    private void SendBlackFrame()
    {
        var balloons = _fleet.GetBalloons();
        if (balloons.Count == 0)
        {
            return;
        }

        var addresses = balloons.Select(balloon => balloon.MeshAddress).ToArray();
        var tick = _dispatch.NextIssuedTick(addresses);
        var payload = BalloonProtocol.EncodeLedSetNow(tick, 0, 0, 0);
        var opcode = BalloonProtocol.VendorOpcode(LedOpcode);
        foreach (var balloon in balloons)
        {
            _gateways.Broadcast(new DownlinkCommand(balloon.MeshAddress, opcode, payload, 7));
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
            case "rainbow":
            {
                var distance = DistanceMeters(originLat, originLon, balloon.Lat!.Value, balloon.Lon!.Value);
                var hue = elapsed * run.Speed / run.Wavelength + distance / run.Wavelength;
                hue -= Math.Floor(hue);
                var (r, g, b) = HsvToRgb(hue, 0.9, 1.0);
                return ((byte)Math.Round(r * 0.85), (byte)Math.Round(g * 0.85), (byte)Math.Round(b * 0.85));
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
        return (
            (byte)Math.Round(Math.Clamp(r, 0, 255)),
            (byte)Math.Round(Math.Clamp(g, 0, 255)),
            (byte)Math.Round(Math.Clamp(b, 0, 255)));
    }

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
            _ => throw new ArgumentException(
                $"Unknown effect kind '{kind}'. Use flow, wave, pulse or rainbow.")
        };

    private static string LabelFor(string kind) => kind switch
    {
        "flow" => "Flow from node",
        "wave" => "Wave sweep",
        "pulse" => "Synchronised pulse",
        "rainbow" => "Rainbow drift",
        _ => kind
    };

    private static (double Speed, double Wavelength, double Width, int DirectionDeg) DefaultsFor(string kind) =>
        kind switch
        {
            "flow" => (55, 110, 0.5, 0),
            "wave" => (45, 150, 0.35, 90),
            "pulse" => (1, 3, 1, 0),
            "rainbow" => (25, 240, 1, 0),
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
            bool clearOnStop,
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
            ClearOnStop = clearOnStop;
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
        public bool ClearOnStop { get; }
        public DateTimeOffset StartedAt { get; private set; }
        public DateTimeOffset? EndsAt { get; private set; }
        public DateTimeOffset NextFrameAt { get; set; }
        public double PeriodSeconds => Math.Max(Kind == "pulse" ? 0.2 : 0, Wavelength / Speed);
        public long LastCycle { get; set; }
        public bool InBlackout { get; set; }
        public DateTimeOffset? StopAt { get; set; }
        public Dictionary<ushort, (byte R, byte G, byte B)> LastColors { get; } = [];

        public void BeginAt(DateTimeOffset now, TimeSpan blackout)
        {
            StartedAt = now;
            EndsAt = DurationSeconds > 0 ? now.AddSeconds(Math.Ceiling(DurationSeconds / PeriodSeconds) * PeriodSeconds) : null;
            NextFrameAt = now.Add(blackout);
            InBlackout = true;
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
