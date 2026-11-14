using BalloonMesh.Server.Services;
using Microsoft.AspNetCore.Http;
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void ABlankGatewayIdIsRejectedRatherThanSkippingTheBindingCheck(string? id)
    {
        var (store, _, _) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);

        // "/api/gateway/%20/events" reaches the handler with a blank id, so a
        // blank id must deny instead of waving the binding check through.
        var result = store.Authenticate(issued.PlainToken, id);

        Assert.False(result.Authorized);
        Assert.Equal(GatewayTokenFailure.GatewayMismatch, result.Failure);
    }

    [Theory]
    [InlineData("gw-01")]
    [InlineData("GW_01.a")]
    public void AValidGatewayIdIsAccepted(string id)
    {
        Assert.True(GatewayTokenStore.IsValidGatewayId(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("gw 01")]
    [InlineData("gw\n2026-11-14 admin login ok")]
    [InlineData("gw/../etc")]
    [InlineData("gw;01")]
    public void AnIdThatCouldForgeALogLineIsNotValid(string id)
    {
        Assert.False(GatewayTokenStore.IsValidGatewayId(id));
        Assert.Equal("<invalid-id>", GatewayTokenStore.ForLog(id));
    }

    [Fact]
    public void AnOverlongGatewayIdIsNotValid()
    {
        Assert.False(GatewayTokenStore.IsValidGatewayId(
            new string('a', GatewayTokenStore.MaxGatewayIdLength + 1)));
        Assert.True(GatewayTokenStore.IsValidGatewayId(
            new string('a', GatewayTokenStore.MaxGatewayIdLength)));
    }

    [Fact]
    public void ARevokeDuringALookupDoesNotLeaveTheTokenCachedAsValid()
    {
        var repository = new FakeGatewayTokenRepository();
        var time = new TestTimeProvider(Now);
        var store = new GatewayTokenStore(repository, time,
            NullLogger<GatewayTokenStore>.Instance);
        var issued = store.Issue("gw-01", "stage left", "admin", null);

        // Revoke lands between the lookup's database read and its cache write,
        // which is the interleaving a polling gateway makes likely.
        repository.OnNextLookup = () => store.Revoke(issued.Record.Id);
        store.Authenticate(issued.PlainToken, "gw-01");

        // The next request must not be served the pre-revocation row.
        var result = store.Authenticate(issued.PlainToken, "gw-01");
        Assert.False(result.Authorized);
        Assert.Equal(GatewayTokenFailure.Revoked, result.Failure);
    }

    [Fact]
    public void UnknownTokensCannotGrowTheCacheWithoutBound()
    {
        var (store, repository, _) = Create();

        for (var i = 0; i < 5_000; i++)
        {
            var result = store.Authenticate($"bmgw_bogus-{i}", "gw-01");
            Assert.False(result.Authorized);
        }

        Assert.True(store.CachedLookups <= 512,
            $"cache grew to {store.CachedLookups} entries");
        // A genuine token still authenticates after the flood.
        var issued = store.Issue("gw-01", "stage left", "admin", null);
        Assert.True(store.Authenticate(issued.PlainToken, "gw-01").Authorized);
        Assert.True(repository.Lookups > 0);
    }

    [Fact]
    public void TheHttpEntryPointRejectsARequestWithNoHeader()
    {
        var (store, _, _) = Create();
        store.Issue("gw-01", "stage left", "admin", null);

        Assert.False(store.AuthenticateRequest(new DefaultHttpContext(), "gw-01")
            .Authorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TheHttpEntryPointRejectsABlankHeader(string header)
    {
        var (store, _, _) = Create();
        store.Issue("gw-01", "stage left", "admin", null);
        var context = new DefaultHttpContext();
        context.Request.Headers[GatewayTokenStore.HeaderName] = header;

        Assert.False(store.AuthenticateRequest(context, "gw-01").Authorized);
    }

    [Fact]
    public void TheHttpEntryPointAcceptsTheIssuedTokenAndRejectsAnotherGatewayId()
    {
        var (store, _, _) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);
        var context = new DefaultHttpContext();
        context.Request.Headers[GatewayTokenStore.HeaderName] = issued.PlainToken;

        Assert.True(store.AuthenticateRequest(context, "gw-01").Authorized);
        Assert.False(store.AuthenticateRequest(context, "gw-02").Authorized);
        Assert.False(store.AuthenticateRequest(context, " ").Authorized);
    }

    [Fact]
    public void TwoTokenHeadersDoNotAuthenticate()
    {
        var (store, _, _) = Create();
        var issued = store.Issue("gw-01", "stage left", "admin", null);
        var context = new DefaultHttpContext();
        context.Request.Headers[GatewayTokenStore.HeaderName] =
            new[] { issued.PlainToken, issued.PlainToken };

        Assert.False(store.AuthenticateRequest(context, "gw-01").Authorized);
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
