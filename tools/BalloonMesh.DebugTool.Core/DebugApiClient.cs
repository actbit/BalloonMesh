using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BalloonMesh.DebugTool.Core;

/// <summary>
/// Small typed client for the debug surface of the BalloonMesh server.
/// Cookie authentication (for example the Development mock login) is handled
/// by the default <see cref="HttpClientHandler"/> cookie container.
/// </summary>
public sealed class DebugApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private string? _sessionCookie;
    private bool _disposed;

    public DebugApiClient(Uri baseUri, HttpMessageHandler? handler = null)
    {
        _baseUri = NormalizeBaseUri(baseUri);
        handler ??= new HttpClientHandler
        {
            // Cookies are managed explicitly so mock-login and manually pasted
            // cookies can never be sent at the same time.
            UseCookies = false,
            AllowAutoRedirect = false
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public Uri BaseUri => _baseUri;

    /// <summary>Gateway token issued by the server. Never persisted by the tool.</summary>
    public string? GatewayToken { get; set; }

    /// <summary>
    /// Optional raw Cookie header value for servers without mock login. It
    /// overrides the cookie obtained from the mock login endpoint.
    /// </summary>
    public string? AuthCookie { get; set; }

    public static Uri NormalizeBaseUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Server URL must be absolute.", nameof(baseUri));
        }
        if (baseUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Server URL must use http or https.", nameof(baseUri));
        }
        if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException("Server URL must not contain a query or fragment.",
                nameof(baseUri));
        }
        if (!string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ArgumentException("Server URL must not contain user information.",
                nameof(baseUri));
        }
        return baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
    }

    public Task<ServerHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
        SendJsonAsync<ServerHealth>(HttpMethod.Get, "api/health", body: null,
            gatewayRequest: false, cancellationToken);

    public Task<FleetState> GetStateAsync(CancellationToken cancellationToken = default) =>
        SendJsonAsync<FleetState>(HttpMethod.Get, "api/state", body: null,
            gatewayRequest: false, cancellationToken);

    public Task<IReadOnlyList<ShowStepInfo>> GetShowStepsAsync(int limit = 50,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<IReadOnlyList<ShowStepInfo>>(HttpMethod.Get,
            $"api/show?limit={Math.Clamp(limit, 1, 200)}", body: null,
            gatewayRequest: false, cancellationToken);

    public async Task<CommandResult?> CancelShowStepAsync(long id,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = NewRequest(HttpMethod.Delete, $"api/show/{id}", gatewayRequest: false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response.StatusCode, text);
        }
        return Deserialize<CommandResult>(text);
    }

    public Task<CommandResult> SendLedSetAsync(LedSetCommand command,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<CommandResult>(HttpMethod.Post, "api/commands/led-set", command,
            gatewayRequest: false, cancellationToken);

    public Task<CommandResult> SendLedNowAsync(LedNowCommand command,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<CommandResult>(HttpMethod.Post, "api/commands/now", command,
            gatewayRequest: false, cancellationToken);

    public Task<CommandResult> SendLedCancelAsync(LedCancelCommand command,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<CommandResult>(HttpMethod.Post, "api/commands/cancel", command,
            gatewayRequest: false, cancellationToken);

    public Task<CommandResult> SendLedResetAsync(LedResetCommand command,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync<CommandResult>(HttpMethod.Post, "api/commands/reset", command,
            gatewayRequest: false, cancellationToken);

    public async Task SetDemoRunningAsync(bool running,
        CancellationToken cancellationToken = default)
    {
        var path = running ? "api/demo/start" : "api/demo/stop";
        _ = await SendJsonAsync<DemoState>(HttpMethod.Post, path, body: null,
            gatewayRequest: false, cancellationToken).ConfigureAwait(false);
    }

    public Task<AcceptedEvents> SendGatewayEventsAsync(string gatewayId,
        IReadOnlyList<GatewayEventInfo> events, string? firmware = null,
        bool? provisioned = null, int? spoolPending = null,
        CancellationToken cancellationToken = default)
    {
        var body = new GatewayEventsBody(gatewayId, firmware, events, provisioned, spoolPending);
        return SendJsonAsync<AcceptedEvents>(HttpMethod.Post,
            $"api/gateway/{Uri.EscapeDataString(gatewayId)}/events", body,
            gatewayRequest: true, cancellationToken);
    }

    public async Task<IReadOnlyList<GatewayCommandInfo>> ReadGatewayCommandsAsync(
        string gatewayId, int waitMs, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wait = Math.Clamp(waitMs, 0, 30_000);
        using var request = NewRequest(HttpMethod.Get,
            $"api/gateway/{Uri.EscapeDataString(gatewayId)}/commands?waitMs={wait}",
            gatewayRequest: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return [];
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response.StatusCode, text, gatewayRequest: true);
        }
        return Deserialize<GatewayCommandsResponse>(text).Commands;
    }

    /// <summary>
    /// Signs in through the Development-only mock endpoint. The cookie is kept
    /// in the handler's cookie container; a manually supplied cookie is cleared
    /// so the two authentication paths cannot mix.
    /// </summary>
    public async Task LoginMockAsync(string role, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (role is not ("Admin" or "Operator" or "Viewer"))
        {
            throw new ArgumentException("Mock role must be Admin, Operator or Viewer.", nameof(role));
        }
        AuthCookie = null;
        _sessionCookie = null;
        using var request = NewRequest(HttpMethod.Get,
            $"auth/login/mock?role={Uri.EscapeDataString(role)}", gatewayRequest: false);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var redirected = (int)response.StatusCode is >= 300 and < 400;
        if (!redirected && !response.IsSuccessStatusCode)
        {
            throw CreateException(response.StatusCode, string.Empty,
                "Mock login is not available. Enable Authentication:Mock:Enabled in Development.");
        }
        _sessionCookie = ExtractSessionCookie(response);
        if (_sessionCookie is null)
        {
            throw new DebugApiException(response.StatusCode,
                "Mock login did not return a session cookie.", null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _http.Dispose();
    }

    private static string? ExtractSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return null;
        }
        var cookies = new List<string>();
        foreach (var header in headers)
        {
            var pair = header.Split(';', 2)[0].Trim();
            var separator = pair.IndexOf('=');
            if (separator <= 0 || separator == pair.Length - 1)
            {
                continue;
            }
            if (header.Contains("Max-Age=0", StringComparison.OrdinalIgnoreCase) ||
                header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            cookies.Add(pair);
        }
        return cookies.Count == 0 ? null : string.Join("; ", cookies);
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string relative,
        object? body, bool gatewayRequest, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var request = NewRequest(method, relative, gatewayRequest);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response.StatusCode, text, gatewayRequest: gatewayRequest);
        }
        return Deserialize<T>(text);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string relative, bool gatewayRequest)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUri, relative));
        if (gatewayRequest && !string.IsNullOrWhiteSpace(GatewayToken))
        {
            request.Headers.TryAddWithoutValidation("X-Gateway-Token", GatewayToken);
        }
        var cookie = string.IsNullOrWhiteSpace(AuthCookie) ? _sessionCookie : AuthCookie.Trim();
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            if (cookie.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            {
                cookie = cookie["Cookie:".Length..].Trim();
            }
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }
        return request;
    }

    private static T Deserialize<T>(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new DebugApiException(HttpStatusCode.OK,
                "The server returned an empty response.", text);
        }
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json)
                ?? throw new DebugApiException(HttpStatusCode.OK,
                    "The server returned an empty JSON value.", text);
        }
        catch (JsonException exception)
        {
            throw new DebugApiException(HttpStatusCode.OK,
                "The server returned malformed JSON.", text, exception);
        }
    }

    private static DebugApiException CreateException(HttpStatusCode status, string body,
        string? hint = null, bool gatewayRequest = false)
    {
        var detail = ExtractError(body);
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}";
        var guidance = hint ?? (gatewayRequest
            ? status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Check the Gateway token (X-Gateway-Token) in the Gateway simulator tab.",
                HttpStatusCode.NotFound => "The gateway endpoint was not found. Check the server URL.",
                _ => null
            }
            : status switch
            {
                HttpStatusCode.Unauthorized => "Sign in with Mock login or supply a session cookie.",
                HttpStatusCode.Forbidden => "The signed-in user does not have permission for this operation.",
                HttpStatusCode.NotFound => "The endpoint was not found. Check the server URL.",
                _ => null
            });
        var message = $"HTTP {(int)status} {status}{suffix}";
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            message += $" {guidance}";
        }
        return new DebugApiException(status, message, body);
    }

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
            {
                return Truncate(error.GetString() ?? string.Empty);
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall back to the raw body.
        }
        return Truncate(body.Trim());
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";

    private sealed record DemoState(bool Running);

    private sealed record GatewayCommandsResponse(IReadOnlyList<GatewayCommandInfo> Commands);

    private sealed record GatewayEventsBody(
        string GatewayId,
        string? Firmware,
        IReadOnlyList<GatewayEventInfo> Events,
        bool? Provisioned,
        int? SpoolPending);
}
