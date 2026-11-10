using BalloonMesh.Server.Data;
using BalloonMesh.Server.Models;
using BalloonMesh.Server.Protocol;

namespace BalloonMesh.Server.Services;

public sealed record TargetSpec(ushort Destination, IReadOnlyList<BalloonRecord> Balloons, string Description);

public sealed class FleetService
{
    private readonly DataStore _store;

    public FleetService(DataStore store)
    {
        _store = store;
    }

    public IReadOnlyList<BalloonDto> GetBalloons() =>
        _store.GetBalloons().Select(ToDto).ToArray();

    public BalloonDto? GetBalloon(ushort meshAddress)
    {
        var record = _store.GetBalloon(meshAddress);
        return record is null ? null : ToDto(record);
    }

    public BalloonRecord? GetBalloonRecord(ushort meshAddress) => _store.GetBalloon(meshAddress);

    public IReadOnlyList<AnchorDto> GetAnchors() =>
        _store.GetAnchors().Select(anchor => new AnchorDto(
            anchor.AnchorId, anchor.Name, anchor.Lat, anchor.Lon, anchor.AltM,
            anchor.TimeValid, anchor.PositionValid)).ToArray();

    public AnchorDto UpsertAnchor(byte id, string name, double lat, double lon,
        double altM, bool timeValid, bool positionValid)
    {
        if (id == 0)
        {
            throw new ArgumentException("Anchor id 0 is reserved.");
        }
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            throw new ArgumentException("Anchor latitude/longitude is out of range.");
        }
        var record = new Data.AnchorRecord(id, string.IsNullOrWhiteSpace(name) ? $"Anchor {id}" : name,
            lat, lon, altM, timeValid, positionValid, DateTimeOffset.UtcNow);
        _store.UpsertAnchor(record);
        return new AnchorDto(id, record.Name, lat, lon, altM, timeValid, positionValid);
    }

    public IReadOnlyList<GroupDto> GetGroups()
    {
        var balloons = _store.GetBalloons();
        return _store.GetGroups().Select(group =>
        {
            var count = group.MeshGroupAddress == BalloonProtocol.GroupLedAll
                ? balloons.Count
                : balloons.Count(b => b.GroupId == group.Id);
            return new GroupDto(group.Id, group.Name, group.MeshGroupAddress, count);
        }).ToArray();
    }

    public GroupDto CreateGroup(string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Group name is required.");
        }
        var groups = _store.GetGroups();
        var used = groups.Select(group => group.MeshGroupAddress).ToHashSet();
        for (var offset = 0; offset < 16; offset++)
        {
            var address = (ushort)(0xC100 + offset);
            if (!used.Contains(address))
            {
                var created = _store.CreateGroup(name, address);
                return new GroupDto(created.Id, created.Name, created.MeshGroupAddress, 0);
            }
        }
        throw new InvalidOperationException("No free zone group address remains (0xC100-0xC10F).");
    }

    public bool DeleteGroup(int id)
    {
        if (_store.GetGroup(id) is null)
        {
            return false;
        }
        return _store.DeleteGroup(id);
    }

    public BalloonDto AssignBalloonToGroup(ushort meshAddress, int groupId)
    {
        var group = _store.GetGroup(groupId)
            ?? throw new ArgumentException($"Group {groupId} was not found.");
        if (group.MeshGroupAddress == BalloonProtocol.GroupLedAll)
        {
            throw new ArgumentException("The virtual all-balloons group cannot be assigned directly.");
        }
        if (!_store.AssignBalloonToGroup(meshAddress, groupId))
        {
            throw new ArgumentException($"Balloon {meshAddress:X4} could not be moved to that group.");
        }
        return GetBalloon(meshAddress)
            ?? throw new InvalidOperationException($"Balloon {meshAddress:X4} was not found.");
    }

    public StateDto GetState(IReadOnlyList<GatewayDto> gateways, bool simulationRunning,
        string version, EffectStateDto? effect = null) => new(
        DateTimeOffset.UtcNow,
        version,
        simulationRunning,
        GetBalloons(),
        GetAnchors(),
        gateways,
        GetGroups(),
        effect);

    public BalloonDto SetInitialPosition(ushort meshAddress, double lat, double lon)
    {
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(lat), "Latitude/longitude is out of range.");
        }
        _store.SetInitialPosition(meshAddress, lat, lon);
        return GetBalloon(meshAddress)
            ?? throw new InvalidOperationException($"Balloon {meshAddress:X4} was not found.");
    }

    public BalloonDto ApplyPosition(ushort meshAddress, double lat, double lon,
        double confidence, byte batchId, string method, DateTimeOffset measuredAt)
    {
        _store.UpdatePosition(meshAddress, lat, lon, confidence, batchId, method, measuredAt);
        return GetBalloon(meshAddress)
            ?? throw new InvalidOperationException($"Balloon {meshAddress:X4} was not found.");
    }

    public BalloonDto ApplyQueueStatus(ushort meshAddress, ushort queueCount)
    {
        _store.UpdateQueueCount(meshAddress, queueCount);
        return GetBalloon(meshAddress)
            ?? throw new InvalidOperationException($"Balloon {meshAddress:X4} was not found.");
    }

    public BalloonDto ApplyColor(ushort meshAddress, byte r, byte g, byte b)
    {
        _store.UpdateColor(meshAddress, r, g, b);
        return GetBalloon(meshAddress)
            ?? throw new InvalidOperationException($"Balloon {meshAddress:X4} was not found.");
    }

    public void UpdateEstimatedQueue(ushort meshAddress, int estimated) =>
        _store.UpdateEstimatedQueueCount(meshAddress, estimated);

    public IReadOnlyList<PositionSampleRecord> GetHistory(ushort meshAddress, int limit) =>
        _store.GetHistory(meshAddress, limit);

    public TargetSpec ResolveTarget(string kind, string target)
    {
        var normalized = kind.Trim().ToLowerInvariant();
        if (normalized is "all" or "everyone")
        {
            return new TargetSpec(
                BalloonProtocol.GroupLedAll,
                _store.GetBalloons(),
                "all balloons");
        }

        if (normalized is "group" or "zone")
        {
            throw new ArgumentException("Zone targets are no longer supported. Choose all balloons or an individual balloon.");
        }

        if (normalized is "balloon" or "node")
        {
            if (!TryParseUShort(target, out var address))
            {
                throw new ArgumentException($"Balloon address '{target}' is invalid.");
            }
            var balloon = _store.GetBalloon(address)
                ?? throw new ArgumentException($"Balloon '{target}' was not found.");
            return new TargetSpec(address, [balloon], $"balloon {balloon.Name}");
        }

        throw new ArgumentException($"Unknown target kind '{kind}'.");
    }

    private static bool TryParseUShort(string text, out ushort value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }
        return ushort.TryParse(text, out value);
    }

    private static BalloonDto ToDto(BalloonRecord balloon)
    {
        var updated = balloon.UpdatedAt ?? DateTimeOffset.MinValue;
        var online = updated > DateTimeOffset.UtcNow.AddMinutes(-15);
        return new BalloonDto(
            balloon.MeshAddress,
            balloon.Name,
            balloon.GroupId,
            balloon.GroupName,
            balloon.Lat,
            balloon.Lon,
            balloon.Confidence,
            balloon.R,
            balloon.G,
            balloon.B,
            balloon.QueueCount,
            balloon.EstimatedQueueCount,
            balloon.LastMethod,
            online);
    }
}
