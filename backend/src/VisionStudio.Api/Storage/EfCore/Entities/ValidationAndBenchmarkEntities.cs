namespace VisionStudio.Api.Storage.EfCore.Entities;

public sealed class ValidationDatasetEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? TopologyHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ValidationDatasetItemEntity
{
    public string DatasetId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public string ExpectedDisposition { get; set; } = string.Empty;
    public string? Note { get; set; }
    public int SortOrder { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class ValidationRunEntity
{
    public string RunId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string SourceWorkflowRunId { get; set; } = string.Empty;
    public string WorkflowHash { get; set; } = string.Empty;
    public string CandidateWorkflowJson { get; set; } = string.Empty;
    public int RequestedCount { get; set; }
    public int CompletedCount { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool CancelRequested { get; set; }
    public string? Error { get; set; }
    public string? SummaryJson { get; set; }
}

public sealed class ValidationResultEntity
{
    public string RunId { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public string ExpectedDisposition { get; set; } = string.Empty;
    public string ActualDisposition { get; set; } = string.Empty;
    public string Classification { get; set; } = string.Empty;
    public string? ReplayRunId { get; set; }
    public bool Success { get; set; }
    public double DurationMs { get; set; }
    public string FailedNodesJson { get; set; } = "[]";
    public string? Error { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

public sealed class PluginBenchmarkRunEntity
{
    public string RunId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string ValidationRunId { get; set; } = string.Empty;
    public string PluginId { get; set; } = string.Empty;
    public string PluginVersion { get; set; } = string.Empty;
    public string? PluginAssemblySha256 { get; set; }
    public string WorkflowHash { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PoolSizesJson { get; set; } = string.Empty;
    public int RequestedCount { get; set; }
    public int WarmupCount { get; set; }
    public int WorkloadConcurrency { get; set; }
    public string? BaselineRunId { get; set; }
    public string PolicyJson { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool CancelRequested { get; set; }
    public string? Error { get; set; }
    public string? RecommendationJson { get; set; }
    public string? RegressionJson { get; set; }
}

public sealed class PluginBenchmarkPoolResultEntity
{
    public string RunId { get; set; } = string.Empty;
    public int PoolSize { get; set; }
    public int ItemCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public double FailureRate { get; set; }
    public double TotalDurationMs { get; set; }
    public double ThroughputPerSecond { get; set; }
    public long ObservedWorkingSetBytes { get; set; }
    public string WorkflowDurationJson { get; set; } = string.Empty;
    public string WorkerPerformanceJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
