using BalloonMesh.Server.Models;
using BalloonMesh.Protocol;
using BalloonMesh.Server.Services;

namespace BalloonMesh.Server.Tests;

public sealed class GatewayRegistryTests
{
    [Fact]
    public void AGatewayIsRestoredAfterARestart()
    {
        var store = new FakeGatewayStateStore();
        var first = TestRegistry.Create(new RecordingMonitorEvents(), store);
        first.GetOrRegister("gw-01", "1.2.3");
        first.UpdateRuntimeState("gw-01", provisioned: true, spoolPending: 4);

        var restarted = TestRegistry.Create(new RecordingMonitorEvents(), store);
        restarted.LoadFromStore();

        var gateway = Assert.Single(restarted.Snapshot());
        Assert.Equal("gw-01", gateway.Id);
        Assert.Equal("1.2.3", gateway.Firmware);
        Assert.True(gateway.Provisioned);
        Assert.Equal(4, gateway.SpoolPending);
        Assert.False(gateway.WebSocket);
    }

    [Fact]
    public void ARestoredGatewayIsNotADeliveryPathUntilItConnectsAgain()
    {
        var store = new FakeGatewayStateStore();
        var first = TestRegistry.Create(new RecordingMonitorEvents(), store);
        first.GetOrRegister("gw-01");
        first.MarkReady("gw-01");

        var restarted = TestRegistry.Create(new RecordingMonitorEvents(), store);
        restarted.LoadFromStore();

        // Nothing reads the restored gateway's channel yet, so a command must
        // not be counted as delivered to it.
        Assert.Equal(0, restarted.Broadcast(new DownlinkCommand(1, 1, [1], 7)));

        restarted.BeginPoll("gw-01");
        Assert.Equal(1, restarted.Broadcast(new DownlinkCommand(1, 1, [1], 7)));
    }

    [Fact]
    public void ConcurrentUpdatesDoNotLeaveTheRowHoldingTheOlderSnapshot()
    {
        var store = new FakeGatewayStateStore();
        var registry = TestRegistry.Create(new RecordingMonitorEvents(), store);
        registry.GetOrRegister("gw-01");

        // Readiness and a plain touch race; whichever lands last, the stored
        // row must not fall back to "not provisioned".
        Parallel.For(0, 200, i =>
        {
            if (i % 2 == 0)
            {
                registry.UpdateRuntimeState("gw-01", provisioned: true, spoolPending: null);
            }
            else
            {
                registry.Touch("gw-01");
            }
        });

        var gateway = Assert.Single(store.GetGateways());
        Assert.True(gateway.Provisioned);
    }

    [Fact]
    public void LastSeenOnlyChangesDoNotWriteOnEveryRequest()
    {
        var store = new FakeGatewayStateStore();
        var registry = TestRegistry.Create(new RecordingMonitorEvents(), store);

        for (var i = 0; i < 50; i++)
        {
            registry.Touch("gw-01");
        }

        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task FullGatewayQueueRetainsOlderCommands()
    {
        var registry = TestRegistry.Create(new RecordingMonitorEvents());
        registry.GetOrRegister("gw-test");
        var first = new DownlinkCommand(1, 1, [1], 7);
        for (var i = 0; i < 2048; i++) Assert.Equal(1, registry.Broadcast(first));
        var second = registry.GetOrRegister("gw-second");
        Assert.Equal(1, registry.Broadcast(new DownlinkCommand(2, 1, [2], 7)));
        Assert.Equal(1, second.PendingCommands);
        Assert.Equal(first, await registry.ReadCommandAsync("gw-test", 0, default));
    }

    [Fact]
    public async Task BroadcastQueuesACommandForAConnectedGateway()
    {
        var registry = TestRegistry.Create(new RecordingMonitorEvents());
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
