namespace VisionStudio.Api.Storage.EfCore.Entities;

public sealed class AlarmEntity
{
    public string Id { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public bool Active { get; set; }
    public bool Acknowledged { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? RunId { get; set; }
    public DateTimeOffset? LastRaisedAt { get; set; }
    public int OccurrenceCount { get; set; } = 1;
}

public sealed class CameraFeatureProfileEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public string? SourceCameraId { get; set; }
    public string ProfileHash { get; set; } = string.Empty;
    public string ProfileJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CameraSyncGroupEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public string CameraIdsJson { get; set; } = string.Empty;
    public long DeviceKey { get; set; }
    public long GroupKey { get; set; }
    public long GroupMask { get; set; }
    public string BroadcastAddress { get; set; } = string.Empty;
    public bool RequirePtpLocked { get; set; }
    public long MaxPtpOffsetNs { get; set; }
    public double MaxTriggerSkewUs { get; set; }
    public int ScheduledLeadTimeMs { get; set; }
    public string ConfigHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CameraSyncRunEntity
{
    public string RunId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string GroupConfigHash { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool Scheduled { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public long DurationMs { get; set; }
    public long? ScheduledDeviceTimeNs { get; set; }
    public string TimestampBasis { get; set; } = string.Empty;
    public double? TriggerSkewUs { get; set; }
    public double MaxAllowedSkewUs { get; set; }
    public bool? WithinTolerance { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? Error { get; set; }
    public string? CommandJson { get; set; }
    public string FramesJson { get; set; } = string.Empty;
    public string PtpJson { get; set; } = "[]";
    public string TransportJson { get; set; } = "[]";
}

public sealed class CameraSyncCommissioningTestEntity
{
    public string TestId { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;
    public string GroupConfigHash { get; set; } = string.Empty;
    public bool Scheduled { get; set; }
    public int RequestedIterations { get; set; }
    public int CompletedIterations { get; set; }
    public int FrameTimeoutMs { get; set; }
    public int DelayMs { get; set; }
    public int MinimumSamples { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? ReportJson { get; set; }
}

public sealed class MigrationStateEntity
{
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset CompletedAt { get; set; }
    public string? Detail { get; set; }
}
