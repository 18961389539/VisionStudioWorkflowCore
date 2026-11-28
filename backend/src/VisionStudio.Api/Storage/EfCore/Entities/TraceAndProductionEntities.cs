namespace VisionStudio.Api.Storage.EfCore.Entities;

public sealed class RunTraceEntity
{
    public string RunId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? JobId { get; set; }
    public int? JobVersion { get; set; }
    public string WorkflowId { get; set; } = string.Empty;
    public string WorkflowName { get; set; } = string.Empty;
    public string? WorkflowHash { get; set; }
    public string? DependencyManifestHash { get; set; }
    public string ExecutionStatus { get; set; } = string.Empty;
    public string Disposition { get; set; } = string.Empty;
    public double TotalDurationMs { get; set; }
    public int NodeCount { get; set; }
    public int OverlayCount { get; set; }
    public bool HasPreview { get; set; }
    public string? PreviewRelativePath { get; set; }
    public long PreviewBytes { get; set; }
    public bool HasReplayInput { get; set; }
    public string? ReplayRelativePath { get; set; }
    public long ReplayBytes { get; set; }
    public string? ReplaySourceNodeId { get; set; }
    public string? Error { get; set; }
    public string? Note { get; set; }
    public string NodeReportsJson { get; set; } = "[]";
    public string? WorkflowJson { get; set; }
    public string OverlaysJson { get; set; } = "[]";
    public string? ErrorCode { get; set; }
}

public sealed class RunNodeObservationEntity
{
    public string RunId { get; set; } = string.Empty;
    public long ExecutionSequence { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public string NodeType { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public bool Success { get; set; }
    public double StartOffsetMs { get; set; }
    public double EndOffsetMs { get; set; }
    public double DurationMs { get; set; }
    public string? Error { get; set; }
}

public sealed class InvestigationCaseEntity
{
    public string Id { get; set; } = string.Empty;
    public string CaseKey { get; set; } = string.Empty;
    public string SignatureId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string WorkflowId { get; set; } = string.Empty;
    public string? WorkflowHash { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? JobId { get; set; }
    public int? JobVersion { get; set; }
    public string? PrimaryNodeId { get; set; }
    public string? PrimaryNodeType { get; set; }
    public string Fingerprint { get; set; } = string.Empty;
    public string RepresentativeRunId { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int OccurrenceCount { get; set; }
    public int ReopenCount { get; set; }
    public string? Owner { get; set; }
    public string? RootCause { get; set; }
    public string? ResolutionNote { get; set; }
    public string? VerificationNote { get; set; }
    public string? VerificationRunId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class InvestigationCaseRunEntity
{
    public string CaseId { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public string Disposition { get; set; } = string.Empty;
    public double TotalDurationMs { get; set; }
    public double? NodeDurationMs { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class InvestigationCaseVerificationEntity
{
    public string Id { get; set; } = string.Empty;
    public string CaseId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string BaselineValidationRunId { get; set; } = string.Empty;
    public string CandidateValidationRunId { get; set; } = string.Empty;
    public string BaselineWorkflowHash { get; set; } = string.Empty;
    public string CandidateWorkflowHash { get; set; } = string.Empty;
    public string GateStatus { get; set; } = string.Empty;
    public int BaselineSignatureHits { get; set; }
    public int CandidateSignatureHits { get; set; }
    public int BaselineInspectableResults { get; set; }
    public int CandidateInspectableResults { get; set; }
    public string BaselineSummaryJson { get; set; } = string.Empty;
    public string CandidateSummaryJson { get; set; } = string.Empty;
    public string ReasonsJson { get; set; } = string.Empty;
    public string WarningsJson { get; set; } = string.Empty;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ResultRecordEntity
{
    public string Id { get; set; } = string.Empty;
    public string Dataset { get; set; } = string.Empty;
    public string? RecordKey { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string? WorkflowScope { get; set; }
    public string NodeId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? PayloadSha256 { get; set; }
    public string RecordKeyNormalizationVersion { get; set; } = "v1-trim";
}

public sealed class DurableCounterEventEntity
{
    public long Seq { get; set; }
    public string WorkflowId { get; set; } = string.Empty;
    public string CounterId { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string OperationFingerprint { get; set; } = string.Empty;
    public long CountValue { get; set; }
    public long Total { get; set; }
    public long PassCount { get; set; }
    public long FailCount { get; set; }
}
