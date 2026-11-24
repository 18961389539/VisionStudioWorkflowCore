namespace VisionStudio.Api;

public sealed record RecipeRolloutRequest(string JobId, int SourceVersion, string WorkflowHash, IReadOnlyList<string> StationIds);

public sealed record StationRecipeDelivery(
    string StationId, string JobId, string JobName, int SourceVersion, string WorkflowHash,
    bool RequiresValidation, WorkflowDefinition Workflow);

public sealed record RecipeDeliveryReceipt(
    string Id, string StationId, string JobId, string JobName, int SourceVersion, string WorkflowHash,
    int LocalVersion, bool RequiresValidation, int? PublishedVersion, DateTimeOffset JobUpdatedAt, DateTimeOffset ReceivedAt);

public sealed record PublishRecipeDeliveryRequest(DateTimeOffset ExpectedUpdatedAt, bool AcknowledgeMaturityNodes = false);

public enum RecipeDeliveryOutcome { Unconfirmed, Received, Rejected }

public sealed record RecipeRolloutStation(
    string StationId, string StationName, RecipeDeliveryOutcome Outcome, int? LocalVersion = null, string? Detail = null);

public sealed record RecipeRolloutBatch(
    string Id, string JobId, string JobName, int SourceVersion, string WorkflowHash, DateTimeOffset CreatedAt,
    IReadOnlyList<RecipeRolloutStation> Stations);

public sealed record StationRecipeRollout(
    string BatchId, string JobId, string JobName, int SourceVersion, string WorkflowHash, DateTimeOffset CreatedAt,
    RecipeRolloutStation Delivery);
