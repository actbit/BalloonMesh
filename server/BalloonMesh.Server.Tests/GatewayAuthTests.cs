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
        var enabled = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:AllowUnkeyedGateways"] = "true"
            })
            .Build();
        Assert.False(GatewayAuth.AllowUnkeyedGateways(unset));
        Assert.True(GatewayAuth.AllowUnkeyedGateways(enabled));
    }
}
