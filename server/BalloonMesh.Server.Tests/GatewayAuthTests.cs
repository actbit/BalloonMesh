using BalloonMesh.Server.Services;
using Microsoft.Extensions.Configuration;

namespace BalloonMesh.Server.Tests;

public sealed class GatewayAuthTests
{
    [Fact]
    public void EmptyConfiguredTokenAllowsAnyGateway()
    {
        Assert.True(GatewayAuth.Matches(null, ""));
        Assert.True(GatewayAuth.Matches("anything", null));
    }

    [Fact]
    public void TokenMustMatchExactly()
    {
        Assert.True(GatewayAuth.Matches("secret", "secret"));
        Assert.False(GatewayAuth.Matches("secret", "other"));
        Assert.False(GatewayAuth.Matches("secret", "secret-longer"));
        Assert.False(GatewayAuth.Matches(null, "secret"));
    }

    [Fact]
    public void UnkeyedGatewaysAreDisallowedByDefault()
    {
        var unset = new ConfigurationBuilder().Build();
        Assert.False(GatewayAuth.AllowUnkeyedGateways(unset));
        Assert.True(GatewayAuth.AllowUnkeyedGateways(Config(allowUnkeyed: true)));
    }

    [Fact]
    public void SharedTokenCannotReceiveMeshIdentity()
    {
        Assert.False(GatewayAuth.MayReceiveMeshIdentity(GatewayAuthLevel.SharedToken));
        Assert.False(GatewayAuth.MayReceiveMeshIdentity(GatewayAuthLevel.Denied));
        Assert.True(GatewayAuth.MayReceiveMeshIdentity(GatewayAuthLevel.GatewayKey));
        Assert.True(GatewayAuth.MayReceiveMeshIdentity(GatewayAuthLevel.Unkeyed));
    }

    internal static IConfiguration Config(string? token = null, bool allowUnkeyed = false) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:Token"] = token,
                ["Gateway:AllowUnkeyedGateways"] = allowUnkeyed ? "true" : "false"
            })
            .Build();
}

[Collection("PostgresDatabase")]
public sealed class GatewayAuthStoreTests
{
    [Fact]
    public void IssuedKeyIsBoundToItsGatewayAndLocksOutTheSharedToken()
    {
        using var database = new TemporaryDatabase();
        var config = GatewayAuthTests.Config(token: "shared");
        var issued = database.Store.IssueGatewayApiKey("gw-a");

        Assert.Equal(GatewayAuthLevel.GatewayKey,
            GatewayAuth.Authenticate(issued.Token, "gw-a", database.Store, config));
        // Another gateway ID cannot reuse gw-a's key.
        Assert.Equal(GatewayAuthLevel.SharedToken,
            GatewayAuth.Authenticate("shared", "gw-b", database.Store, config));
        Assert.Equal(GatewayAuthLevel.Denied,
            GatewayAuth.Authenticate(issued.Token, "gw-b", database.Store, config));
        // Once gw-a has a key, the shared token can no longer impersonate it.
        Assert.Equal(GatewayAuthLevel.Denied,
            GatewayAuth.Authenticate("shared", "gw-a", database.Store, config));
    }

    [Fact]
    public void NoTokenIsDeniedUnlessUnkeyedIsExplicitlyAllowed()
    {
        using var database = new TemporaryDatabase();
        Assert.Equal(GatewayAuthLevel.Denied,
            GatewayAuth.Authenticate((string?)null, "gw-a", database.Store, GatewayAuthTests.Config()));
        Assert.Equal(GatewayAuthLevel.Unkeyed,
            GatewayAuth.Authenticate((string?)null, "gw-a", database.Store,
                GatewayAuthTests.Config(allowUnkeyed: true)));
    }

    [Fact]
    public void MeshNetworkKeysAreRandomAndStable()
    {
        using var database = new TemporaryDatabase();
        var first = database.Store.GetOrCreateMeshNetworkKeys(0, 0);
        var again = database.Store.GetOrCreateMeshNetworkKeys(0, 0);

        Assert.Equal(16, first.NetKey.Length);
        Assert.Equal(16, first.AppKey.Length);
        Assert.NotEqual(first.NetKey, first.AppKey);
        Assert.Equal(first.NetKey, again.NetKey);
        Assert.Equal(first.AppKey, again.AppKey);
    }
}
