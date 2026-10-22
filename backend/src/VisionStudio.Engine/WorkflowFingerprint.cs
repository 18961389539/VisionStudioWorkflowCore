using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VisionStudio.Engine;

/// <summary>
/// Produces a stable execution fingerprint for a workflow.
/// Designer-only fields (workflow/node display names, node positions and edge ids) are intentionally excluded,
/// so moving a node on canvas does not create a different executable definition or Job hash.
/// </summary>
public static class WorkflowFingerprint
{
    public static string Compute(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var canonical = Canonicalize(workflow);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string ComputeShort(WorkflowDefinition workflow, int chars = 16)
    {
        var hash = Compute(workflow);
        return hash[..Math.Clamp(chars, 8, hash.Length)];
    }

    public static string Canonicalize(WorkflowDefinition workflow)
    {
        var sb = new StringBuilder(4096);
        sb.Append("{\"nodes\":[");
        var nodes = workflow.Nodes.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        for (var i = 0; i < nodes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var node = nodes[i];
            sb.Append('{');
            AppendProperty(sb, "id", node.Id); sb.Append(',');
            AppendProperty(sb, "type", node.Type); sb.Append(',');
            sb.Append("\"parameters\":");
            AppendParameters(sb, node.Parameters);
            sb.Append('}');
        }

        sb.Append("],\"edges\":[");
        var edges = workflow.Edges
            .OrderBy(x => x.SourceNodeId, StringComparer.Ordinal)
            .ThenBy(x => x.SourcePort, StringComparer.Ordinal)
            .ThenBy(x => x.TargetNodeId, StringComparer.Ordinal)
            .ThenBy(x => x.TargetPort, StringComparer.Ordinal)
            .ThenBy(x => x.Kind ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
        for (var i = 0; i < edges.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var edge = edges[i];
            sb.Append('{');
            AppendProperty(sb, "sourceNodeId", edge.SourceNodeId); sb.Append(',');
            AppendProperty(sb, "sourcePort", edge.SourcePort); sb.Append(',');
            AppendProperty(sb, "targetNodeId", edge.TargetNodeId); sb.Append(',');
            AppendProperty(sb, "targetPort", edge.TargetPort); sb.Append(',');
            AppendProperty(sb, "kind", edge.Kind ?? string.Empty);
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static void AppendParameters(StringBuilder sb, Dictionary<string, JsonElement>? parameters)
    {
        sb.Append('{');
        if (parameters is not null)
        {
            var first = true;
            foreach (var pair in parameters.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(JsonSerializer.Serialize(pair.Key));
                sb.Append(':');
                AppendJsonElement(sb, pair.Value);
            }
        }
        sb.Append('}');
    }

    private static void AppendJsonElement(StringBuilder sb, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var properties = element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
                for (var i = 0; i < properties.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonSerializer.Serialize(properties[i].Name));
                    sb.Append(':');
                    AppendJsonElement(sb, properties[i].Value);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var j = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (j++ > 0) sb.Append(',');
                    AppendJsonElement(sb, item);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(element.GetString()));
                break;
            case JsonValueKind.Number:
                sb.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                sb.Append("null");
                break;
            default:
                sb.Append(element.GetRawText());
                break;
        }
    }

    private static void AppendProperty(StringBuilder sb, string name, string value)
    {
        sb.Append(JsonSerializer.Serialize(name));
        sb.Append(':');
        sb.Append(JsonSerializer.Serialize(value));
    }
}
