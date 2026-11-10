using SvelteSharp.Models;

namespace BalloonMesh.Server.Models;

[SvelteModel(Name = "HomePage")]
public sealed record HomePageModel(
    string Title,
    string Version,
    string Subtitle,
    string UserName,
    string UserProvider,
    string? UserAvatar,
    bool CanOperate,
    bool CanAdmin);

[SvelteModel(Name = "LoginPage")]
public sealed record LoginPageModel(
    string Title,
    bool OidcEnabled,
    bool DiscordEnabled,
    bool MockEnabled,
    string? Error);

public sealed record MockProvisioningDevice(
    string Id,
    string Name,
    string Kind,
    string Uuid,
    ushort? MeshAddress,
    string State,
    int Progress,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed record BalloonDto(
    ushort MeshAddress,
    string Name,
    int GroupId,
    string GroupName,
    double? Lat,
    double? Lon,
    double? Confidence,
    byte R,
    byte G,
    byte B,
    int QueueCount,
    int EstimatedQueueCount,
    string? LastMethod,
    bool Online);

public sealed record AnchorDto(
    byte AnchorId,
    string Name,
    double Lat,
    double Lon,
    double AltM,
    bool TimeValid,
    bool PositionValid);

public sealed record GatewayDto(
    string Id,
    bool WebSocket,
    string? Remote,
    string? Firmware,
    DateTimeOffset LastSeen,
    int PendingCommands,
    bool Provisioned,
    int SpoolPending);

public sealed record GroupDto(int Id, string Name, ushort MeshGroupAddress, int BalloonCount);

public sealed record StateDto(
    DateTimeOffset ServerTime,
    string Version,
    bool SimulationRunning,
    IReadOnlyList<BalloonDto> Balloons,
    IReadOnlyList<AnchorDto> Anchors,
    IReadOnlyList<GatewayDto> Gateways,
    IReadOnlyList<GroupDto> Groups,
    EffectStateDto? Effect = null);

public sealed record CommandResultDto(
    bool Accepted,
    string Message,
    int Gateways,
    int Balloons,
    string? Opcode,
    string? Payload);

public sealed record LedSetRequest(
    string TargetKind,
    string Target,
    DateTimeOffset ExecuteAt,
    byte R,
    byte G,
    byte B,
    string? Name = null);

public sealed record LedNowRequest(
    string TargetKind,
    string Target,
    byte R,
    byte G,
    byte B);

public sealed record LedCancelRequest(
    string TargetKind,
    string Target,
    DateTimeOffset ExecuteAt);

public sealed record LedResetRequest(
    string TargetKind,
    string Target,
    DateTimeOffset? CutoffAt,
    bool SetNow,
    byte R = 0,
    byte G = 0,
    byte B = 0);

/// <summary>
/// Abstract light effect.  The server expands this into per-balloon LED
/// commands, so the UI never has to describe individual layers.
/// </summary>
public sealed record EffectStartRequest(
    string Kind,
    string? Origin = null,
    double? Speed = null,
    double? Wavelength = null,
    double? Width = null,
    int? DirectionDeg = null,
    byte R = 56,
    byte G = 189,
    byte B = 248,
    byte R2 = 0,
    byte G2 = 0,
    byte B2 = 0,
    double? DurationSeconds = null,
    int? FrameIntervalMs = null,
    bool ClearOnStop = false);

public sealed record EffectStopRequest(bool Clear = false);

public sealed record EffectStateDto(
    string Kind,
    string Label,
    bool Running,
    string? OriginAddress,
    string? OriginName,
    double Speed,
    double Wavelength,
    double Width,
    int DirectionDeg,
    byte R,
    byte G,
    byte B,
    byte R2,
    byte G2,
    byte B2,
    int FrameIntervalMs,
    double DurationSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndsAt);

public sealed record InitialPositionRequest(double Lat, double Lon);

public sealed record CreateGroupRequest(string Name);

public sealed record AnchorUpdateRequest(
    string? Name,
    double Lat,
    double Lon,
    double AltM,
    bool TimeValid = true,
    bool PositionValid = true);

public sealed record AssignGroupRequest(int GroupId);

public sealed record GatewayEventDto(
    ushort Src,
    ushort Dst,
    uint Opcode,
    string Payload,
    int Rssi,
    int Ttl,
    long? ReceivedMs);

public sealed record GatewayEventsRequest(
    string GatewayId,
    string? Firmware,
    IReadOnlyList<GatewayEventDto> Events,
    bool? Provisioned = null,
    int? SpoolPending = null);

public sealed record GatewayCommandDto(ushort Dst, uint Opcode, string Payload, int Ttl);

public sealed record GatewayCommandsRequest(IReadOnlyList<GatewayCommandDto> Commands);

public sealed record DownlinkCommand(ushort Dst, uint Opcode, byte[] Payload, int Ttl)
{
    public GatewayCommandDto ToDto() => new(Dst, Opcode, Convert.ToHexString(Payload), Ttl);
}

public sealed record MeshEnvelope(
    string GatewayId,
    ushort Src,
    ushort Dst,
    uint Opcode,
    byte[] Payload,
    int Rssi,
    int Ttl,
    DateTimeOffset ReceivedAt);
