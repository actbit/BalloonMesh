using BalloonMesh.Server.Models;
using BalloonMesh.Server.Services;
using Microsoft.AspNetCore.SignalR;

namespace BalloonMesh.Server.Hubs;

public sealed class MonitorHub : Hub
{
    private readonly FleetService _fleet;
    private readonly GatewayRegistry _gateways;
    private readonly SimulationService _simulation;
    private readonly EffectService _effects;
    private readonly LedShadowService _shadow;

    public MonitorHub(FleetService fleet, GatewayRegistry gateways, SimulationService simulation,
        EffectService effects, LedShadowService shadow)
    {
        _fleet = fleet;
        _gateways = gateways;
        _simulation = simulation;
        _effects = effects;
        _shadow = shadow;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("state",
            _fleet.GetState(_gateways.Snapshot(), _simulation.Running, ServerInfo.Version,
                _effects.Current, _shadow.Capture()));
        await base.OnConnectedAsync();
    }
}

public interface IMonitorEvents
{
    Task BalloonUpdatedAsync(BalloonDto balloon);
    Task QueueUpdatedAsync(BalloonDto balloon);
    Task GatewayUpdatedAsync(IReadOnlyList<GatewayDto> gateways);
    Task SimulationChangedAsync(bool running);
    Task CommandSentAsync(CommandResultDto result);
    Task OnboardingUpdatedAsync(IReadOnlyList<MockProvisioningDevice> devices);
    Task EffectChangedAsync(EffectStateDto? effect);
    Task LedShadowChangedAsync(LedShadowSnapshotDto state);
}

public sealed class MonitorNotifier : IMonitorEvents
{
    private readonly IHubContext<MonitorHub> _hub;

    public MonitorNotifier(IHubContext<MonitorHub> hub)
    {
        _hub = hub;
    }

    public Task BalloonUpdatedAsync(BalloonDto balloon) =>
        _hub.Clients.All.SendAsync("balloonUpdated", balloon);

    public Task QueueUpdatedAsync(BalloonDto balloon) =>
        _hub.Clients.All.SendAsync("queueUpdated", balloon);

    public Task GatewayUpdatedAsync(IReadOnlyList<GatewayDto> gateways) =>
        _hub.Clients.All.SendAsync("gatewaysUpdated", gateways);

    public Task SimulationChangedAsync(bool running) =>
        _hub.Clients.All.SendAsync("simulationChanged", running);

    public Task CommandSentAsync(CommandResultDto result) =>
        _hub.Clients.All.SendAsync("commandSent", result);

    public Task OnboardingUpdatedAsync(IReadOnlyList<MockProvisioningDevice> devices) =>
        _hub.Clients.All.SendAsync("onboardingUpdated", devices);

    public Task EffectChangedAsync(EffectStateDto? effect) =>
        _hub.Clients.All.SendAsync("effectChanged", effect);

    public Task LedShadowChangedAsync(LedShadowSnapshotDto state) =>
        _hub.Clients.All.SendAsync("ledShadowChanged", state);
}

public static class ServerInfo
{
    public const string Version = "0.1.0";
}
