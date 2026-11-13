using BalloonMesh.Server.Hubs;
using BalloonMesh.Server.Models;
using BalloonMesh.Protocol;

namespace BalloonMesh.Server.Services;

/// <summary>
/// Development-only mock of the future BLE Mesh onboarding flow.  The real
/// Provisioner (key distribution, model bind, group subscribe) is separate;
/// this service only exercises the UI and SignalR contract.
/// </summary>
public sealed class MockProvisioningService
{
    private readonly object _gate = new();
    private readonly List<MockProvisioningDevice> _devices;
    private readonly FleetService _fleet;
    private readonly IMonitorEvents _notifier;
    private readonly ILogger<MockProvisioningService> _logger;
    private int _session;

    public MockProvisioningService(FleetService fleet, IMonitorEvents notifier,
        ILogger<MockProvisioningService> logger)
    {
        _fleet = fleet;
        _notifier = notifier;
        _logger = logger;
        _devices = CreateInitial();
    }

    public IReadOnlyList<MockProvisioningDevice> Snapshot()
    {
        lock (_gate)
        {
            return _devices.OrderBy(device => device.Id, StringComparer.Ordinal).ToArray();
        }
    }

    public bool Provision(string id, int? groupId)
    {
        bool shouldFail;
        int session;
        lock (_gate)
        {
            session = _session;
            var index = _devices.FindIndex(device =>
                string.Equals(device.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }
            var device = _devices[index];
            if (device.State is "provisioned" or "provisioning")
            {
                return false;
            }
            if (device.Kind == "balloon" && !IsValidBalloonGroup(groupId))
            {
                return false;
            }
            shouldFail = id == "mock-b-03";
            _devices[index] = device with
            {
                State = "provisioning",
                Progress = 5,
                GroupId = groupId,
                Error = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        _ = SimulateAsync(id, shouldFail, session);
        _ = NotifyAsync();
        return true;
    }

    public void Reset()
    {
        lock (_gate)
        {
            _session++;
            _devices.Clear();
            _devices.AddRange(CreateInitial());
        }
        _ = NotifyAsync();
    }

    private async Task SimulateAsync(string id, bool shouldFail, int session)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        if (!Update(id, session, device => device with
            {
                State = "provisioning",
                Progress = 35,
                UpdatedAt = DateTimeOffset.UtcNow
            }))
        {
            return;
        }
        await NotifyAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(1600));
        if (!Update(id, session, device => device with
            {
                State = "provisioning",
                Progress = 70,
                UpdatedAt = DateTimeOffset.UtcNow
            }))
        {
            return;
        }
        await NotifyAsync();

        await Task.Delay(TimeSpan.FromMilliseconds(1800));
        if (shouldFail)
        {
            if (!Update(id, session, device => device with
                {
                    State = "failed",
                    Progress = 70,
                    Error = "Provisioning timed out (mock)",
                    UpdatedAt = DateTimeOffset.UtcNow
                }))
            {
                return;
            }
            _logger.LogWarning("mock provisioning failed for {Id}", id);
        }
        else
        {
            if (!MarkProvisioned(id, session, out var address))
            {
                return;
            }
            _logger.LogInformation("mock provisioning completed for {Id} as 0x{Address:X4}",
                id, address ?? 0);
        }
        await NotifyAsync();
    }

    private bool MarkProvisioned(string id, int session, out ushort? address)
    {
        address = null;
        lock (_gate)
        {
            if (session != _session)
            {
                return false;
            }
            var index = _devices.FindIndex(device =>
                string.Equals(device.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }
            // Allocate and apply under the same lock so two concurrent
            // simulations cannot pick the same address.
            address = NextAddressLocked();
            _devices[index] = _devices[index] with
            {
                State = "provisioned",
                Progress = 100,
                MeshAddress = address,
                Error = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            return true;
        }
    }

    private bool Update(string id, int session,
        Func<MockProvisioningDevice, MockProvisioningDevice> update)
    {
        lock (_gate)
        {
            if (session != _session)
            {
                return false;
            }
            var index = _devices.FindIndex(device =>
                string.Equals(device.Id, id, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }
            _devices[index] = update(_devices[index]);
            return true;
        }
    }

    private ushort? NextAddressLocked()
    {
        var used = _devices
            .Where(device => device.MeshAddress is not null)
            .Select(device => device.MeshAddress!.Value)
            .ToHashSet();
        for (var candidate = 0x0140; candidate <= 0x01FF; candidate++)
        {
            if (!used.Contains((ushort)candidate))
            {
                return (ushort)candidate;
            }
        }
        return null;
    }

    private Task NotifyAsync() => _notifier.OnboardingUpdatedAsync(Snapshot());

    private bool IsValidBalloonGroup(int? groupId)
    {
        if (groupId is null)
        {
            return false;
        }

        var group = _fleet.GetGroups().FirstOrDefault(item => item.Id == groupId.Value);
        return group is not null && group.MeshGroupAddress != BalloonProtocol.GroupLedAll;
    }

    private static List<MockProvisioningDevice> CreateInitial() =>
    [
        new("mock-b-01", "B-Mock-01", "balloon",
            "B1A11000000111223344001122334455", null, null, "unprovisioned", 0, null, DateTimeOffset.UtcNow),
        new("mock-b-02", "B-Mock-02", "balloon",
            "B1A11000000111223344001122334466", 0x0141, 1, "provisioned", 100, null, DateTimeOffset.UtcNow),
        new("mock-b-03", "B-Mock-03", "balloon",
            "B1A11000000111223344001122334477", null, null, "failed", 70,
            "Provisioning timed out (mock)", DateTimeOffset.UtcNow),
        new("mock-b-04", "B-Mock-04", "balloon",
            "B1A11000000111223344001122334488", null, null, "failed", 60,
            "Provisioning was interrupted (mock)", DateTimeOffset.UtcNow),
        new("mock-b-05", "B-Mock-05", "balloon",
            "B1A11000000111223344001122334499", null, null, "unprovisioned", 0, null, DateTimeOffset.UtcNow),
        new("mock-gw-01", "GW-Mock-01", "gateway",
            "B1A110000001112233440011223344AA", null, null, "unprovisioned", 0, null, DateTimeOffset.UtcNow)
    ];
}
