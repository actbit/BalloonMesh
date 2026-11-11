using BalloonMesh.Server.Protocol;
using BalloonMesh.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BalloonMesh.Server.Tests;

[Collection("PostgresDatabase")]
public sealed class FtmAggregatorTests
{
    [Fact]
    public async Task FirstComputeAtTenSecondsAndLateReportRecomputes()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var neighbors = new NeighborStore();
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new FtmAggregator(
            fleet, neighbors, new PositionSolver(), events, time,
            NullLogger<FtmAggregator>.Instance);

        var balloon = fleet.GetBalloonRecord(0x0101)!;
        var anchors = fleet.GetAnchors().Take(3).ToArray();
        for (var i = 0; i < anchors.Length; i++)
        {
            var distance = SimulationService.DistanceMeters(
                balloon.Lat!.Value, balloon.Lon!.Value, anchors[i].Lat, anchors[i].Lon);
            aggregator.AddReport(0x0101,
                new FtmReport(anchors[i].AnchorId,
                    (ushort)Math.Clamp(distance * 100.0, 1.0, 65535.0), 100, 1),
                i == 0 ? "gateway-a" : "gateway-b",
                time.GetUtcNow());
        }
        // A duplicate delivered by a second gateway must not create a second batch.
        aggregator.AddReport(0x0101,
            new FtmReport(anchors[0].AnchorId,
                (ushort)Math.Clamp(SimulationService.DistanceMeters(
                    balloon.Lat!.Value, balloon.Lon!.Value, anchors[0].Lat, anchors[0].Lon) * 100.0,
                    1.0, 65535.0), 100, 1),
            "gateway-a", time.GetUtcNow());

        Assert.Equal(0, await aggregator.ProcessDueAsync(time.GetUtcNow()));
        time.Advance(TimeSpan.FromSeconds(9.9));
        Assert.Equal(0, await aggregator.ProcessDueAsync(time.GetUtcNow()));
        time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(1, await aggregator.ProcessDueAsync(time.GetUtcNow()));

        var updated = fleet.GetBalloon(0x0101)!;
        Assert.NotEqual("seed", updated.LastMethod);
        Assert.True(updated.Confidence is > 0.3);
        Assert.True(SimulationService.DistanceMeters(
            balloon.Lat!.Value, balloon.Lon!.Value, updated.Lat!.Value, updated.Lon!.Value) < 10.0);

        var historyBefore = fleet.GetHistory(0x0101, 100).Count;
        time.Advance(TimeSpan.FromSeconds(2));
        aggregator.AddReport(0x0101,
            new FtmReport(anchors[0].AnchorId, (ushort)Math.Clamp(
                SimulationService.DistanceMeters(balloon.Lat!.Value, balloon.Lon!.Value,
                    anchors[0].Lat, anchors[0].Lon) * 100.0 + 100.0, 1.0, 65535.0), 200, 1),
            "gateway-a", time.GetUtcNow());
        Assert.Equal(1, await aggregator.ProcessDueAsync(time.GetUtcNow()));
        Assert.True(fleet.GetHistory(0x0101, 100).Count > historyBefore);

        time.Advance(TimeSpan.FromSeconds(19));
        await aggregator.ProcessDueAsync(time.GetUtcNow());
        var historyAfterClose = fleet.GetHistory(0x0101, 100).Count;
        aggregator.AddReport(0x0101,
            new FtmReport(anchors[0].AnchorId, 1234, 300, 1),
            "gateway-a", time.GetUtcNow());
        Assert.Equal(0, await aggregator.ProcessDueAsync(time.GetUtcNow()));
        Assert.Equal(historyAfterClose, fleet.GetHistory(0x0101, 100).Count);
    }

    [Fact]
    public async Task ClosingABatchRemovesItsNeighborObservations()
    {
        using var database = new TemporaryDatabase();
        var fleet = new FleetService(database.Store);
        var neighbors = new NeighborStore();
        var events = new RecordingMonitorEvents();
        var time = new TestTimeProvider(new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero));
        var aggregator = new FtmAggregator(
            fleet, neighbors, new PositionSolver(), events, time,
            NullLogger<FtmAggregator>.Instance);

        neighbors.Add(0x0101, 7, [new NeighborEntry(0x0102, -50)], time.GetUtcNow());
        aggregator.AddReport(0x0101, new FtmReport(1, 100, 100, 7), "gateway-a",
            time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(31));

        await aggregator.ProcessDueAsync(time.GetUtcNow());

        Assert.Empty(neighbors.Get(0x0101, 7));
    }
}
