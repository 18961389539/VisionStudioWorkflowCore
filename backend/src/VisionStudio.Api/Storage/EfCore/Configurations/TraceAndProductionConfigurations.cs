using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using VisionStudio.Api.Storage.EfCore.Entities;

namespace VisionStudio.Api.Storage.EfCore.Configurations;

internal sealed class RunTraceConfiguration : IEntityTypeConfiguration<RunTraceEntity>
{
    public void Configure(EntityTypeBuilder<RunTraceEntity> b)
    {
        b.ToTable("run_traces");
        b.HasKey(x => x.RunId);
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.StartedAt).HasColumnName("started_at");
        b.Property(x => x.Source).HasColumnName("source");
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.JobVersion).HasColumnName("job_version");
        b.Property(x => x.WorkflowId).HasColumnName("workflow_id");
        b.Property(x => x.WorkflowName).HasColumnName("workflow_name");
        b.Property(x => x.WorkflowHash).HasColumnName("workflow_hash");
        b.Property(x => x.DependencyManifestHash).HasColumnName("dependency_manifest_hash");
        b.Property(x => x.ExecutionStatus).HasColumnName("execution_status");
        b.Property(x => x.Disposition).HasColumnName("disposition");
        b.Property(x => x.TotalDurationMs).HasColumnName("total_duration_ms");
        b.Property(x => x.NodeCount).HasColumnName("node_count");
        b.Property(x => x.OverlayCount).HasColumnName("overlay_count");
        b.Property(x => x.HasPreview).HasColumnName("has_preview").HasConversion<int>();
        b.Property(x => x.PreviewRelativePath).HasColumnName("preview_relative_path");
        b.Property(x => x.PreviewBytes).HasColumnName("preview_bytes").HasDefaultValue(0L);
        b.Property(x => x.HasReplayInput).HasColumnName("has_replay_input").HasConversion<int>().HasDefaultValue(false);
        b.Property(x => x.ReplayRelativePath).HasColumnName("replay_relative_path");
        b.Property(x => x.ReplayBytes).HasColumnName("replay_bytes").HasDefaultValue(0L);
        b.Property(x => x.ReplaySourceNodeId).HasColumnName("replay_source_node_id");
        b.Property(x => x.Error).HasColumnName("error");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.NodeReportsJson).HasColumnName("node_reports_json");
        b.Property(x => x.WorkflowJson).HasColumnName("workflow_json");
        b.Property(x => x.OverlaysJson).HasColumnName("overlays_json");
        b.Property(x => x.ErrorCode).HasColumnName("error_code");

        b.HasIndex(x => x.StartedAt).HasDatabaseName("ix_run_traces_started").IsDescending();
        b.HasIndex(x => new { x.JobId, x.StartedAt }).HasDatabaseName("ix_run_traces_job_started").IsDescending(false, true);
        b.HasIndex(x => new { x.Disposition, x.StartedAt }).HasDatabaseName("ix_run_traces_disposition_started").IsDescending(false, true);
        b.HasIndex(x => new { x.HasPreview, x.StartedAt }).HasDatabaseName("ix_run_traces_preview_started");
        b.HasIndex(x => new { x.DependencyManifestHash, x.StartedAt }).HasDatabaseName("ix_run_traces_dependency_manifest").IsDescending(false, true);
        b.HasIndex(x => new { x.JobId, x.JobVersion, x.StartedAt, x.RunId })
            .HasDatabaseName("ix_run_traces_operator_ng_preview")
            .IsDescending(false, false, true, true)
            .HasFilter("source='ProductionRuntime' AND has_preview=1 AND disposition='NG' COLLATE NOCASE");
    }
}

internal sealed class RunNodeObservationConfiguration : IEntityTypeConfiguration<RunNodeObservationEntity>
{
    public void Configure(EntityTypeBuilder<RunNodeObservationEntity> b)
    {
        b.ToTable("run_node_observations");
        b.HasKey(x => new { x.RunId, x.ExecutionSequence });
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.ExecutionSequence).HasColumnName("execution_sequence");
        b.Property(x => x.NodeId).HasColumnName("node_id");
        b.Property(x => x.NodeType).HasColumnName("node_type");
        b.Property(x => x.Phase).HasColumnName("phase");
        b.Property(x => x.Success).HasColumnName("success").HasConversion<int>();
        b.Property(x => x.StartOffsetMs).HasColumnName("start_offset_ms");
        b.Property(x => x.EndOffsetMs).HasColumnName("end_offset_ms");
        b.Property(x => x.DurationMs).HasColumnName("duration_ms");
        b.Property(x => x.Error).HasColumnName("error");

        b.HasOne<RunTraceEntity>()
            .WithMany()
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.NodeId, x.NodeType }).HasDatabaseName("ix_run_node_observations_node");
        b.HasIndex(x => new { x.RunId, x.ExecutionSequence }).HasDatabaseName("ix_run_node_observations_run");
    }
}

internal sealed class InvestigationCaseConfiguration : IEntityTypeConfiguration<InvestigationCaseEntity>
{
    public void Configure(EntityTypeBuilder<InvestigationCaseEntity> b)
    {
        b.ToTable("investigation_cases");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CaseKey).HasColumnName("case_key");
        b.Property(x => x.SignatureId).HasColumnName("signature_id");
        b.Property(x => x.Category).HasColumnName("category");
        b.Property(x => x.Title).HasColumnName("title");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.Severity).HasColumnName("severity");
        b.Property(x => x.WorkflowId).HasColumnName("workflow_id");
        b.Property(x => x.WorkflowHash).HasColumnName("workflow_hash");
        b.Property(x => x.Source).HasColumnName("source");
        b.Property(x => x.JobId).HasColumnName("job_id");
        b.Property(x => x.JobVersion).HasColumnName("job_version");
        b.Property(x => x.PrimaryNodeId).HasColumnName("primary_node_id");
        b.Property(x => x.PrimaryNodeType).HasColumnName("primary_node_type");
        b.Property(x => x.Fingerprint).HasColumnName("fingerprint");
        b.Property(x => x.RepresentativeRunId).HasColumnName("representative_run_id");
        b.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
        b.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        b.Property(x => x.OccurrenceCount).HasColumnName("occurrence_count");
        b.Property(x => x.ReopenCount).HasColumnName("reopen_count").HasDefaultValue(0);
        b.Property(x => x.Owner).HasColumnName("owner");
        b.Property(x => x.RootCause).HasColumnName("root_cause");
        b.Property(x => x.ResolutionNote).HasColumnName("resolution_note");
        b.Property(x => x.VerificationNote).HasColumnName("verification_note");
        b.Property(x => x.VerificationRunId).HasColumnName("verification_run_id");
        b.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        b.Property(x => x.VerifiedAt).HasColumnName("verified_at");
        b.Property(x => x.ClosedAt).HasColumnName("closed_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        b.HasIndex(x => x.CaseKey).IsUnique();
        b.HasIndex(x => new { x.Status, x.LastSeenAt }).HasDatabaseName("ix_investigation_cases_status").IsDescending(false, true);
        b.HasIndex(x => new { x.SignatureId, x.LastSeenAt }).HasDatabaseName("ix_investigation_cases_signature").IsDescending(false, true);
        b.HasIndex(x => new { x.WorkflowId, x.JobId, x.JobVersion, x.LastSeenAt }).HasDatabaseName("ix_investigation_cases_workflow").IsDescending(false, false, false, true);
    }
}

internal sealed class InvestigationCaseRunConfiguration : IEntityTypeConfiguration<InvestigationCaseRunEntity>
{
    public void Configure(EntityTypeBuilder<InvestigationCaseRunEntity> b)
    {
        b.ToTable("investigation_case_runs");
        b.HasKey(x => new { x.CaseId, x.RunId });
        b.Property(x => x.CaseId).HasColumnName("case_id");
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.StartedAt).HasColumnName("started_at");
        b.Property(x => x.Disposition).HasColumnName("disposition");
        b.Property(x => x.TotalDurationMs).HasColumnName("total_duration_ms");
        b.Property(x => x.NodeDurationMs).HasColumnName("node_duration_ms");
        b.Property(x => x.LinkedAt).HasColumnName("linked_at");

        b.HasOne<InvestigationCaseEntity>()
            .WithMany()
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<RunTraceEntity>()
            .WithMany()
            .HasForeignKey(x => x.RunId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.StartedAt, x.CaseId }).HasDatabaseName("ix_investigation_case_runs_started").IsDescending(true, false);
        b.HasIndex(x => x.RunId).HasDatabaseName("ix_investigation_case_runs_run");
    }
}

internal sealed class InvestigationCaseVerificationConfiguration : IEntityTypeConfiguration<InvestigationCaseVerificationEntity>
{
    public void Configure(EntityTypeBuilder<InvestigationCaseVerificationEntity> b)
    {
        b.ToTable("investigation_case_verifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CaseId).HasColumnName("case_id");
        b.Property(x => x.DatasetId).HasColumnName("dataset_id");
        b.Property(x => x.BaselineValidationRunId).HasColumnName("baseline_validation_run_id");
        b.Property(x => x.CandidateValidationRunId).HasColumnName("candidate_validation_run_id");
        b.Property(x => x.BaselineWorkflowHash).HasColumnName("baseline_workflow_hash");
        b.Property(x => x.CandidateWorkflowHash).HasColumnName("candidate_workflow_hash");
        b.Property(x => x.GateStatus).HasColumnName("gate_status");
        b.Property(x => x.BaselineSignatureHits).HasColumnName("baseline_signature_hits");
        b.Property(x => x.CandidateSignatureHits).HasColumnName("candidate_signature_hits");
        b.Property(x => x.BaselineInspectableResults).HasColumnName("baseline_inspectable_results");
        b.Property(x => x.CandidateInspectableResults).HasColumnName("candidate_inspectable_results");
        b.Property(x => x.BaselineSummaryJson).HasColumnName("baseline_summary_json");
        b.Property(x => x.CandidateSummaryJson).HasColumnName("candidate_summary_json");
        b.Property(x => x.ReasonsJson).HasColumnName("reasons_json");
        b.Property(x => x.WarningsJson).HasColumnName("warnings_json");
        b.Property(x => x.Note).HasColumnName("note");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");

        b.HasOne<InvestigationCaseEntity>()
            .WithMany()
            .HasForeignKey(x => x.CaseId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasOne<ValidationDatasetEntity>()
            .WithMany()
            .HasForeignKey(x => x.DatasetId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<ValidationRunEntity>()
            .WithMany()
            .HasForeignKey(x => x.BaselineValidationRunId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasOne<ValidationRunEntity>()
            .WithMany()
            .HasForeignKey(x => x.CandidateValidationRunId)
            .OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.CaseId, x.CreatedAt }).HasDatabaseName("ix_investigation_case_verifications_case").IsDescending(false, true);
        b.HasIndex(x => new { x.GateStatus, x.CreatedAt }).HasDatabaseName("ix_investigation_case_verifications_gate").IsDescending(false, true);
        b.HasIndex(x => new { x.BaselineValidationRunId, x.CandidateValidationRunId }).HasDatabaseName("ix_investigation_case_verifications_runs");
    }
}

internal sealed class ResultRecordConfiguration : IEntityTypeConfiguration<ResultRecordEntity>
{
    public void Configure(EntityTypeBuilder<ResultRecordEntity> b)
    {
        b.ToTable("result_records");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Dataset).HasColumnName("dataset");
        b.Property(x => x.RecordKey).HasColumnName("record_key");
        b.Property(x => x.PayloadJson).HasColumnName("payload_json");
        b.Property(x => x.RunId).HasColumnName("run_id");
        b.Property(x => x.WorkflowScope).HasColumnName("workflow_scope");
        b.Property(x => x.NodeId).HasColumnName("node_id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");

        b.HasIndex(x => new { x.Dataset, x.CreatedAt }).HasDatabaseName("ix_result_records_dataset_created").IsDescending(false, true);
        b.HasIndex(x => x.RecordKey).HasDatabaseName("ix_result_records_key");
        b.HasIndex(x => new { x.Dataset, x.IdempotencyKey })
            .HasDatabaseName("ux_result_records_dataset_idempotency_key")
            .IsUnique();
    }
}

internal sealed class DurableCounterEventConfiguration : IEntityTypeConfiguration<DurableCounterEventEntity>
{
    public void Configure(EntityTypeBuilder<DurableCounterEventEntity> b)
    {
        b.ToTable("durable_counter_events");
        b.HasKey(x => x.Seq);
        b.Property(x => x.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
        b.Property(x => x.WorkflowId).HasColumnName("workflow_id");
        b.Property(x => x.CounterId).HasColumnName("counter_id");
        b.Property(x => x.EventId).HasColumnName("event_id");
        b.Property(x => x.CountValue).HasColumnName("count_value");
        b.Property(x => x.Total).HasColumnName("total");
        b.Property(x => x.PassCount).HasColumnName("pass_count");
        b.Property(x => x.FailCount).HasColumnName("fail_count");

        b.HasIndex(x => new { x.WorkflowId, x.CounterId, x.EventId }).IsUnique();
        b.HasIndex(x => new { x.WorkflowId, x.CounterId, x.Seq }).HasDatabaseName("ix_durable_counter_events_latest").IsDescending(false, false, true);
    }
}
