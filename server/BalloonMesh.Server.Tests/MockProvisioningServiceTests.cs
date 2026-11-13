using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BalloonMesh.Server.Tests;

public sealed class MockProvisioningServiceTests
{
    [Fact]
    public void BalloonProvisioningRequiresAZoneGroup()
    {
        using var database = new TemporaryDatabase();
        var service = CreateService(database.Store);

        Assert.False(service.Provision("mock-b-01", null));

        var device = service.Snapshot().Single(item => item.Id == "mock-b-01");
        Assert.Equal("unprovisioned", device.State);
        Assert.Null(device.GroupId);
    }

    [Fact]
    public void BalloonProvisioningKeepsTheSelectedZoneGroup()
    {
        using var database = new TemporaryDatabase();
        var service = CreateService(database.Store);

        Assert.True(service.Provision("mock-b-01", 2));

        var device = service.Snapshot().Single(item => item.Id == "mock-b-01");
        Assert.Equal("provisioning", device.State);
        Assert.Equal(2, device.GroupId);
    }

    [Fact]
    public void AllBalloonsGroupCannotBeSelectedForProvisioning()
    {
        using var database = new TemporaryDatabase();
        var service = CreateService(database.Store);

        Assert.False(service.Provision("mock-b-01", 5));
    }

    [Fact]
    public void GatewayCannotBeAssignedToABalloonGroup()
    {
        using var database = new TemporaryDatabase();
        var service = CreateService(database.Store);

        Assert.False(service.Provision("mock-gw-01", 1));
    }

    [Fact]
    public void BodylessLegacyProvisioningRemainsAvailable()
    {
        using var database = new TemporaryDatabase();
        var service = CreateService(database.Store);

        Assert.True(service.Provision("mock-gw-01"));
        Assert.False(service.Provision("mock-b-01"));
    }

    private static MockProvisioningService CreateService(BalloonMesh.Server.Data.DataStore store)
    {
        var fleet = new FleetService(store);
        return new MockProvisioningService(
            fleet,
            new RecordingMonitorEvents(),
            NullLogger<MockProvisioningService>.Instance);
    }
}
