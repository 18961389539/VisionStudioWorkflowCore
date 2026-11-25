using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Storage.EfCore.Configurations;

internal sealed class JobConfiguration : IEntityTypeConfiguration<JobEntity>
{
    public void Configure(EntityTypeBuilder<JobEntity> b)
    {
        b.ToTable("jobs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LatestVersion).HasColumnName("latest_version");
        b.Property(x => x.PublishedVersion).HasColumnName("published_version");
        b.Property(x => x.PublishedDependencyManifestHash).HasColumnName("published_dependency_manifest_hash");
        b.Property(x => x.ProductId).HasColumnName("product_id");
        b.Property(x => x.RecipeCode).HasColumnName("recipe_code");
        b.Property(x => x.RequiresValidation).HasColumnName("requires_validation").HasConversion<int>().HasDefaultValue(false);

        b.HasIndex(x => new { x.ProductId, x.RecipeCode })
            .HasDatabaseName("ux_jobs_product_recipe_code")
            .IsUnique()
            .HasFilter("product_id IS NOT NULL AND recipe_code IS NOT NULL");
    }
}

internal sealed class JobVersionConfiguration : IEntityTypeConfiguration<JobVersionEntity>
{
    public void Configure(EntityTypeBuilder<JobVersionEntity> b)
    {
        b.ToTable("job_versions");
        b.HasKey(x => new { x.JobId, x.Version });
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.WorkflowHash).HasColumnName("workflow_hash");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.WorkflowJson).HasColumnName("workflow_json");
        b.Property(x => x.BaseWorkflowHash).HasColumnName("base_workflow_hash");
        b.Property(x => x.BaseWorkflowJson).HasColumnName("base_workflow_json");
        b.Property(x => x.ParameterBindingsJson).HasColumnName("parameter_bindings_json").HasDefaultValue("{}");
        b.Property(x => x.ParameterSnapshotJson).HasColumnName("parameter_snapshot_json").HasDefaultValue("{}");

        b.HasOne<JobEntity>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.JobId, x.CreatedAt }).HasDatabaseName("ix_job_versions_created").IsDescending(false, true);
    }
}

internal sealed class JobPublicationHistoryConfiguration : IEntityTypeConfiguration<JobPublicationHistoryEntity>
{
    public void Configure(EntityTypeBuilder<JobPublicationHistoryEntity> b)
    {
        b.ToTable("job_publication_history");
        b.HasKey(x => x.Seq);
        b.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.Action).HasColumnName("action");
        b.Property(x => x.At).HasColumnName("at");
        b.Property(x => x.DependencyManifestHash).HasColumnName("dependency_manifest_hash");

        b.HasOne<JobEntity>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.JobId, x.Seq }).HasDatabaseName("ix_job_publication_history_job");
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<ProductEntity>
{
    public void Configure(EntityTypeBuilder<ProductEntity> b)
    {
        b.ToTable("products");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => x.UpdatedAt).HasDatabaseName("ix_products_updated").IsDescending();
    }
}

internal sealed class JobVersionValidationConfiguration : IEntityTypeConfiguration<JobVersionValidationEntity>
{
    public void Configure(EntityTypeBuilder<JobVersionValidationEntity> b)
    {
        b.ToTable("job_version_validations");
        b.HasKey(x => new { x.JobId, x.Version });
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.ValidationRunId).HasColumnName("validation_run_id");
        b.Property(x => x.DatasetId).HasColumnName("dataset_id");
        b.Property(x => x.WorkflowHash).HasColumnName("workflow_hash");
        b.Property(x => x.Accepted).HasColumnName("accepted").HasConversion<int>();
        b.Property(x => x.PolicyJson).HasColumnName("policy_json");
        b.Property(x => x.SummaryJson).HasColumnName("summary_json");
        b.Property(x => x.Reason).HasColumnName("reason");
        b.Property(x => x.LinkedAt).HasColumnName("linked_at");

        b.HasOne<JobVersionEntity>()
            .WithOne()
            .HasForeignKey<JobVersionValidationEntity>(x => new { x.JobId, x.Version })
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<ValidationRunEntity>()
            .WithMany()
            .HasForeignKey(x => x.ValidationRunId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.ValidationRunId).HasDatabaseName("ix_job_version_validations_run");
        b.HasIndex(x => new { x.JobId, x.Accepted, x.LinkedAt }).HasDatabaseName("ix_job_version_validations_accepted").IsDescending(false, false, true);
    }
}

internal sealed class ProductParameterValueConfiguration : IEntityTypeConfiguration<ProductParameterValueEntity>
{
    public void Configure(EntityTypeBuilder<ProductParameterValueEntity> b)
    {
        b.ToTable("product_parameter_values");
        b.HasKey(x => new { x.ProductId, x.Key });
        b.Property(x => x.ProductId).HasColumnName("product_id");
        b.Property(x => x.Key).HasColumnName("key");
        b.Property(x => x.ValueJson).HasColumnName("value_json");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasOne<ProductEntity>()
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.ProductId, x.Key }).HasDatabaseName("ix_product_parameter_values_product");
    }
}

internal sealed class RecipeParameterValueConfiguration : IEntityTypeConfiguration<RecipeParameterValueEntity>
{
    public void Configure(EntityTypeBuilder<RecipeParameterValueEntity> b)
    {
        b.ToTable("recipe_parameter_values");
        b.HasKey(x => new { x.JobId, x.Key });
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.Key).HasColumnName("key");
        b.Property(x => x.ValueJson).HasColumnName("value_json");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasOne<JobEntity>()
            .WithMany()
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.JobId, x.Key }).HasDatabaseName("ix_recipe_parameter_values_job");
    }
}

internal sealed class WorkflowModuleConfiguration : IEntityTypeConfiguration<WorkflowModuleEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowModuleEntity> b)
    {
        b.ToTable("workflow_modules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Name).HasColumnName("name");
        b.Property(x => x.Description).HasColumnName("description");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.LatestVersion).HasColumnName("latest_version");

        b.HasIndex(x => x.UpdatedAt).HasDatabaseName("ix_workflow_modules_updated").IsDescending();
    }
}

internal sealed class WorkflowModuleVersionConfiguration : IEntityTypeConfiguration<WorkflowModuleVersionEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowModuleVersionEntity> b)
    {
        b.ToTable("workflow_module_versions");
        b.HasKey(x => new { x.ModuleId, x.Version });
        b.Property(x => x.ModuleId).HasColumnName("module_id");
        b.Property(x => x.Version).HasColumnName("version");
        b.Property(x => x.ModuleHash).HasColumnName("module_hash");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.WorkflowJson).HasColumnName("workflow_json");
        b.Property(x => x.InputsJson).HasColumnName("inputs_json");
        b.Property(x => x.OutputsJson).HasColumnName("outputs_json");
        b.Property(x => x.ParametersJson).HasColumnName("parameters_json");

        b.HasOne<WorkflowModuleEntity>()
            .WithMany()
            .HasForeignKey(x => x.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.ModuleId, x.CreatedAt }).HasDatabaseName("ix_workflow_module_versions_created").IsDescending(false, true);
        b.HasIndex(x => x.ModuleHash).HasDatabaseName("ix_workflow_module_versions_hash");
    }
}

internal sealed class RuntimeDependencyManifestConfiguration : IEntityTypeConfiguration<RuntimeDependencyManifestEntity>
{
    public void Configure(EntityTypeBuilder<RuntimeDependencyManifestEntity> b)
    {
        b.ToTable("runtime_dependency_manifests");
        b.HasKey(x => x.ManifestHash);
        b.Property(x => x.ManifestHash).HasColumnName("manifest_hash");
        b.Property(x => x.SchemaVersion).HasColumnName("schema_version");
        b.Property(x => x.CapturedAt).HasColumnName("captured_at");
        b.Property(x => x.ManifestJson).HasColumnName("manifest_json");
    }
}

internal sealed class RecipeDeliveryReceiptConfiguration : IEntityTypeConfiguration<RecipeDeliveryReceiptEntity>
{
    public void Configure(EntityTypeBuilder<RecipeDeliveryReceiptEntity> b)
    {
        b.ToTable("recipe_delivery_receipts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.StationId).HasColumnName("station_id");
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.SourceVersion).HasColumnName("source_version");
        b.Property(x => x.WorkflowHash).HasColumnName("workflow_hash");
        b.Property(x => x.LocalVersion).HasColumnName("local_version");
        b.Property(x => x.RequiresValidation).HasColumnName("requires_validation").HasConversion<int>();
        b.Property(x => x.ReceivedAt).HasColumnName("received_at");

        b.HasOne<JobVersionEntity>()
            .WithMany()
            .HasForeignKey(x => new { x.JobId, x.LocalVersion })
            .OnDelete(DeleteBehavior.NoAction);

        b.HasIndex(x => new { x.StationId, x.JobId, x.SourceVersion, x.WorkflowHash, x.RequiresValidation }).IsUnique();
        b.HasIndex(x => new { x.JobId, x.LocalVersion }).IsUnique();
        b.HasIndex(x => x.ReceivedAt).HasDatabaseName("ix_recipe_delivery_received_at").IsDescending();
    }
}

internal sealed class RecipeRolloutBatchConfiguration : IEntityTypeConfiguration<RecipeRolloutBatchEntity>
{
    public void Configure(EntityTypeBuilder<RecipeRolloutBatchEntity> b)
    {
        b.ToTable("recipe_rollout_batches");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.JobName).HasColumnName("job_name");
        b.Property(x => x.SourceVersion).HasColumnName("source_version");
        b.Property(x => x.WorkflowHash).HasColumnName("workflow_hash");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_recipe_rollout_created_at").IsDescending();
    }
}

internal sealed class RecipeRolloutStationConfiguration : IEntityTypeConfiguration<RecipeRolloutStationEntity>
{
    public void Configure(EntityTypeBuilder<RecipeRolloutStationEntity> b)
    {
        b.ToTable("recipe_rollout_stations");
        b.HasKey(x => new { x.BatchId, x.StationId });
        b.Property(x => x.BatchId).HasColumnName("batch_id");
        b.Property(x => x.StationId).HasColumnName("station_id");
        b.Property(x => x.StationName).HasColumnName("station_name");
        b.Property(x => x.Outcome).HasColumnName("outcome");
        b.Property(x => x.LocalVersion).HasColumnName("local_version");
        b.Property(x => x.Detail).HasColumnName("detail");

        b.HasOne<RecipeRolloutBatchEntity>()
            .WithMany()
            .HasForeignKey(x => x.BatchId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class RecipeRolloutLatestStationConfiguration : IEntityTypeConfiguration<RecipeRolloutLatestStationEntity>
{
    public void Configure(EntityTypeBuilder<RecipeRolloutLatestStationEntity> b)
    {
        b.ToTable("recipe_rollout_latest_station");
        b.HasKey(x => x.StationId);
        b.Property(x => x.StationId).HasColumnName("station_id");
        b.Property(x => x.BatchId).HasColumnName("batch_id");

        b.HasOne<RecipeRolloutStationEntity>()
            .WithOne()
            .HasForeignKey<RecipeRolloutLatestStationEntity>(x => new { x.BatchId, x.StationId })
            .OnDelete(DeleteBehavior.NoAction);
    }
}
