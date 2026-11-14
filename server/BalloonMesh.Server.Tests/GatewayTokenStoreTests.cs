using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BalloonMesh.Server.Tests;

public sealed class GatewayTokenStoreTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static (GatewayTokenStore Store, FakeGatewayTokenRepository Repository,
        TestTimeProvider Time) Create()
    {
        var repository = new FakeGatewayTokenRepository();
        var time = new TestTimeProvider(Now);
        return (new GatewayTokenStore(repository, time,
            NullLogger<GatewayTokenStore>.Instance), repository, time);
    }

    [Fact]
    public void WithNoTokenIssuedEveryGatewayIsRejected()
    {
        var (store, _, _) = Create();

        Assert.False(store.Authenticate("anything", "gw-01").Authorized);
        Assert.False(store.Authenticate(null, "gw-01").Authorized);
        Assert.False(store.Authenticate("", "gw-01").Authorized);
    }

    [Fact]
    public void AnIssuedTokenAuthenticatesItsOwnGateway()
    {
        var (store, _, _) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);

        var result = store.Authenticate(issued.PlainToken, "gw-01");

        Assert.True(result.Authorized);
        Assert.Equal("gw-01", result.Token!.GatewayId);
    }

    [Fact]
    public void ATokenCannotBeUsedForAnotherGateway()
    {
        var (store, _, _) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);

        var result = store.Authenticate(issued.PlainToken, "gw-02");

        Assert.False(result.Authorized);
        Assert.Equal(GatewayTokenFailure.GatewayMismatch, result.Failure);
    }

    [Fact]
    public void TheIssuedValueIsNotRecoverableFromTheStoredRow()
    {
        var (store, _, _) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);

        var stored = Assert.Single(store.List());

        Assert.DoesNotContain(issued.PlainToken, stored.Prefix);
        Assert.StartsWith(stored.Prefix, issued.PlainToken);
        Assert.True(issued.PlainToken.Length > stored.Prefix.Length);
    }

    [Fact]
    public void RevokingATokenRejectsItImmediately()
    {
        var (store, _, _) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);
        Assert.True(store.Authenticate(issued.PlainToken, "gw-01").Authorized);

        Assert.True(store.Revoke(issued.Record.Id));

        var result = store.Authenticate(issued.PlainToken, "gw-01");
        Assert.False(result.Authorized);
        Assert.Equal(GatewayTokenFailure.Revoked, result.Failure);
        Assert.False(store.Revoke(issued.Record.Id));
    }

    [Fact]
    public void AnExpiredTokenIsRejected()
    {
        var (store, _, time) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", Now.AddDays(1));
        Assert.True(store.Authenticate(issued.PlainToken, "gw-01").Authorized);

        time.Advance(TimeSpan.FromDays(2));

        var result = store.Authenticate(issued.PlainToken, "gw-01");
        Assert.False(result.Authorized);
        Assert.Equal(GatewayTokenFailure.Expired, result.Failure);
    }

    [Fact]
    public void TwoIssuedTokensDoNotCollide()
    {
        var (store, _, _) = Create();

        var first = store.Issue("gw-01", "a", "admin", null);
        var second = store.Issue("gw-01", "b", "admin", null);

        Assert.NotEqual(first.PlainToken, second.PlainToken);
        Assert.True(store.Authenticate(first.PlainToken, "gw-01").Authorized);
        Assert.True(store.Authenticate(second.PlainToken, "gw-01").Authorized);
    }

    [Fact]
    public void RepeatedPollsDoNotWriteUsageEveryTime()
    {
        var (store, repository, time) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(store.Authenticate(issued.PlainToken, "gw-01").Authorized);
        }
        Assert.Equal(1, repository.UseWrites);

        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(store.Authenticate(issued.PlainToken, "gw-01").Authorized);
        Assert.Equal(2, repository.UseWrites);
    }

    [Fact]
    public void RepeatedPollsDoNotQueryTheDatabaseEveryTime()
    {
        var (store, repository, time) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);

        for (var i = 0; i < 20; i++)
        {
            store.Authenticate(issued.PlainToken, "gw-01");
        }
        Assert.Equal(1, repository.Lookups);

        // The cache is short-lived, so a revoke elsewhere is picked up.
        time.Advance(TimeSpan.FromMinutes(1));
        store.Authenticate(issued.PlainToken, "gw-01");
        Assert.Equal(2, repository.Lookups);
    }
}
