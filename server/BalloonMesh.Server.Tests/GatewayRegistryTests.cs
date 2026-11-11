using BalloonMesh.Server.Models;
using BalloonMesh.Server.Protocol;
using BalloonMesh.Server.Services;

namespace BalloonMesh.Server.Tests;

public sealed class GatewayRegistryTests
{
    [Fact]
    public async Task FullGatewayQueueRetainsOlderCommands()
    {
        var registry = new GatewayRegistry(new RecordingMonitorEvents());
        registry.GetOrRegister("gw-test");
        var first = new DownlinkCommand(1, 1, [1], 7);
        for (var i = 0; i < 2048; i++) Assert.Equal(1, registry.Broadcast(first));
        var second = registry.GetOrRegister("gw-second");
        Assert.Equal(0, registry.Broadcast(new DownlinkCommand(2, 1, [2], 7)));
        Assert.Equal(0, second.PendingCommands);
        Assert.Equal(first, await registry.ReadCommandAsync("gw-test", 0, default));
    }

    [Fact]
    public async Task BroadcastQueuesACommandForAConnectedGateway()
    {
        var registry = new GatewayRegistry(new RecordingMonitorEvents());
        var command = new DownlinkCommand(
            0xC010,
            BalloonProtocol.VendorOpcode(BalloonProtocol.OpLedSet),
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            7);

        Assert.Equal(0, registry.Broadcast(command));
        registry.Touch("gw-test", "0.1.0");
        Assert.Equal(1, registry.Broadcast(command));

        var read = await registry.ReadCommandAsync("gw-test", 0, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(command, read);
        Assert.Null(await registry.ReadCommandAsync("gw-test", 10, CancellationToken.None));
        Assert.Contains(registry.Snapshot(), gateway => gateway.Id == "gw-test");
    }
}
