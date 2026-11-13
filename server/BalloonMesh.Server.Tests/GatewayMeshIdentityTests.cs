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

        Assert.Equal((ushort)0x4000, first.MeshAddress);
        Assert.Equal((ushort)0x4001, second.MeshAddress);
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
}
