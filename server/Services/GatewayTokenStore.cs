using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// Upper bound on cached lookups.  Unknown digests are cached too, and an
    /// unauthenticated caller chooses them, so the cache must not grow with
    /// request count.
    /// </summary>
    private const int MaxCacheEntries = 512;

    private readonly IGatewayTokenRepository _store;
    private readonly TimeProvider _time;
    private readonly ILogger<GatewayTokenStore> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastUseWrite = new();

    /// <summary>
    /// Bumped by every issue and revoke.  A lookup captures it before reading
    /// the database and the result is only cached if it has not changed since,
    /// so a revoke that lands mid-lookup cannot be overwritten by the stale
    /// row the lookup already fetched.
    /// </summary>
    private long _generation;

    public GatewayTokenStore(IGatewayTokenRepository store, TimeProvider time,
        ILogger<GatewayTokenStore> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<GatewayTokenRecord> List() => _store.GetGatewayTokens();

    /// <summary>Cached lookups held right now; exposed so a test can show the cache is bounded.</summary>
    public int CachedLookups => _cache.Count;

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
        Invalidate();
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
        // Invalidate even when the row was already revoked, so a cached entry
        // from a concurrent request cannot keep the token alive.
        Invalidate();
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
                // The id comes from the URL, so an invalid one must not reach
                // the log as text: it could otherwise forge whole log lines.
                ForLog(gatewayId), context.Connection.RemoteIpAddress, result.Failure);
        }
        return result;
    }

    public GatewayTokenResult Authenticate(string? presented, string? gatewayId)
    {
        if (string.IsNullOrWhiteSpace(presented))
        {
            return GatewayTokenResult.NoToken;
        }
        // A token is only ever valid for the gateway it was issued for, so a
        // caller that names no gateway cannot be authenticated.  The route
        // pattern accepts a blank segment ("/api/gateway/%20/events"), so
        // this check must deny rather than skip the binding below.
        if (!IsValidGatewayId(gatewayId))
        {
            return GatewayTokenResult.Denied(GatewayTokenFailure.GatewayMismatch);
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
            var generation = Interlocked.Read(ref _generation);
            record = _store.FindGatewayTokenByHash(Convert.FromHexString(digest));
            // Drop the result instead of caching it if an issue or revoke
            // landed while the read was in flight: the row may already be
            // stale, and caching it would keep a revoked token alive.
            if (Interlocked.Read(ref _generation) == generation &&
                _cache.Count < MaxCacheEntries)
            {
                _cache[digest] = new CacheEntry(record, now + CacheLifetime);
            }
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
        if (!string.Equals(record.GatewayId, gatewayId, StringComparison.OrdinalIgnoreCase))
        {
            return GatewayTokenResult.Denied(GatewayTokenFailure.GatewayMismatch, record);
        }

        MarkUsed(record, now);
        return GatewayTokenResult.Allowed(record);
    }

    private void Invalidate()
    {
        // Bump first: a lookup that reads the database after this point must
        // see the new generation and refuse to cache a pre-change row.
        Interlocked.Increment(ref _generation);
        _cache.Clear();
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

    /// <summary>
    /// The id as it may appear in a log line: the value itself when it is a
    /// valid id, and a fixed placeholder otherwise.
    /// </summary>
    public static string ForLog(string? gatewayId) =>
        IsValidGatewayId(gatewayId) ? gatewayId : "<invalid-id>";

    /// <summary>Longest accepted gateway id, matching the firmware's own limit.</summary>
    public const int MaxGatewayIdLength = 48;

    /// <summary>
    /// A gateway id must be a short, printable identifier.  Restricting the
    /// character set keeps a caller-supplied id out of log lines as anything
    /// but one token, and keeps junk ids out of the gateways table.
    /// </summary>
    public static bool IsValidGatewayId(
        [NotNullWhen(true)] string? gatewayId)
    {
        if (string.IsNullOrEmpty(gatewayId) || gatewayId.Length > MaxGatewayIdLength)
        {
            return false;
        }
        foreach (var character in gatewayId)
        {
            if (!char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_' or '.'))
            {
                return false;
            }
        }
        return true;
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
