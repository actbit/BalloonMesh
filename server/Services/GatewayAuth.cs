using System.Security.Cryptography;
using System.Text;
using BalloonMesh.Server.Data;

namespace BalloonMesh.Server.Services;

/// <summary>How a gateway request was authenticated.</summary>
public enum GatewayAuthLevel
{
    /// <summary>Rejected.</summary>
    Denied,
    /// <summary>Development only: no key issued and Gateway:AllowUnkeyedGateways is on.</summary>
    Unkeyed,
    /// <summary>Legacy shared Gateway:Token, accepted only for gateways without an issued key.</summary>
    SharedToken,
    /// <summary>The per-gateway API key issued for exactly this gateway ID.</summary>
    GatewayKey,
}

public static class GatewayAuth
{
    public const string HeaderName = "X-Gateway-Token";

    public static string? ConfiguredToken(IConfiguration configuration) =>
        configuration["Gateway:Token"];

    // Gateways without an issued key are accepted unauthenticated only when
    // explicitly enabled (development / first-time bootstrap). Defaults to false.
    public static bool AllowUnkeyedGateways(IConfiguration configuration) =>
        configuration.GetValue<bool>("Gateway:AllowUnkeyedGateways");

    public static GatewayAuthLevel Authenticate(
        HttpContext context,
        string gatewayId,
        DataStore store,
        IConfiguration configuration)
    {
        var provided = context.Request.Headers.TryGetValue(HeaderName, out var values)
            ? values.ToString()
            : null;
        return Authenticate(provided, gatewayId, store, configuration);
    }

    /// <summary>
    /// A gateway that has an issued API key can only authenticate with that
    /// key: the shared token can no longer impersonate it.  The shared token
    /// (or, in development, no token at all) only covers gateways that have
    /// not been issued a key yet.
    /// </summary>
    public static GatewayAuthLevel Authenticate(
        string? provided,
        string gatewayId,
        DataStore store,
        IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(gatewayId))
        {
            return GatewayAuthLevel.Denied;
        }
        if (store.IsGatewayApiKeyValid(gatewayId, provided))
        {
            return GatewayAuthLevel.GatewayKey;
        }
        if (store.HasActiveGatewayApiKey(gatewayId))
        {
            return GatewayAuthLevel.Denied;
        }
        var configured = ConfiguredToken(configuration);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Matches(provided, configured)
                ? GatewayAuthLevel.SharedToken
                : GatewayAuthLevel.Denied;
        }
        return AllowUnkeyedGateways(configuration)
            ? GatewayAuthLevel.Unkeyed
            : GatewayAuthLevel.Denied;
    }

    public static bool IsAuthorized(
        HttpContext context,
        string gatewayId,
        DataStore store,
        IConfiguration configuration) =>
        Authenticate(context, gatewayId, store, configuration) != GatewayAuthLevel.Denied;

    public static bool IsAuthorized(
        string? provided,
        string gatewayId,
        DataStore store,
        IConfiguration configuration) =>
        Authenticate(provided, gatewayId, store, configuration) != GatewayAuthLevel.Denied;

    /// <summary>
    /// Mesh address allocation and Mesh network keys are bound to a gateway
    /// identity, so they require that gateway's own API key.  The shared token
    /// cannot claim them.  Unkeyed access is honoured only in development.
    /// </summary>
    public static bool MayReceiveMeshIdentity(GatewayAuthLevel level) =>
        level is GatewayAuthLevel.GatewayKey or GatewayAuthLevel.Unkeyed;

    public static bool Matches(string? provided, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }
        var left = Encoding.UTF8.GetBytes(provided);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length &&
               CryptographicOperations.FixedTimeEquals(left, right);
    }
}

/// <summary>Mesh key indexes shared with firmware (bm_config.h BM_NET_IDX / BM_APP_IDX).</summary>
public static class MeshNetwork
{
    public const int NetIndex = 0;
    public const int AppIndex = 0;
}
