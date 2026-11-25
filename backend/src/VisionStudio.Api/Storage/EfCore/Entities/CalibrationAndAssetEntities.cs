namespace VisionStudio.Api.Storage.EfCore.Entities;

public sealed class CalibrationAssetEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int LatestVersion { get; set; }
    public int? PublishedVersion { get; set; }
}

public sealed class CalibrationVersionEntity
{
    public string AssetId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Note { get; set; } = string.Empty;
    public string WorkspaceJson { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
}

public sealed class CalibrationPublicationHistoryEntity
{
    public long Seq { get; set; }
    public string AssetId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

public abstract class SpecializedCalibrationAssetEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int LatestVersion { get; set; }
    public int? PublishedVersion { get; set; }
}

public abstract class SpecializedCalibrationVersionEntity
{
    public string AssetId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string SnapshotHash { get; set; } = string.Empty;
    public string ModelHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Note { get; set; } = string.Empty;
    public string ModelJson { get; set; } = string.Empty;
    public string SourceDocumentJson { get; set; } = string.Empty;
    public string SummaryJson { get; set; } = string.Empty;
}

public abstract class SpecializedCalibrationHistoryEntity
{
    public long Seq { get; set; }
    public string AssetId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
}

public sealed class CameraIntrinsicAssetEntity : SpecializedCalibrationAssetEntity;
public sealed class CameraIntrinsicVersionEntity : SpecializedCalibrationVersionEntity;
public sealed class CameraIntrinsicPublicationHistoryEntity : SpecializedCalibrationHistoryEntity;

public sealed class HandEyeCalibrationAssetEntity : SpecializedCalibrationAssetEntity;
public sealed class HandEyeCalibrationVersionEntity : SpecializedCalibrationVersionEntity;
public sealed class HandEyeCalibrationPublicationHistoryEntity : SpecializedCalibrationHistoryEntity;

public sealed class HandEye3DCalibrationAssetEntity : SpecializedCalibrationAssetEntity;
public sealed class HandEye3DCalibrationVersionEntity : SpecializedCalibrationVersionEntity;
public sealed class HandEye3DCalibrationPublicationHistoryEntity : SpecializedCalibrationHistoryEntity;

public sealed class AssetEntity
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int LatestVersion { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class AssetVersionEntity
{
    public string AssetId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string MetadataJson { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class AssetBlobEntity
{
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[] Bytes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ShapeModelAssetEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int LatestVersion { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ShapeModelVersionEntity
{
    public string AssetId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string TrainingConfig { get; set; } = string.Empty;
    public string SourceImageSha256 { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public sealed class ShapeModelBlobEntity
{
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public byte[] Bytes { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}
