using System.Security.Cryptography;
using System.Text;
using BalloonMesh.Server.Data;

namespace BalloonMesh.Server.Services;

public static class GatewayAuth
{
    public const string HeaderName = "X-Gateway-Token";

    public static string? ConfiguredToken(IConfiguration configuration) =>
        configuration["Gateway:Token"];

    // Gateways without an issued key are accepted unauthenticated only when
    // explicitly enabled (development / first-time bootstrap). Defaults to false.
    public static bool AllowUnkeyedGateways(IConfiguration configuration) =>
        configuration.GetValue<bool>("Gateway:AllowUnkeyedGateways");

    public static bool IsAuthorized(HttpContext context, IConfiguration configuration)
    {
        var expected = ConfiguredToken(configuration);
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return false;
        }
        return Matches(values.ToString(), expected);
    }

    public static bool IsAuthorized(
        HttpContext context,
        string gatewayId,
        DataStore store,
        IConfiguration configuration)
    {
        var provided = context.Request.Headers.TryGetValue(HeaderName, out var values)
            ? values.ToString()
            : null;
        return IsAuthorized(provided, gatewayId, store, configuration);
    }

    public static bool IsAuthorized(
        string? provided,
        string gatewayId,
        DataStore store,
        IConfiguration configuration)
    {
        var configured = ConfiguredToken(configuration);
        if (!string.IsNullOrWhiteSpace(configured) && Matches(provided, configured))
        {
            return true;
        }
        if (store.IsGatewayApiKeyValid(gatewayId, provided))
        {
            return true;
        }
        if (store.HasActiveGatewayApiKey(gatewayId))
        {
            return false;
        }

        // Development/bootstrap: unkeyed gateways pass only when no legacy
        // token is configured AND Gateway:AllowUnkeyedGateways is enabled.
        return string.IsNullOrWhiteSpace(configured) && AllowUnkeyedGateways(configuration);
    }

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
