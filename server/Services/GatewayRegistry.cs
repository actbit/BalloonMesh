using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using BalloonMesh.Server.Data;
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

    private long _webSocketGeneration;

    /// <summary>Serializes attach/detach/state swaps for this gateway.</summary>
    public object ConnectionGate { get; } = new();

    public string Id { get; }
    public Channel<DownlinkCommand> Commands { get; }
    public WebSocket? CurrentSocket { get; set; }
    public bool WriterBusy { get; set; }
    public bool WebSocketConnected { get; set; }
    public bool HasWebSocket { get; set; }
    public bool HttpPolling { get; set; }
    public bool ProvisionedReported { get; set; }
    public long WebSocketGeneration => Interlocked.Read(ref _webSocketGeneration);
    public CancellationTokenSource? WebSocketLifetime { get; set; }
    public Task? WebSocketWriter { get; set; }
    public string? Remote { get; set; }
    public string? Firmware { get; set; }
    public bool Provisioned { get; set; }
    public int SpoolPending { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True for a gateway restored from the database that has not connected
    /// again yet.  Its stored LastSeen must not make it look like a live
    /// delivery path, because nothing is reading its command channel.
    /// </summary>
    public bool Restored { get; set; }

    /// <summary>Last write-through of this gateway's row, for throttling.</summary>
    public DateTimeOffset PersistedAt { get; set; }
    public string? PersistedState { get; set; }
    public int PendingCommands =>
        (Commands.Reader.CanCount ? Commands.Reader.Count : 0) + (WriterBusy ? 1 : 0);

    public long BumpWebSocketGeneration() =>
        Interlocked.Increment(ref _webSocketGeneration);
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
    private readonly IGatewayStateStore _store;
    private readonly ILogger<GatewayRegistry> _logger;

    /// <summary>Minimum interval between last-seen-only row writes.</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);

    public GatewayRegistry(IMonitorEvents notifier, IGatewayStateStore store,
        ILogger<GatewayRegistry> logger)
    {
        _notifier = notifier;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Restores the known gateways at startup so the fleet list, firmware and
    /// last-seen times survive a restart.  Live transport state is not
    /// restored: a restored gateway counts as a delivery path only once it
    /// connects or polls again.
    /// </summary>
    public void LoadFromStore()
    {
        foreach (var record in _store.GetGateways())
        {
            var client = _clients.GetOrAdd(record.Id, static key => new GatewayClient(key));
            lock (client.ConnectionGate)
            {
                client.Remote = record.Remote;
                client.Firmware = record.Firmware;
                client.Provisioned = record.Provisioned;
                client.SpoolPending = record.SpoolPending;
                client.LastSeen = record.LastSeen;
                client.Restored = true;
                client.PersistedAt = record.LastSeen;
                client.PersistedState = StateSignature(client);
            }
        }
    }

    public GatewayClient GetOrRegister(string id, string? firmware = null)
    {
        var client = _clients.GetOrAdd(id, static key => new GatewayClient(key));
        client.LastSeen = DateTimeOffset.UtcNow;
        client.Restored = false;
        if (!string.IsNullOrWhiteSpace(firmware))
        {
            client.Firmware = firmware;
        }
        Persist(client);
        return client;
    }

    /// <summary>
    /// Writes the gateway's durable fields.  A change to firmware, remote,
    /// readiness or spool depth is written immediately; a last-seen-only
    /// change is throttled so a long-polling gateway does not write per
    /// request.
    /// </summary>
    private void Persist(GatewayClient client)
    {
        string? remote;
        string? firmware;
        bool provisioned;
        int spoolPending;
        DateTimeOffset lastSeen;
        lock (client.ConnectionGate)
        {
            var signature = StateSignature(client);
            var now = DateTimeOffset.UtcNow;
            if (signature == client.PersistedState &&
                now - client.PersistedAt < PersistInterval)
            {
                return;
            }
            client.PersistedState = signature;
            client.PersistedAt = now;
            remote = client.Remote;
            firmware = client.Firmware;
            provisioned = client.Provisioned;
            spoolPending = client.SpoolPending;
            lastSeen = client.LastSeen;
        }
        try
        {
            _store.UpsertGateway(client.Id, remote, firmware, provisioned,
                spoolPending, lastSeen);
        }
        catch (Exception exception)
        {
            // Persistence must not fail a gateway request; the next write retries.
            _logger.LogWarning(exception,
                "Could not persist gateway {Gateway}", client.Id);
            lock (client.ConnectionGate)
            {
                client.PersistedState = null;
            }
        }
    }

    private static string StateSignature(GatewayClient client) =>
        string.Join('\u001f', client.Remote, client.Firmware,
            client.Provisioned ? "1" : "0", client.SpoolPending);

    public long AttachWebSocket(string id, string? remote, string? firmware,
        bool provisioned, int spoolPending, WebSocket socket,
        CancellationTokenSource lifetime)
    {
        var client = GetOrRegister(id, firmware);
        long generation;
        CancellationTokenSource? previous;
        lock (client.ConnectionGate)
        {
            // Bump first: the old handler's detach/state callbacks must see a
            // stale generation as soon as the new connection is accepted.
            generation = client.BumpWebSocketGeneration();
            previous = client.WebSocketLifetime;
            client.WebSocketLifetime = lifetime;
            client.CurrentSocket = socket;
            client.Provisioned = provisioned;
            client.ProvisionedReported = true;
            client.SpoolPending = spoolPending;
            client.WebSocketConnected = true;
            client.HasWebSocket = true;
            client.Remote = remote;
            client.Restored = false;
        }
        Persist(client);
        // Stop the previous receive loop; the single writer survives and will
        // pick up the new CurrentSocket.
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The previous handler already finished.
            }
        }
        _ = NotifyChangedAsync();
        return generation;
    }

    /// <summary>Starts the single persistent writer for this gateway once.</summary>
    public void EnsureWebSocketWriter(string id, Func<Task> factory)
    {
        var client = GetOrRegister(id);
        lock (client.ConnectionGate)
        {
            client.WebSocketWriter ??= factory();
        }
    }

    public async Task DetachWebSocketAsync(string id, long generation)
    {
        if (_clients.TryGetValue(id, out var client))
        {
            lock (client.ConnectionGate)
            {
                if (client.WebSocketGeneration == generation)
                {
                    // Only the current connection may clear the socket/flag.
                    client.CurrentSocket = null;
                    client.WebSocketConnected = false;
                    client.LastSeen = DateTimeOffset.UtcNow;
                }
            }
            Persist(client);
        }
        await NotifyChangedAsync();
    }

    public void UpdateRuntimeState(string id, bool? provisioned, int? spoolPending,
        long? generation = null)
    {
        var client = GetOrRegister(id);
        var changed = false;
        lock (client.ConnectionGate)
        {
            if (generation is { } expected &&
                client.WebSocketGeneration != expected)
            {
                // A stale connection must not overwrite the current one's state.
                return;
            }
            if (provisioned is { } value)
            {
                if (client.Provisioned != value)
                {
                    client.Provisioned = value;
                    changed = true;
                }
                if (!client.ProvisionedReported)
                {
                    client.ProvisionedReported = true;
                    changed = true;
                }
            }
            if (spoolPending is { } spool && client.SpoolPending != spool)
            {
                client.SpoolPending = spool;
                changed = true;
            }
        }
        Persist(client);
        if (changed)
        {
            _ = NotifyChangedAsync();
        }
    }

    /// <summary>
    /// A gateway only polls commands after its mesh models are ready, so a
    /// successful poll is itself a readiness signal.  This prevents an HTTP
    /// gateway from latching "unready" forever after an early spooled event.
    /// </summary>
    public void MarkReady(string id)
    {
        var client = GetOrRegister(id);
        var changed = false;
        lock (client.ConnectionGate)
        {
            if (!client.Provisioned)
            {
                client.Provisioned = true;
                changed = true;
            }
            if (!client.ProvisionedReported)
            {
                client.ProvisionedReported = true;
                changed = true;
            }
        }
        Persist(client);
        if (changed)
        {
            _ = NotifyChangedAsync();
        }
    }

    public void BeginPoll(string id)
    {
        var client = GetOrRegister(id);
        lock (client.ConnectionGate)
        {
            client.HttpPolling = true;
            client.HasWebSocket = false;
            client.LastSeen = DateTimeOffset.UtcNow;
        }
        Persist(client);
    }

    public void EndPoll(string id)
    {
        if (_clients.TryGetValue(id, out var client))
        {
            lock (client.ConnectionGate)
            {
                client.HttpPolling = false;
                client.LastSeen = DateTimeOffset.UtcNow;
            }
            Persist(client);
        }
    }

    public void Touch(string id, string? firmware = null, bool webSocket = false)
    {
        var isNew = !_clients.ContainsKey(id);
        var client = GetOrRegister(id, firmware);
        lock (client.ConnectionGate)
        {
            client.LastSeen = DateTimeOffset.UtcNow;
            if (webSocket)
            {
                client.HasWebSocket = true;
            }
            else if (!client.WebSocketConnected)
            {
                // A gateway that never reconnects its WebSocket may have
                // switched to HTTP; let the HTTP activity rule apply again.
                client.HasWebSocket = false;
            }
        }
        Persist(client);
        if (isNew)
        {
            _ = NotifyChangedAsync();
        }
    }

    public int Broadcast(DownlinkCommand command)
    {
        lock (_broadcastGate)
        {
            // HTTP gateways are never removed, so only recently seen or
            // WebSocket-connected clients count as active receivers.
            var now = DateTimeOffset.UtcNow;
            // An HTTP gateway is a delivery path only while it is actually
            // long-polling (or just finished a poll).  A gateway that stopped
            // polling must not make a command look delivered.
            var httpCutoff = now.AddSeconds(-5);
            var clients = _clients.Values
                // A gateway restored from the database has no reader on its
                // command channel until it connects again.
                .Where(client => !client.Restored)
                .Where(client => client.HasWebSocket
                    // A detached WebSocket gateway must not fall back to the
                    // HTTP "recently seen" rule: its old channel has no reader.
                    // Readiness is required because an unbound model drops
                    // downlinks silently.
                    ? client.WebSocketConnected && client.Provisioned
                    // HTTP gateways are only active while they keep polling.
                    // If they explicitly reported "not ready", respect it.
                    : (client.HttpPolling || client.LastSeen >= httpCutoff) &&
                      (!client.ProvisionedReported || client.Provisioned))
                .ToArray();
            // One full gateway must not block delivery through the remaining
            // gateways: they broadcast to the same mesh, so a healthy path can
            // still cover the target.  Only fail when no client can accept it.
            return clients.Count(client =>
                client.PendingCommands < GatewayQueueCapacity &&
                client.Commands.Writer.TryWrite(command));
        }
    }

    public bool TryReadCommand(string id, out DownlinkCommand? command)
    {
        var client = GetOrRegister(id);
        return client.Commands.Reader.TryRead(out command);
    }

    public async Task<DownlinkCommand?> ReadCommandAsync(string id, int waitMs,
        CancellationToken cancellationToken)
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
