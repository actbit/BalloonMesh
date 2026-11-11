using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
        LedShadowService shadow,
        ILogger logger,
        string? expectedToken,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, context.RequestAborted);
        string? gatewayId = null;
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
                if (root.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "hello", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("gatewayId", out var id))
                {
                    gatewayId = id.GetString();
                }
                var remote = root.TryGetProperty("name", out var name) ? name.GetString() : null;
                var firmware = root.TryGetProperty("firmware", out var version) ? version.GetString() : null;
                var token = root.TryGetProperty("token", out var tokenValue) ? tokenValue.GetString() : null;
                var provisioned = root.TryGetProperty("provisioned", out var provValue) &&
                                  provValue.ValueKind == JsonValueKind.True;
                var spoolPending = root.TryGetProperty("spoolPending", out var spoolValue) &&
                                   spoolValue.TryGetInt32(out var spoolCount)
                    ? spoolCount
                    : 0;
                if (!GatewayAuth.Matches(token, expectedToken))
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                        "invalid gateway token", linked.Token);
                    return;
                }
                if (!string.IsNullOrWhiteSpace(gatewayId))
                {
                    registry.AttachWebSocket(gatewayId, remote ?? context.Connection.RemoteIpAddress?.ToString(), firmware);
                    if (registry.GetOrRegister(gatewayId) is { } attachedClient)
                    {
                        attachedClient.Provisioned = provisioned;
                        attachedClient.SpoolPending = spoolPending;
                    }
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

            var client = registry.GetOrRegister(gatewayId);
            var writer = WriteCommandsAsync(socket, client, shadow, linked.Token, logger);
            try
            {
                while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
                {
                    var text = await ReceiveTextAsync(socket, linked.Token);
                    if (text is null)
                    {
                        break;
                    }
                    registry.Touch(gatewayId);
                    try
                    {
                        using var document = JsonDocument.Parse(text);
                        var root = document.RootElement;
                        if (root.TryGetProperty("type", out var type)
                            && string.Equals(type.GetString(), "mesh", StringComparison.OrdinalIgnoreCase))
                        {
                            var message = JsonSerializer.Deserialize<GatewayEventDto>(text, Json);
                            if (message is not null)
                            {
                                router.Handle(message, gatewayId);
                            }
                        }
                    }
                    catch (JsonException exception)
                    {
                        logger.LogWarning(exception, "Gateway {Gateway} sent malformed JSON", gatewayId);
                    }
                }
            }
            finally
            {
                linked.Cancel();
                try
                {
                    await writer;
                }
                catch (OperationCanceledException)
                {
                    // expected on disconnect
                }
                await registry.DetachWebSocketAsync(gatewayId);
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

    private static async Task WriteCommandsAsync(
        WebSocket socket,
        GatewayClient client,
        LedShadowService shadow,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var command = await client.Commands.Reader.ReadAsync(cancellationToken);
                var body = new
                {
                    type = "mesh",
                    dst = command.Dst,
                    opcode = command.Opcode,
                    payload = Convert.ToHexString(command.Payload),
                    ttl = command.Ttl
                };
                var json = JsonSerializer.SerializeToUtf8Bytes(body, Json);
                await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken);
                shadow.ObserveDelivered(command);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on disconnect
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation(exception, "Gateway command writer stopped");
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
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
