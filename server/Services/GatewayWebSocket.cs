using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BalloonMesh.Server.Data;
using BalloonMesh.Server.Models;

namespace BalloonMesh.Server.Services;

public static class GatewayWebSocket
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(
        WebSocket socket,
        HttpContext context,
        GatewayRegistry registry,
        MeshReportRouter router,
        ILogger logger,
        DataStore store,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, context.RequestAborted);
        string? gatewayId = null;
        long connectionGeneration = 0;
        CancellationTokenSource? connectionLifetime = null;
        try
        {
            var hello = await ReceiveTextAsync(socket, linked.Token);
            if (hello is null)
            {
                return;
            }
            try
            {
                using var document = JsonDocument.Parse(hello);
                var root = document.RootElement;
                var helloType = ReadString(root, "type");
                if (string.Equals(helloType, "hello", StringComparison.OrdinalIgnoreCase))
                {
                    gatewayId = ReadString(root, "gatewayId");
                }
                var remote = ReadString(root, "name");
                var firmware = ReadString(root, "firmware");
                var token = ReadString(root, "token");
                var provisioned = root.TryGetProperty("provisioned", out var provValue) &&
                                  provValue.ValueKind == JsonValueKind.True;
                var spoolPending = root.TryGetProperty("spoolPending", out var spoolValue) &&
                                   spoolValue.TryGetInt32(out var spoolCount)
                    ? spoolCount
                    : 0;
                if (string.IsNullOrWhiteSpace(gatewayId) ||
                    !GatewayAuth.IsAuthorized(token, gatewayId, store, configuration))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                        "invalid gateway token", linked.Token);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(gatewayId))
                {
                    connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                        linked.Token);
                    connectionGeneration = registry.AttachWebSocket(
                        gatewayId,
                        remote ?? context.Connection.RemoteIpAddress?.ToString(),
                        firmware,
                        provisioned,
                        spoolPending,
                        socket,
                        connectionLifetime);
                }
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Gateway sent malformed hello JSON");
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    "invalid hello", linked.Token);
                return;
            }

            if (string.IsNullOrWhiteSpace(gatewayId))
            {
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                    "hello with gatewayId required", linked.Token);
                return;
            }

            var connectionToken = connectionLifetime?.Token ?? linked.Token;
            var client = registry.GetOrRegister(gatewayId);
            // One writer per gateway.  It survives reconnects and always sends
            // to CurrentSocket, so commands keep their order and are retried
            // after a socket failure instead of being handed between writers.
            registry.EnsureWebSocketWriter(gatewayId,
                () => Task.Run(() => WriteCommandsLoopAsync(client, logger)));

            while (socket.State == WebSocketState.Open &&
                   !connectionToken.IsCancellationRequested)
            {
                var text = await ReceiveTextAsync(socket, connectionToken);
                if (text is null)
                {
                    break;
                }
                registry.Touch(gatewayId, webSocket: true);
                try
                {
                    using var document = JsonDocument.Parse(text);
                    var root = document.RootElement;
                    var messageType = ReadString(root, "type");
                    if (string.Equals(messageType, "mesh", StringComparison.OrdinalIgnoreCase))
                    {
                        var message = JsonSerializer.Deserialize<GatewayEventDto>(text, Json);
                        if (message is not null)
                        {
                            router.Handle(message, gatewayId);
                        }
                    }
                    else if (string.Equals(messageType, "hello",
                                 StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(messageType, "state",
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        // Readiness can change after the initial hello (for
                        // example when provisioning or AppKey binding
                        // completes), so accept later status updates too.
                        bool? provisioned = root.TryGetProperty("provisioned",
                            out var prov)
                            ? prov.ValueKind == JsonValueKind.True
                            : null;
                        var spool = root.TryGetProperty("spoolPending", out var spoolValue) &&
                                    spoolValue.TryGetInt32(out var spoolCount)
                            ? spoolCount
                            : (int?)null;
                        registry.UpdateRuntimeState(gatewayId, provisioned, spool,
                            connectionGeneration);
                    }
                }
                catch (JsonException exception)
                {
                    logger.LogWarning(exception,
                        "Gateway {Gateway} sent malformed JSON", gatewayId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation(exception, "Gateway WebSocket closed: {Gateway}", gatewayId);
        }
        finally
        {
            if (connectionGeneration != 0 && !string.IsNullOrWhiteSpace(gatewayId))
            {
                await registry.DetachWebSocketAsync(gatewayId, connectionGeneration);
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // ignore
                }
            }
        }
    }

    /// <summary>
    /// Persistent writer: reads one command at a time and retries it on the
    /// current socket until it is sent or the process stops.  Because there is
    /// only one reader, command order is preserved across reconnects.
    /// </summary>
    private static async Task WriteCommandsLoopAsync(GatewayClient client, ILogger logger)
    {
        while (true)
        {
            var command = await client.Commands.Reader.ReadAsync();
            client.WriterBusy = true;
            try
            {
                var body = new
                {
                    type = "mesh",
                    dst = command.Dst,
                    opcode = command.Opcode,
                    payload = Convert.ToHexString(command.Payload),
                    ttl = command.Ttl
                };
                var json = JsonSerializer.SerializeToUtf8Bytes(body, Json);
                while (true)
                {
                    WebSocket? socket;
                    lock (client.ConnectionGate)
                    {
                        socket = client.WebSocketConnected ? client.CurrentSocket : null;
                    }
                    if (socket is null || socket.State != WebSocketState.Open)
                    {
                        await Task.Delay(200);
                        continue;
                    }
                    try
                    {
                        using var sendTimeout = new CancellationTokenSource(
                            TimeSpan.FromSeconds(5));
                        await socket.SendAsync(json, WebSocketMessageType.Text, true,
                            sendTimeout.Token);
                        break;
                    }
                    catch (Exception exception) when (exception is WebSocketException or
                                                      OperationCanceledException or
                                                      ObjectDisposedException or
                                                      InvalidOperationException)
                    {
                        logger.LogDebug(exception,
                            "Gateway {Gateway} send failed; retrying", client.Id);
                        await Task.Delay(200);
                    }
                }
            }
            finally
            {
                client.WriterBusy = false;
            }
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string?> ReceiveTextAsync(WebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 1024 * 1024)
            {
                return null;
            }
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}
