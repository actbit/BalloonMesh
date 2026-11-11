using BalloonMesh.Server.Data;
using BalloonMesh.Server.Protocol;

namespace BalloonMesh.Server.Services;

/// <summary>
/// Re-enqueues server-side reservations that were still "queued" when the
/// process stopped.  The device may already hold some of them; LED_SET is
/// idempotent for the same tick and the balloon keeps one entry per tick.
/// </summary>
public sealed class ShowRecoveryWorker : BackgroundService
{
    private readonly DataStore _store;
    private readonly FleetService _fleet;
    private readonly LedDispatchService _dispatch;
    private readonly TimeProvider _time;
    private readonly ILogger<ShowRecoveryWorker> _logger;

    public ShowRecoveryWorker(
        DataStore store,
        FleetService fleet,
        LedDispatchService dispatch,
        TimeProvider time,
        ILogger<ShowRecoveryWorker> logger)
    {
        _store = store;
        _fleet = fleet;
        _dispatch = dispatch;
        _time = time;
        _logger = logger;
    }

    public Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var recovered = 0;
        var expired = _store.ExpireQueuedShowSteps(now);
        if (expired > 0)
        {
            _logger.LogWarning("{Count} queued show step(s) were already past and were marked expired", expired);
        }
        foreach (var step in _store.GetPendingShowSteps(now))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                var target = _fleet.ResolveTarget(step.TargetKind, step.Target);
                var tick = BalloonProtocol.Tick40FromUnixMs(step.ExecuteAt.ToUnixTimeMilliseconds());
                if (tick <= BalloonProtocol.NowTick40(_time) ||
                    !_dispatch.EnqueueScheduled(target, tick, step.ExecuteAt,
                        step.R, step.G, step.B, step.Id))
                {
                    _store.UpdateShowStepStatus(step.Id, "expired");
                    continue;
                }
                recovered++;
            }
            catch (ArgumentException exception)
            {
                _store.UpdateShowStepStatus(step.Id, "expired");
                _logger.LogWarning("show step {Id} could not be recovered: {Message}",
                    step.Id, exception.Message);
            }
        }
        if (recovered > 0)
        {
            _logger.LogInformation("recovered {Count} queued show step(s)", recovered);
        }
        return Task.FromResult(recovered);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "show recovery failed");
        }
    }
}
