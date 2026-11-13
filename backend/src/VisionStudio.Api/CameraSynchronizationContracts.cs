using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VisionStudio.Engine.Camera;
using VisionStudio.Abstractions;

namespace VisionStudio.Api;

public sealed record CameraSynchronizationGroup(
    string Id,
    string Name,
    string Driver,
    IReadOnlyList<string> CameraIds,
    int DeviceKey,
    int GroupKey,
    int GroupMask,
    string BroadcastAddress,
    bool RequirePtpLocked,
    long MaxPtpOffsetNs,
    double MaxTriggerSkewUs,
    int ScheduledLeadTimeMs,
    string ConfigurationHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CameraSynchronizationGroupRequest(
    string Id,
    string Name,
    string Driver,
    IReadOnlyList<string> CameraIds,
    int DeviceKey = 1,
    int GroupKey = 1,
    int GroupMask = -1,
    string BroadcastAddress = "255.255.255.255",
    bool RequirePtpLocked = true,
    long MaxPtpOffsetNs = 1_000_000,
    double MaxTriggerSkewUs = 100,
    int ScheduledLeadTimeMs = 100);

public sealed record CameraSynchronizationMemberStatus(
    string CameraId,
    string Driver,
    CameraState CameraState,
    CameraAcquisitionState AcquisitionState,
    CameraTimeSynchronizationStatus TimeSync,
    CameraFrameTimingSnapshot? LatestFrame);

public sealed record CameraSynchronizationGroupStatus(
    CameraSynchronizationGroup Group,
    bool Ready,
    bool PtpReady,
    string? Error,
    IReadOnlyList<CameraSynchronizationMemberStatus> Members,
    double? LatestSkewUs,
    string TimestampBasis);

public sealed record CameraSynchronizationTriggerRequest(
    bool Scheduled = false,
    int? LeadTimeMs = null,
    int FrameTimeoutMs = 3000);

public sealed record CameraSynchronizationFrameResult(
    string CameraId,
    long Sequence,
    DateTimeOffset HostTimestamp,
    long? DeviceTimestampNs,
    long? TriggerId);

public sealed record CameraSynchronizationCaptureResult(
    string GroupId,
    bool Scheduled,
    long? ScheduledDeviceTimeNs,
    CameraActionCommandResult Command,
    IReadOnlyList<CameraSynchronizationFrameResult> Frames,
    double TriggerSkewUs,
    string TimestampBasis,
    bool WithinTolerance,
    double MaxAllowedSkewUs,
    DateTimeOffset CompletedAt);
