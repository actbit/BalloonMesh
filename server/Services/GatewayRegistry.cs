using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;

namespace BalloonMesh.Server.Services;

public sealed class GatewayClient
{
    public GatewayClient(string id)
    {
        Id = id;
        Commands = Channel.CreateBounded<DownlinkCommand>(new BoundedChannelOptions(2048)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public string Id { get; }
    public Channel<DownlinkCommand> Commands { get; }
    public bool WebSocketConnected { get; set; }
    public string? Remote { get; set; }
    public string? Firmware { get; set; }
    public bool Provisioned { get; set; }
    public int SpoolPending { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public int PendingCommands => Commands.Reader.CanCount ? Commands.Reader.Count : 0;
}

/// <summary>
/// Tracks connected gateways.  A gateway may use the WebSocket transport or
/// the HTTP event/command-poll transport; both share one command channel.
/// </summary>
public sealed class GatewayRegistry
{
    private const int GatewayQueueCapacity = 2048;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, GatewayClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _broadcastGate = new();
    private readonly IMonitorEvents _notifier;

    public GatewayRegistry(IMonitorEvents notifier)
    {
        _notifier = notifier;
    }

    public GatewayClient GetOrRegister(string id, string? firmware = null)
    {
        var client = _clients.GetOrAdd(id, static key => new GatewayClient(key));
        client.LastSeen = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(firmware))
        {
            client.Firmware = firmware;
        }
        return client;
    }

    public bool AttachWebSocket(string id, string? remote, string? firmware)
    {
        var client = GetOrRegister(id, firmware);
        client.WebSocketConnected = true;
        client.Remote = remote;
        _ = NotifyChangedAsync();
        return true;
    }

    public async Task DetachWebSocketAsync(string id)
    {
        if (_clients.TryGetValue(id, out var client))
        {
            client.WebSocketConnected = false;
            client.LastSeen = DateTimeOffset.UtcNow;
        }
        await NotifyChangedAsync();
    }

    public void Touch(string id, string? firmware = null)
    {
        var isNew = !_clients.ContainsKey(id);
        var client = GetOrRegister(id, firmware);
        client.LastSeen = DateTimeOffset.UtcNow;
        if (isNew)
        {
            _ = NotifyChangedAsync();
        }
    }

    public int Broadcast(DownlinkCommand command)
    {
        lock (_broadcastGate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-30);
            var clients = _clients.Values.Where(client => client.WebSocketConnected || client.LastSeen >= cutoff).ToArray();
            if (clients.Any(client => client.PendingCommands >= GatewayQueueCapacity))
            {
                return 0;
            }
            return clients.Count(client => client.Commands.Writer.TryWrite(command));
        }
    }

    public bool TryReadCommand(string id, out DownlinkCommand? command)
    {
        var client = GetOrRegister(id);
        return client.Commands.Reader.TryRead(out command);
    }

    public async Task<DownlinkCommand?> ReadCommandAsync(string id, int waitMs, CancellationToken cancellationToken)
    {
        var client = GetOrRegister(id);
        if (waitMs <= 0)
        {
            return client.Commands.Reader.TryRead(out var immediate) ? immediate : null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Math.Clamp(waitMs, 0, 30_000));
        try
        {
            return await client.Commands.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public IReadOnlyList<GatewayDto> Snapshot() =>
        _clients.Values
            .OrderBy(client => client.Id, StringComparer.OrdinalIgnoreCase)
            .Select(client => new GatewayDto(
                client.Id,
                client.WebSocketConnected,
                client.Remote,
                client.Firmware,
                client.LastSeen,
                client.PendingCommands,
                client.Provisioned,
                client.SpoolPending))
            .ToArray();

    public async Task NotifyChangedAsync()
    {
        var snapshot = Snapshot();
        await _notifier.GatewayUpdatedAsync(snapshot);
    }

    public static JsonSerializerOptions SerializerOptions => Json;
}
