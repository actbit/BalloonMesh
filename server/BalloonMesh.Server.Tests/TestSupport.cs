using BalloonMesh.Server.Data;
using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using Microsoft.Extensions.Configuration;
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
    public Task LedShadowChangedAsync(LedShadowSnapshotDto state) => Task.CompletedTask;
    public Task EffectChangedAsync(EffectStateDto? effect)
    {
        Effects.Add(effect);
        return Task.CompletedTask;
    }

    public List<EffectStateDto?> Effects { get; } = [];
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
