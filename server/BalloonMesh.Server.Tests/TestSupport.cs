using BalloonMesh.Server.Data;
using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BalloonMesh.Server.Tests;

public sealed class TemporaryDatabase : IDisposable
{
    private readonly string _directory;

    public TemporaryDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "balloonmesh-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Data:Schema"] = "balloonmesh_tests"
            })
            .Build();
        var environment = new TestHostEnvironment(_directory);
        Store = new DataStore(configuration, environment);
        Store.Initialize();
        Store.Reset();
    }

    public DataStore Store { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file briefly on Windows.
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRoot)
        {
            ContentRootPath = contentRoot;
            ContentRootFileProvider = new NullFileProvider();
        }

        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "BalloonMesh.Server.Tests";
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}

public sealed class RecordingMonitorEvents : IMonitorEvents
{
    public List<BalloonDto> Balloons { get; } = [];
    public List<BalloonDto> Queues { get; } = [];
    public List<BalloonDto> BalloonUpdates => Balloons;

    public Task BalloonUpdatedAsync(BalloonDto balloon)
    {
        Balloons.Add(balloon);
        return Task.CompletedTask;
    }

    public Task QueueUpdatedAsync(BalloonDto balloon)
    {
        Queues.Add(balloon);
        return Task.CompletedTask;
    }

    public Task GatewayUpdatedAsync(IReadOnlyList<GatewayDto> gateways) => Task.CompletedTask;
    public Task SimulationChangedAsync(bool running) => Task.CompletedTask;
    public Task CommandSentAsync(CommandResultDto result) => Task.CompletedTask;
    public Task OnboardingUpdatedAsync(IReadOnlyList<MockProvisioningDevice> devices) => Task.CompletedTask;
}

public static class TestRegistry
{
    /// <summary>
    /// A registry backed by in-memory persistence, so tests of the queueing
    /// and readiness rules do not need a database.
    /// </summary>
    public static GatewayRegistry Create(IMonitorEvents events,
        IGatewayStateStore? store = null) =>
        new(events, store ?? new FakeGatewayStateStore(),
            NullLogger<GatewayRegistry>.Instance);
}

/// <summary>In-memory stand-in for the gateways table.</summary>
public sealed class FakeGatewayStateStore : IGatewayStateStore
{
    private readonly Dictionary<string, GatewayRecord> _rows = new(StringComparer.OrdinalIgnoreCase);

    public int Writes { get; private set; }

    public IReadOnlyList<GatewayRecord> GetGateways() => _rows.Values.ToArray();

    public void UpsertGateway(string id, string? remote, string? firmware,
        bool? provisioned, int? spoolPending, DateTimeOffset lastSeen)
    {
        Writes++;
        _rows.TryGetValue(id, out var existing);
        _rows[id] = new GatewayRecord(
            id,
            remote ?? existing?.Remote,
            firmware ?? existing?.Firmware,
            provisioned ?? existing?.Provisioned ?? false,
            spoolPending ?? existing?.SpoolPending ?? 0,
            existing?.FirstSeen ?? lastSeen,
            existing is null || lastSeen > existing.LastSeen ? lastSeen : existing.LastSeen);
    }

    public void Seed(GatewayRecord record) => _rows[record.Id] = record;
}

/// <summary>In-memory stand-in for the gateway_tokens table.</summary>
public sealed class FakeGatewayTokenRepository : IGatewayTokenRepository
{
    private readonly Dictionary<long, GatewayTokenRecord> _rows = [];
    private readonly Dictionary<string, long> _byHash = new(StringComparer.Ordinal);
    private long _nextId = 1;

    public int Lookups { get; private set; }
    public int UseWrites { get; private set; }

    public long InsertGatewayToken(string gatewayId, string label, byte[] tokenHash,
        string prefix, string createdBy, DateTimeOffset createdAt,
        DateTimeOffset? expiresAt)
    {
        var id = _nextId++;
        _rows[id] = new GatewayTokenRecord(id, gatewayId, label, prefix, createdAt,
            createdBy, null, expiresAt, null);
        _byHash[Convert.ToHexString(tokenHash)] = id;
        return id;
    }

    public IReadOnlyList<GatewayTokenRecord> GetGatewayTokens() =>
        _rows.Values.OrderByDescending(row => row.Id).ToArray();

    public GatewayTokenRecord? FindGatewayTokenByHash(byte[] tokenHash)
    {
        Lookups++;
        return _byHash.TryGetValue(Convert.ToHexString(tokenHash), out var id)
            ? _rows[id]
            : null;
    }

    public bool RevokeGatewayToken(long id, DateTimeOffset revokedAt)
    {
        if (!_rows.TryGetValue(id, out var row) || row.RevokedAt is not null)
        {
            return false;
        }
        _rows[id] = row with { RevokedAt = revokedAt };
        return true;
    }

    public void TouchGatewayTokenUse(long id, DateTimeOffset usedAt)
    {
        UseWrites++;
        if (_rows.TryGetValue(id, out var row))
        {
            _rows[id] = row with { LastUsedAt = usedAt };
        }
    }

    public void Expire(long id, DateTimeOffset expiresAt)
    {
        _rows[id] = _rows[id] with { ExpiresAt = expiresAt };
    }
}

public sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan duration) => _now += duration;
}

[CollectionDefinition("PostgresDatabase", DisableParallelization = true)]
public sealed class PostgresDatabaseCollection
{
}
