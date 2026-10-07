namespace VisionStudio.Engine.Runtime;

public static class WorkflowGraph
{
    private static readonly HashSet<string> ControlSourcePorts =
        new(["next", "true", "false", "branch1", "branch2"], StringComparer.OrdinalIgnoreCase);

    public static bool IsControlEdge(EdgeDefinition edge)
        => edge.IsControl || ControlSourcePorts.Contains(edge.SourcePort);
}
