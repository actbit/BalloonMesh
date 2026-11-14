using BalloonMesh.Server.Data;

namespace BalloonMesh.Server.Tests;

[Collection("PostgresDatabase")]
public sealed class GatewayMeshIdentityTests
{
    [Fact]
    public void AllocatesDistinctAddressesAndReusesAnExistingIdentity()
    {
        using var database = new TemporaryDatabase();

        var first = database.Store.GetOrAllocateGatewayMeshIdentity(
            "gw-a", "B1A11000000111223344001122334455");
        var second = database.Store.GetOrAllocateGatewayMeshIdentity(
            "gw-b", "B1A11000000111223344001122334466");
        var firstAgain = database.Store.GetOrAllocateGatewayMeshIdentity(
            "gw-a", "B1A11000000111223344001122334455");

        Assert.Equal((ushort)0x0001, first.MeshAddress);
        Assert.Equal((ushort)0x0002, second.MeshAddress);
        Assert.Equal(first, firstAgain);
    }

    [Fact]
    public void RejectsAnIdentityCollision()
    {
        using var database = new TemporaryDatabase();
        database.Store.GetOrAllocateGatewayMeshIdentity(
            "gw-a", "B1A11000000111223344001122334455");

        Assert.Throws<InvalidOperationException>(() =>
            database.Store.GetOrAllocateGatewayMeshIdentity(
                "gw-a", "B1A11000000111223344001122334466"));
    }

    [Fact]
    public void Allocates64Then16AddressesWithoutOverlappingTwoProvisioners()
    {
        using var database = new TemporaryDatabase();

        var firstGatewayFirst = database.Store.GetOrAllocateGatewayMeshNodeAddress(
            "gw-a", "B1A11000000111223344001122334455");
        var secondGatewayFirst = database.Store.GetOrAllocateGatewayMeshNodeAddress(
            "gw-b", "B1A11000000111223344001122334466");

        Assert.Equal(64, firstGatewayFirst.RangeEnd - firstGatewayFirst.RangeStart + 1);
        Assert.Equal(64, secondGatewayFirst.RangeEnd - secondGatewayFirst.RangeStart + 1);
        Assert.True(firstGatewayFirst.RangeStart > 0x0132);
        Assert.True(firstGatewayFirst.RangeEnd < secondGatewayFirst.RangeStart);

        GatewayMeshNodeIdentityRecord? last = null;
        for (var index = 1; index < 64; index++)
        {
            last = database.Store.GetOrAllocateGatewayMeshNodeAddress(
                "gw-a", $"B1A1100000011122334400112233{index:0000}");
        }

        var extension = database.Store.GetOrAllocateGatewayMeshNodeAddress(
            "gw-a", "B1A11000000111223344001122339999");
        Assert.NotNull(last);
        Assert.Equal(16, extension.RangeEnd - extension.RangeStart + 1);
        Assert.True(extension.MeshAddress > last!.MeshAddress);
        Assert.True(extension.RangeStart > secondGatewayFirst.RangeEnd);
    }

    [Fact]
    public void ReturnsExistingNodeClaimToTheOriginalProvisioner()
    {
        using var database = new TemporaryDatabase();
        var first = database.Store.GetOrAllocateGatewayMeshNodeAddress(
            "gw-a", "B1A11000000111223344001122334455");
        var retry = database.Store.GetOrAllocateGatewayMeshNodeAddress(
            "gw-a", "B1A11000000111223344001122334455");
        var other = database.Store.GetOrAllocateGatewayMeshNodeAddress(
            "gw-b", "B1A11000000111223344001122334455");

        Assert.Equal(first, retry);
        Assert.Equal(first.MeshAddress, other.MeshAddress);
        Assert.Equal("gw-a", other.GatewayId);
    }
}
