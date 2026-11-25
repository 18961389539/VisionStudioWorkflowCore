using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Storage.EfCore.Configurations;

internal sealed class ValidationDatasetConfiguration : IEntityTypeConfiguration<ValidationDatasetEntity>
{
    public void Configure(EntityTypeBuilder<ValidationDatasetEntity> b)
    {
        b.ToTable("validation_datasets");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.TopologyHash).HasColumnName("topology_hash");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => x.UpdatedAt).HasDatabaseName("ix_validation_datasets_updated").IsDescending();
    }
}

internal sealed class ValidationDatasetItemConfiguration : IEntityTypeConfiguration<ValidationDatasetItemEntity>
{
    public void Configure(EntityTypeBuilder<ValidationDatasetItemEntity> b)
    {
        b.ToTable("validation_dataset_items");
        b.HasKey(x => new { x.DatasetId, x.ItemId });
        b.Property(x => x.DatasetId).HasColumnName("dataset_id");
        b.Property(x => x.ItemId).HasColumnName("item_id");
        b.Property(x => x.SourceKind).HasColumnName("source_kind");
        b.Property(x => x.SourceRef).HasColumnName("source_ref");
        b.Property(x => x.ExpectedDisposition).HasColumnName("expected_disposition");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.AddedAt).HasColumnName("added_at");

        b.HasOne<ValidationDatasetEntity>()
            .WithMany()
            .HasForeignKey(x => x.DatasetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.DatasetId, x.SourceKind, x.SourceRef }).IsUnique();
        b.HasIndex(x => new { x.SourceKind, x.SourceRef }).HasDatabaseName("ix_validation_dataset_items_source");
    }
}

internal sealed class ValidationRunConfiguration : IEntityTypeConfiguration<ValidationRunEntity>
{
    public void Configure(EntityTypeBuilder<ValidationRunEntity> b)
    {
        b.ToTable("validation_runs");
        b.HasKey(x => x.RunId);
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.DatasetId).HasColumnName("dataset_id");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.SourceWorkflowRunId).HasColumnName("source_workflow_run_id");
        b.Property(x => x.WorkflowHash).HasColumnName("workflow_hash");
        b.Property(x => x.CandidateWorkflowJson).HasColumnName("candidate_workflow_json");
        b.Property(x => x.RequestedCount).HasColumnName("requested_count");
        b.Property(x => x.CompletedCount).HasColumnName("completed_count");
        b.Property(x => x.StartedAt).HasColumnName("started_at");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.CancelRequested).HasColumnName("cancel_requested").HasConversion<int>().HasDefaultValue(false);
        b.Property(x => x.Error).HasColumnName("error");
        b.Property(x => x.SummaryJson).HasColumnName("summary_json");

        b.HasOne<ValidationDatasetEntity>()
            .WithMany()
            .HasForeignKey(x => x.DatasetId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.DatasetId, x.StartedAt }).HasDatabaseName("ix_validation_runs_dataset_started").IsDescending(false, true);
        b.HasIndex(x => new { x.Status, x.StartedAt }).HasDatabaseName("ix_validation_runs_status_started").IsDescending(false, true);
    }
}

internal sealed class ValidationResultConfiguration : IEntityTypeConfiguration<ValidationResultEntity>
{
    public void Configure(EntityTypeBuilder<ValidationResultEntity> b)
    {
        b.ToTable("validation_results");
        b.HasKey(x => new { x.RunId, x.ItemId });
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.ItemId).HasColumnName("item_id");
        b.Property(x => x.SourceKind).HasColumnName("source_kind");
        b.Property(x => x.SourceRef).HasColumnName("source_ref");
        b.Property(x => x.ExpectedDisposition).HasColumnName("expected_disposition");
        b.Property(x => x.ActualDisposition).HasColumnName("actual_disposition");
        b.Property(x => x.Classification).HasColumnName("classification");
        b.Property(x => x.ReplayRunId).HasColumnName("replay_run_id");
        b.Property(x => x.Success).HasColumnName("success").HasConversion<int>();
        b.Property(x => x.DurationMs).HasColumnName("duration_ms");
        b.Property(x => x.FailedNodesJson).HasColumnName("failed_nodes_json");
        b.Property(x => x.Error).HasColumnName("error");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");

        b.HasOne<ValidationRunEntity>()
            .WithMany()
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.RunId, x.Classification }).HasDatabaseName("ix_validation_results_classification");
        b.HasIndex(x => new { x.SourceKind, x.SourceRef }).HasDatabaseName("ix_validation_results_source");
    }
}

internal sealed class PluginBenchmarkRunConfiguration : IEntityTypeConfiguration<PluginBenchmarkRunEntity>
{
    public void Configure(EntityTypeBuilder<PluginBenchmarkRunEntity> b)
    {
        b.ToTable("plugin_benchmark_runs");
        b.HasKey(x => x.RunId);
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.DatasetId).HasColumnName("dataset_id");
        b.Property(x => x.ValidationRunId).HasColumnName("validation_run_id");
        b.Property(x => x.PluginId).HasColumnName("plugin_id");
        b.Property(x => x.PluginVersion).HasColumnName("plugin_version");
        b.Property(x => x.PluginAssemblySha256).HasColumnName("plugin_assembly_sha256");
        b.Property(x => x.WorkflowHash).HasColumnName("workflow_hash");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.PoolSizesJson).HasColumnName("pool_sizes_json");
        b.Property(x => x.RequestedCount).HasColumnName("requested_count");
        b.Property(x => x.WarmupCount).HasColumnName("warmup_count");
        b.Property(x => x.WorkloadConcurrency).HasColumnName("workload_concurrency");
        b.Property(x => x.BaselineRunId).HasColumnName("baseline_run_id");
        b.Property(x => x.PolicyJson).HasColumnName("policy_json");
        b.Property(x => x.StartedAt).HasColumnName("started_at");
        b.Property(x => x.CompletedAt).HasColumnName("completed_at");
        b.Property(x => x.CancelRequested).HasColumnName("cancel_requested").HasConversion<int>().HasDefaultValue(false);
        b.Property(x => x.Error).HasColumnName("error");
        b.Property(x => x.RecommendationJson).HasColumnName("recommendation_json");
        b.Property(x => x.RegressionJson).HasColumnName("regression_json");

        b.HasOne<ValidationDatasetEntity>()
            .WithMany()
            .HasForeignKey(x => x.DatasetId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<ValidationRunEntity>()
            .WithMany()
            .HasForeignKey(x => x.ValidationRunId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<PluginBenchmarkRunEntity>()
            .WithMany()
            .HasForeignKey(x => x.BaselineRunId)
            .OnDelete(DeleteBehavior.SetNull);

        b.HasIndex(x => new { x.PluginId, x.StartedAt }).HasDatabaseName("ix_plugin_benchmark_plugin_started").IsDescending(false, true);
        b.HasIndex(x => new { x.DatasetId, x.StartedAt }).HasDatabaseName("ix_plugin_benchmark_dataset_started").IsDescending(false, true);
    }
}

internal sealed class PluginBenchmarkPoolResultConfiguration : IEntityTypeConfiguration<PluginBenchmarkPoolResultEntity>
{
    public void Configure(EntityTypeBuilder<PluginBenchmarkPoolResultEntity> b)
    {
        b.ToTable("plugin_benchmark_pool_results");
        b.HasKey(x => new { x.RunId, x.PoolSize });
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.PoolSize).HasColumnName("pool_size");
        b.Property(x => x.ItemCount).HasColumnName("item_count");
        b.Property(x => x.SuccessCount).HasColumnName("success_count");
        b.Property(x => x.FailureCount).HasColumnName("failure_count");
        b.Property(x => x.FailureRate).HasColumnName("failure_rate");
        b.Property(x => x.TotalDurationMs).HasColumnName("total_duration_ms");
        b.Property(x => x.ThroughputPerSecond).HasColumnName("throughput_per_second");
        b.Property(x => x.ObservedWorkingSetBytes).HasColumnName("observed_working_set_bytes");
        b.Property(x => x.WorkflowDurationJson).HasColumnName("workflow_duration_json");
        b.Property(x => x.WorkerPerformanceJson).HasColumnName("worker_performance_json");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasOne<PluginBenchmarkRunEntity>()
            .WithMany()
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.RunId, x.PoolSize }).HasDatabaseName("ix_plugin_benchmark_pool_run");
    }
}
