using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using BalloonMesh.Server.Data;

namespace BalloonMesh.Server.Services;

public enum GatewayTokenFailure
{
    Missing,
    Unknown,
    Revoked,
    Expired,
    GatewayMismatch
}

/// <summary>
/// Result of authenticating a gateway.  A failure never says which of the
/// reasons applied in its HTTP response; the reason is for the server log.
/// </summary>
public sealed record GatewayTokenResult(
    bool Authorized,
    GatewayTokenFailure? Failure = null,
    GatewayTokenRecord? Token = null)
{
    public static readonly GatewayTokenResult NoToken =
        new(false, GatewayTokenFailure.Missing);

    public static GatewayTokenResult Denied(GatewayTokenFailure failure,
        GatewayTokenRecord? token = null) => new(false, failure, token);

    public static GatewayTokenResult Allowed(GatewayTokenRecord token) =>
        new(true, null, token);
}

public sealed record IssuedGatewayToken(GatewayTokenRecord Record, string PlainToken);

/// <summary>
/// Issues and verifies per-gateway tokens.  Tokens are stored as SHA-256
/// digests in <c>gateway_tokens</c>, so the plain value is returned exactly
/// once, when it is issued.  Verification goes through a digest-keyed cache
/// because gateways post events several times per second; the cache is
/// invalidated on every issue and revoke, and a digest that is not cached
/// falls through to the database.
/// </summary>
public sealed class GatewayTokenStore
{
    public const string HeaderName = "X-Gateway-Token";
    public const string TokenPrefix = "bmgw_";

    /// <summary>How long a verified lookup may be served from memory.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    /// <summary>How often a token's last_used_at is written back.</summary>
    private static readonly TimeSpan UseWriteInterval = TimeSpan.FromSeconds(60);

    private readonly IGatewayTokenRepository _store;
    private readonly TimeProvider _time;
    private readonly ILogger<GatewayTokenStore> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastUseWrite = new();

    public GatewayTokenStore(IGatewayTokenRepository store, TimeProvider time,
        ILogger<GatewayTokenStore> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<GatewayTokenRecord> List() => _store.GetGatewayTokens();

    /// <summary>
    /// Creates a token for one gateway id.  The plain value is only in the
    /// returned object: it is never stored and cannot be shown again.
    /// </summary>
    public IssuedGatewayToken Issue(string gatewayId, string label, string createdBy,
        DateTimeOffset? expiresAt)
    {
        var plain = TokenPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _time.GetUtcNow();
        var id = _store.InsertGatewayToken(gatewayId, label, Digest(plain),
            Prefix(plain), createdBy, now, expiresAt);
        _cache.Clear();
        _logger.LogInformation(
            "Issued gateway token {Prefix} for {Gateway} by {User}",
            Prefix(plain), gatewayId, createdBy);
        return new IssuedGatewayToken(
            new GatewayTokenRecord(id, gatewayId, label, Prefix(plain), now, createdBy,
                null, expiresAt, null),
            plain);
    }

    public bool Revoke(long id)
    {
        var revoked = _store.RevokeGatewayToken(id, _time.GetUtcNow());
        // Clear even when the row was already revoked, so a cached entry from
        // a concurrent request cannot keep the token alive.
        _cache.Clear();
        if (revoked)
        {
            _logger.LogInformation("Revoked gateway token {Id}", id);
        }
        return revoked;
    }

    /// <summary>
    /// Authenticates an HTTP gateway request.  <paramref name="gatewayId"/> is
    /// the id from the route, which must be the one the token was issued for.
    /// </summary>
    public GatewayTokenResult AuthenticateRequest(HttpContext context, string gatewayId)
    {
        var presented = context.Request.Headers.TryGetValue(HeaderName, out var values)
            ? values.ToString()
            : null;
        var result = Authenticate(presented, gatewayId);
        if (!result.Authorized)
        {
            _logger.LogWarning(
                "Rejected gateway request for {Gateway} from {Remote}: {Reason}",
                gatewayId, context.Connection.RemoteIpAddress, result.Failure);
        }
        return result;
    }

    public GatewayTokenResult Authenticate(string? presented, string? gatewayId)
    {
        if (string.IsNullOrWhiteSpace(presented))
        {
            return GatewayTokenResult.NoToken;
        }

        var now = _time.GetUtcNow();
        var digest = Convert.ToHexString(Digest(presented));
        GatewayTokenRecord? record;
        if (_cache.TryGetValue(digest, out var cached) && cached.ExpiresAt > now)
        {
            record = cached.Record;
        }
        else
        {
            record = _store.FindGatewayTokenByHash(Convert.FromHexString(digest));
            _cache[digest] = new CacheEntry(record, now + CacheLifetime);
        }

        if (record is null)
        {
            return GatewayTokenResult.Denied(GatewayTokenFailure.Unknown);
        }
        if (record.RevokedAt is not null)
        {
            return GatewayTokenResult.Denied(GatewayTokenFailure.Revoked, record);
        }
        if (record.ExpiresAt is not null && record.ExpiresAt <= now)
        {
            return GatewayTokenResult.Denied(GatewayTokenFailure.Expired, record);
        }
        if (!string.IsNullOrWhiteSpace(gatewayId) &&
            !string.Equals(record.GatewayId, gatewayId, StringComparison.OrdinalIgnoreCase))
        {
            return GatewayTokenResult.Denied(GatewayTokenFailure.GatewayMismatch, record);
        }

        MarkUsed(record, now);
        return GatewayTokenResult.Allowed(record);
    }

    /// <summary>
    /// Records the use, at most once per <see cref="UseWriteInterval"/> per
    /// token, so a polling gateway does not write on every request.
    /// </summary>
    private void MarkUsed(GatewayTokenRecord record, DateTimeOffset now)
    {
        if (_lastUseWrite.TryGetValue(record.Id, out var previous) &&
            now - previous < UseWriteInterval)
        {
            return;
        }
        _lastUseWrite[record.Id] = now;
        try
        {
            _store.TouchGatewayTokenUse(record.Id, now);
        }
        catch (Exception exception)
        {
            // Usage bookkeeping must never fail an authenticated request.
            _logger.LogWarning(exception,
                "Could not record use of gateway token {Id}", record.Id);
        }
    }

    public static byte[] Digest(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>The leading characters shown in the UI to identify a token.</summary>
    public static string Prefix(string token) =>
        token.Length <= 12 ? token : token[..12];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');

    private sealed record CacheEntry(GatewayTokenRecord? Record, DateTimeOffset ExpiresAt);
}
