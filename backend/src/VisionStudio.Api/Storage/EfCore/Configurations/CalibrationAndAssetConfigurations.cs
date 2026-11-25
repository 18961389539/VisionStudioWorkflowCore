using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Storage.EfCore.Configurations;

internal sealed class CalibrationAssetConfiguration : IEntityTypeConfiguration<CalibrationAssetEntity>
{
    public void Configure(EntityTypeBuilder<CalibrationAssetEntity> b)
    {
        b.ToTable("calibration_assets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LatestVersion).HasColumnName("latest_version");
        b.Property(x => x.PublishedVersion).HasColumnName("published_version");
    }
}

internal sealed class CalibrationVersionConfiguration : IEntityTypeConfiguration<CalibrationVersionEntity>
{
    public void Configure(EntityTypeBuilder<CalibrationVersionEntity> b)
    {
        b.ToTable("calibration_versions");
        b.HasKey(x => new { x.AssetId, x.Version });
        b.Property(x => x.AssetId).HasColumnName("asset_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.SnapshotHash).HasColumnName("snapshot_hash");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.WorkspaceJson).HasColumnName("workspace_json");
        b.Property(x => x.ResultJson).HasColumnName("result_json");

        b.HasOne<CalibrationAssetEntity>()
            .WithMany()
            .HasForeignKey(x => x.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.AssetId, x.CreatedAt }).HasDatabaseName("ix_calibration_versions_created").IsDescending(false, true);
    }
}

internal sealed class CalibrationPublicationHistoryConfiguration : IEntityTypeConfiguration<CalibrationPublicationHistoryEntity>
{
    public void Configure(EntityTypeBuilder<CalibrationPublicationHistoryEntity> b)
    {
        b.ToTable("calibration_publication_history");
        b.HasKey(x => x.Seq);
        b.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
        b.Property(x => x.AssetId).HasColumnName("asset_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Action).HasColumnName("action");
        b.Property(x => x.At).HasColumnName("at");

        b.HasOne<CalibrationAssetEntity>()
            .WithMany()
            .HasForeignKey(x => x.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.AssetId, x.Seq }).HasDatabaseName("ix_calibration_publication_history_asset");
    }
}

internal static class SpecializedCalibrationConfigHelper
{
    public static void ConfigureAsset<TAsset>(EntityTypeBuilder<TAsset> b, string tableName)
        where TAsset : SpecializedCalibrationAssetEntity
    {
        b.ToTable(tableName);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LatestVersion).HasColumnName("latest_version");
        b.Property(x => x.PublishedVersion).HasColumnName("published_version");
    }

    public static void ConfigureVersion<TAsset, TVersion>(EntityTypeBuilder<TVersion> b, string tableName, string indexName)
        where TAsset : SpecializedCalibrationAssetEntity
        where TVersion : SpecializedCalibrationVersionEntity
    {
        b.ToTable(tableName);
        b.HasKey(x => new { x.AssetId, x.Version });
        b.Property(x => x.AssetId).HasColumnName("asset_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.SnapshotHash).HasColumnName("snapshot_hash");
        b.Property(x => x.ModelHash).HasColumnName("model_hash");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.ModelJson).HasColumnName("model_json");
        b.Property(x => x.SourceDocumentJson).HasColumnName("source_document_json");
        b.Property(x => x.SummaryJson).HasColumnName("summary_json");

        b.HasOne<TAsset>()
            .WithMany()
            .HasForeignKey(x => x.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.AssetId, x.CreatedAt }).HasDatabaseName(indexName).IsDescending(false, true);
    }

    public static void ConfigureHistory<TAsset, THistory>(EntityTypeBuilder<THistory> b, string tableName, string indexName)
        where TAsset : SpecializedCalibrationAssetEntity
        where THistory : SpecializedCalibrationHistoryEntity
    {
        b.ToTable(tableName);
        b.HasKey(x => x.Seq);
        b.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
        b.Property(x => x.AssetId).HasColumnName("asset_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Action).HasColumnName("action");
        b.Property(x => x.At).HasColumnName("at");

        b.HasOne<TAsset>()
            .WithMany()
            .HasForeignKey(x => x.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.AssetId, x.Seq }).HasDatabaseName(indexName);
    }
}

internal sealed class CameraIntrinsicAssetConfiguration : IEntityTypeConfiguration<CameraIntrinsicAssetEntity>
{
    public void Configure(EntityTypeBuilder<CameraIntrinsicAssetEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureAsset(b, "camera_intrinsic_assets");
}

internal sealed class CameraIntrinsicVersionConfiguration : IEntityTypeConfiguration<CameraIntrinsicVersionEntity>
{
    public void Configure(EntityTypeBuilder<CameraIntrinsicVersionEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureVersion<CameraIntrinsicAssetEntity, CameraIntrinsicVersionEntity>(
            b, "camera_intrinsic_versions", "ix_camera_intrinsic_versions_created");
}

internal sealed class CameraIntrinsicPublicationHistoryConfiguration : IEntityTypeConfiguration<CameraIntrinsicPublicationHistoryEntity>
{
    public void Configure(EntityTypeBuilder<CameraIntrinsicPublicationHistoryEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureHistory<CameraIntrinsicAssetEntity, CameraIntrinsicPublicationHistoryEntity>(
            b, "camera_intrinsic_publication_history", "ix_camera_intrinsic_publication_history_asset");
}

internal sealed class HandEyeCalibrationAssetConfiguration : IEntityTypeConfiguration<HandEyeCalibrationAssetEntity>
{
    public void Configure(EntityTypeBuilder<HandEyeCalibrationAssetEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureAsset(b, "hand_eye_calibration_assets");
}

internal sealed class HandEyeCalibrationVersionConfiguration : IEntityTypeConfiguration<HandEyeCalibrationVersionEntity>
{
    public void Configure(EntityTypeBuilder<HandEyeCalibrationVersionEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureVersion<HandEyeCalibrationAssetEntity, HandEyeCalibrationVersionEntity>(
            b, "hand_eye_calibration_versions", "ix_hand_eye_calibration_versions_created");
}

internal sealed class HandEyeCalibrationPublicationHistoryConfiguration : IEntityTypeConfiguration<HandEyeCalibrationPublicationHistoryEntity>
{
    public void Configure(EntityTypeBuilder<HandEyeCalibrationPublicationHistoryEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureHistory<HandEyeCalibrationAssetEntity, HandEyeCalibrationPublicationHistoryEntity>(
            b, "hand_eye_calibration_publication_history", "ix_hand_eye_calibration_publication_history_asset");
}

internal sealed class HandEye3DCalibrationAssetConfiguration : IEntityTypeConfiguration<HandEye3DCalibrationAssetEntity>
{
    public void Configure(EntityTypeBuilder<HandEye3DCalibrationAssetEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureAsset(b, "hand_eye3d_calibration_assets");
}

internal sealed class HandEye3DCalibrationVersionConfiguration : IEntityTypeConfiguration<HandEye3DCalibrationVersionEntity>
{
    public void Configure(EntityTypeBuilder<HandEye3DCalibrationVersionEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureVersion<HandEye3DCalibrationAssetEntity, HandEye3DCalibrationVersionEntity>(
            b, "hand_eye3d_calibration_versions", "ix_hand_eye3d_calibration_versions_created");
}

internal sealed class HandEye3DCalibrationPublicationHistoryConfiguration : IEntityTypeConfiguration<HandEye3DCalibrationPublicationHistoryEntity>
{
    public void Configure(EntityTypeBuilder<HandEye3DCalibrationPublicationHistoryEntity> b) =>
        SpecializedCalibrationConfigHelper.ConfigureHistory<HandEye3DCalibrationAssetEntity, HandEye3DCalibrationPublicationHistoryEntity>(
            b, "hand_eye3d_calibration_publication_history", "ix_hand_eye3d_calibration_publication_history_asset");
}

internal sealed class AssetConfiguration : IEntityTypeConfiguration<AssetEntity>
{
    public void Configure(EntityTypeBuilder<AssetEntity> b)
    {
        b.ToTable("assets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Kind).HasColumnName("kind");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LatestVersion).HasColumnName("latest_version");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");

        b.HasIndex(x => new { x.Kind, x.UpdatedAt }).HasDatabaseName("ix_assets_kind_updated").IsDescending(false, true);
    }
}

internal sealed class AssetVersionConfiguration : IEntityTypeConfiguration<AssetVersionEntity>
{
    public void Configure(EntityTypeBuilder<AssetVersionEntity> b)
    {
        b.ToTable("asset_versions");
        b.HasKey(x => new { x.AssetId, x.Version });
        b.Property(x => x.AssetId).HasColumnName("asset_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Sha256).HasColumnName("sha256");
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.MetadataJson).HasColumnName("metadata_json");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");

        b.HasOne<AssetEntity>()
            .WithMany()
            .HasForeignKey(x => x.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.AssetId, x.CreatedAt }).HasDatabaseName("ix_asset_versions_created").IsDescending(false, true);
        b.HasIndex(x => x.Sha256).HasDatabaseName("ix_asset_versions_sha");
    }
}

internal sealed class AssetBlobConfiguration : IEntityTypeConfiguration<AssetBlobEntity>
{
    public void Configure(EntityTypeBuilder<AssetBlobEntity> b)
    {
        b.ToTable("asset_blobs");
        b.HasKey(x => x.Sha256);
        b.Property(x => x.Sha256).HasColumnName("sha256");
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.Bytes).HasColumnName("bytes");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}

internal sealed class ShapeModelAssetConfiguration : IEntityTypeConfiguration<ShapeModelAssetEntity>
{
    public void Configure(EntityTypeBuilder<ShapeModelAssetEntity> b)
    {
        b.ToTable("shape_model_assets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LatestVersion).HasColumnName("latest_version");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
    }
}

internal sealed class ShapeModelVersionConfiguration : IEntityTypeConfiguration<ShapeModelVersionEntity>
{
    public void Configure(EntityTypeBuilder<ShapeModelVersionEntity> b)
    {
        b.ToTable("shape_model_versions");
        b.HasKey(x => new { x.AssetId, x.Version });
        b.Property(x => x.AssetId).HasColumnName("asset_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Sha256).HasColumnName("sha256");
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.TrainingConfig).HasColumnName("training_config");
        b.Property(x => x.SourceImageSha256).HasColumnName("source_image_sha256");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");

        b.HasOne<ShapeModelAssetEntity>()
            .WithMany()
            .HasForeignKey(x => x.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.AssetId, x.CreatedAt }).HasDatabaseName("ix_shape_model_versions_created").IsDescending(false, true);
        b.HasIndex(x => x.Sha256).HasDatabaseName("ix_shape_model_versions_sha");
    }
}

internal sealed class ShapeModelBlobConfiguration : IEntityTypeConfiguration<ShapeModelBlobEntity>
{
    public void Configure(EntityTypeBuilder<ShapeModelBlobEntity> b)
    {
        b.ToTable("shape_model_blobs");
        b.HasKey(x => x.Sha256);
        b.Property(x => x.Sha256).HasColumnName("sha256");
        b.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        b.Property(x => x.Bytes).HasColumnName("bytes");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
    }
}
