namespace VisionStudio.Api.Storage.EfCore.Entities;

public sealed class JobEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int LatestVersion { get; set; }
    public int? PublishedVersion { get; set; }
    public string? PublishedDependencyManifestHash { get; set; }
    public string? ProductId { get; set; }
    public string? RecipeCode { get; set; }
    public bool RequiresValidation { get; set; }
}

public sealed class JobVersionEntity
{
    public string JobId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string WorkflowHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Note { get; set; } = string.Empty;
    public string WorkflowJson { get; set; } = string.Empty;
    public string? BaseWorkflowHash { get; set; }
    public string? BaseWorkflowJson { get; set; }
    public string ParameterBindingsJson { get; set; } = "{}";
    public string ParameterSnapshotJson { get; set; } = "{}";
}

public sealed class JobPublicationHistoryEntity
{
    public long Seq { get; set; }
    public string JobId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Action { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; }
    public string? DependencyManifestHash { get; set; }
}

public sealed class ProductEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class JobVersionValidationEntity
{
    public string JobId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string ValidationRunId { get; set; } = string.Empty;
    public string DatasetId { get; set; } = string.Empty;
    public string WorkflowHash { get; set; } = string.Empty;
    public bool Accepted { get; set; }
    public string PolicyJson { get; set; } = string.Empty;
    public string SummaryJson { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class ProductParameterValueEntity
{
    public string ProductId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RecipeParameterValueEntity
{
    public string JobId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkflowModuleEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int LatestVersion { get; set; }
}

public sealed class WorkflowModuleVersionEntity
{
    public string ModuleId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string ModuleHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public string Note { get; set; } = string.Empty;
    public string WorkflowJson { get; set; } = string.Empty;
    public string InputsJson { get; set; } = string.Empty;
    public string OutputsJson { get; set; } = string.Empty;
    public string ParametersJson { get; set; } = string.Empty;
}

public sealed class RuntimeDependencyManifestEntity
{
    public string ManifestHash { get; set; } = string.Empty;
    public int SchemaVersion { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string ManifestJson { get; set; } = string.Empty;
}

public sealed class RecipeDeliveryReceiptEntity
{
    public string Id { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public int SourceVersion { get; set; }
    public string WorkflowHash { get; set; } = string.Empty;
    public int LocalVersion { get; set; }
    public bool RequiresValidation { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class RecipeRolloutBatchEntity
{
    public string Id { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public int SourceVersion { get; set; }
    public string WorkflowHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RecipeRolloutStationEntity
{
    public string BatchId { get; set; } = string.Empty;
    public string StationId { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public int? LocalVersion { get; set; }
    public string? Detail { get; set; }
}

public sealed class RecipeRolloutLatestStationEntity
{
    public string StationId { get; set; } = string.Empty;
    public string BatchId { get; set; } = string.Empty;
}
