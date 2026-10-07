using System.Text.Json;

namespace VisionStudio.Abstractions;

public sealed record WorkflowDefinition(
    string Id,
    string Name,
    IReadOnlyList<NodeDefinition> Nodes,
    IReadOnlyList<EdgeDefinition> Edges);

public sealed record NodeDefinition(
    string Id,
    string Type,
    string? Name,
    NodePosition? Position,
    Dictionary<string, JsonElement>? Parameters);

public sealed record NodePosition(double X, double Y);

public sealed record EdgeDefinition(
    string Id,
    string SourceNodeId,
    string SourcePort,
    string TargetNodeId,
    string TargetPort,
    string? Kind = null)
{
    public bool IsControl => string.Equals(Kind, "control", StringComparison.OrdinalIgnoreCase);
}

public sealed record PortDescriptor(string Name, VisionDataType DataType, bool Required = true);

public sealed record ParameterOption(string Label, string Value);

public sealed record ParameterDescriptor(
    string Name,
    string Label,
    string Type,
    object? DefaultValue = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    IReadOnlyList<ParameterOption>? Options = null,
    string? Unit = null,
    string? Group = null,
    string? Description = null);

public enum VisionExecutionMode
{
    Auto,
    WorkflowCore,
    Pipeline
}

public sealed record VisionToolCapabilities(
    bool SupportsRoi = false,
    bool EmitsOverlay = false,
    bool Deterministic = true,
    bool SupportsRunNode = true,
    bool SupportsParallel = true,
    VisionExecutionMode ExecutionMode = VisionExecutionMode.Auto);

public sealed record NodeCatalogItem(
    string Type,
    string DisplayName,
    string Category,
    IReadOnlyList<PortDescriptor> Inputs,
    IReadOnlyList<PortDescriptor> Outputs,
    IReadOnlyList<ParameterDescriptor> Parameters,
    string? Description = null,
    string? PluginId = null,
    VisionToolCapabilities? Capabilities = null);
