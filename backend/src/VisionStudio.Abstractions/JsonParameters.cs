using System.Text.Json;

namespace VisionStudio.Abstractions;

public static class JsonParameters
{
    public static double GetDouble(this NodeDefinition node, string name, double fallback)
    {
        if (node.Parameters is null || !node.Parameters.TryGetValue(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    public static int GetInt(this NodeDefinition node, string name, int fallback)
        => (int)Math.Round(node.GetDouble(name, fallback));

    public static string GetString(this NodeDefinition node, string name, string fallback)
    {
        if (node.Parameters is null || !node.Parameters.TryGetValue(name, out var value)) return fallback;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
    }

    public static bool GetBool(this NodeDefinition node, string name, bool fallback)
    {
        if (node.Parameters is null || !node.Parameters.TryGetValue(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }
}
