using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Storage.EfCore.Configurations;

internal sealed class AlarmConfiguration : IEntityTypeConfiguration<AlarmEntity>
{
    public void Configure(EntityTypeBuilder<AlarmEntity> b)
    {
        b.ToTable("alarms");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Code).HasColumnName("code");
        b.Property(x => x.Severity).HasColumnName("severity");
        b.Property(x => x.Source).HasColumnName("source");
        b.Property(x => x.Message).HasColumnName("message");
        b.Property(x => x.Active).HasColumnName("active").HasConversion<int>();
        b.Property(x => x.Acknowledged).HasColumnName("acknowledged").HasConversion<int>();
        b.Property(x => x.RaisedAt).HasColumnName("raised_at");
        b.Property(x => x.RecoveredAt).HasColumnName("recovered_at");
        b.Property(x => x.AcknowledgedAt).HasColumnName("acknowledged_at");
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.LastRaisedAt).HasColumnName("last_raised_at");
        b.Property(x => x.OccurrenceCount).HasColumnName("occurrence_count").HasDefaultValue(1);

        b.HasIndex(x => x.RaisedAt).HasDatabaseName("ix_alarms_raised_at").IsDescending();
        b.HasIndex(x => new { x.Code, x.Source })
            .HasDatabaseName("ux_alarms_active_code_source")
            .IsUnique()
            .HasFilter("active = 1");
    }
}

internal sealed class CameraFeatureProfileConfiguration : IEntityTypeConfiguration<CameraFeatureProfileEntity>
{
    public void Configure(EntityTypeBuilder<CameraFeatureProfileEntity> b)
    {
        b.ToTable("camera_feature_profiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Driver).HasColumnName("driver");
        b.Property(x => x.SourceCameraId).HasColumnName("source_camera_id");
        b.Property(x => x.ProfileHash).HasColumnName("profile_hash");
        b.Property(x => x.ProfileJson).HasColumnName("profile_json");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => new { x.Driver, x.Name }).HasDatabaseName("ix_camera_feature_profiles_driver_name");
        b.HasIndex(x => x.ProfileHash).HasDatabaseName("ix_camera_feature_profiles_hash");
    }
}

internal sealed class CameraSyncGroupConfiguration : IEntityTypeConfiguration<CameraSyncGroupEntity>
{
    public void Configure(EntityTypeBuilder<CameraSyncGroupEntity> b)
    {
        b.ToTable("camera_sync_groups");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Driver).HasColumnName("driver");
        b.Property(x => x.CameraIdsJson).HasColumnName("camera_ids_json");
        b.Property(x => x.DeviceKey).HasColumnName("device_key");
        b.Property(x => x.GroupKey).HasColumnName("group_key");
        b.Property(x => x.GroupMask).HasColumnName("group_mask");
        b.Property(x => x.BroadcastAddress).HasColumnName("broadcast_address");
        b.Property(x => x.RequirePtpLocked).HasColumnName("require_ptp_locked").HasConversion<int>();
        b.Property(x => x.MaxPtpOffsetNs).HasColumnName("max_ptp_offset_ns");
        b.Property(x => x.MaxTriggerSkewUs).HasColumnName("max_trigger_skew_us");
        b.Property(x => x.ScheduledLeadTimeMs).HasColumnName("scheduled_lead_time_ms");
        b.Property(x => x.ConfigHash).HasColumnName("config_hash");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => new { x.Driver, x.Name }).HasDatabaseName("ix_camera_sync_groups_driver");
        b.HasIndex(x => x.ConfigHash).HasDatabaseName("ix_camera_sync_groups_hash");
    }
}

internal sealed class CameraSyncRunConfiguration : IEntityTypeConfiguration<CameraSyncRunEntity>
{
    public void Configure(EntityTypeBuilder<CameraSyncRunEntity> b)
    {
        b.ToTable("camera_sync_runs");
        b.HasKey(x => x.RunId);
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.GroupId).HasColumnName("group_id");
        b.Property(x => x.GroupConfigHash).HasColumnName("group_config_hash");
        b.Property(x => x.Source).HasColumnName("source");
        b.Property(x => x.Scheduled).HasColumnName("scheduled").HasConversion<int>();
        b.Property(x => x.RequestedAt).HasColumnName("requested_at");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.DurationMs).HasColumnName("duration_ms");
        b.Property(x => x.ScheduledDeviceTimeNs).HasColumnName("scheduled_device_time_ns");
        b.Property(x => x.TimestampBasis).HasColumnName("timestamp_basis");
        b.Property(x => x.TriggerSkewUs).HasColumnName("trigger_skew_us");
        b.Property(x => x.MaxAllowedSkewUs).HasColumnName("max_allowed_skew_us");
        b.Property(x => x.WithinTolerance).HasColumnName("within_tolerance").HasConversion<int?>();
        b.Property(x => x.Outcome).HasColumnName("outcome");
        b.Property(x => x.Error).HasColumnName("error");
        b.Property(x => x.CommandJson).HasColumnName("command_json");
        b.Property(x => x.FramesJson).HasColumnName("frames_json");
        b.Property(x => x.PtpJson).HasColumnName("ptp_json").HasDefaultValue("[]");
        b.Property(x => x.TransportJson).HasColumnName("transport_json").HasDefaultValue("[]");

        b.HasIndex(x => new { x.GroupId, x.CompletedAt }).HasDatabaseName("ix_camera_sync_runs_group_completed").IsDescending(false, true);
        b.HasIndex(x => new { x.Outcome, x.CompletedAt }).HasDatabaseName("ix_camera_sync_runs_outcome_completed").IsDescending(false, true);
    }
}

internal sealed class CameraSyncCommissioningTestConfiguration : IEntityTypeConfiguration<CameraSyncCommissioningTestEntity>
{
    public void Configure(EntityTypeBuilder<CameraSyncCommissioningTestEntity> b)
    {
        b.ToTable("camera_sync_commissioning_tests");
        b.HasKey(x => x.TestId);
        b.Property(x => x.TestId).HasColumnName("test_id");
        b.Property(x => x.GroupId).HasColumnName("group_id");
        b.Property(x => x.GroupConfigHash).HasColumnName("group_config_hash");
        b.Property(x => x.Scheduled).HasColumnName("scheduled").HasConversion<int>();
        b.Property(x => x.RequestedIterations).HasColumnName("requested_iterations");
        b.Property(x => x.CompletedIterations).HasColumnName("completed_iterations");
        b.Property(x => x.FrameTimeoutMs).HasColumnName("frame_timeout_ms");
        b.Property(x => x.DelayMs).HasColumnName("delay_ms");
        b.Property(x => x.MinimumSamples).HasColumnName("minimum_samples");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.StartedAt).HasColumnName("started_at");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.Error).HasColumnName("error");
        b.Property(x => x.ReportJson).HasColumnName("report_json");

        b.HasIndex(x => new { x.GroupId, x.StartedAt }).HasDatabaseName("ix_camera_sync_commissioning_group_started").IsDescending(false, true);
        b.HasIndex(x => new { x.Status, x.StartedAt }).HasDatabaseName("ix_camera_sync_commissioning_status_started").IsDescending(false, true);
    }
}

internal sealed class MigrationStateConfiguration : IEntityTypeConfiguration<MigrationStateEntity>
{
    public void Configure(EntityTypeBuilder<MigrationStateEntity> b)
    {
        b.ToTable("migration_state");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasColumnName("key");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.Detail).HasColumnName("detail");
    }
}
