using System.Globalization;
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
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback
        };
    }

    public static int GetInt(this NodeDefinition node, string name, int fallback)
    {
        if (node.Parameters is null || !node.Parameters.TryGetValue(name, out var value)) return fallback;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt32(out var exact)) return exact;
                if (value.TryGetDouble(out var number)
                    && double.IsFinite(number)
                    && number == Math.Truncate(number)
                    && number >= int.MinValue
                    && number <= int.MaxValue)
                    return (int)number;
                throw new InvalidOperationException($"Parameter '{name}' expects a whole 32-bit integer, got '{value.GetRawText()}'.");

            case JsonValueKind.String:
                var text = value.GetString();
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
                    return parsedInt;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
                {
                    if (double.IsFinite(parsedDouble)
                        && parsedDouble == Math.Truncate(parsedDouble)
                        && parsedDouble >= int.MinValue
                        && parsedDouble <= int.MaxValue)
                        return (int)parsedDouble;
                    throw new InvalidOperationException($"Parameter '{name}' expects a whole 32-bit integer, got '{text}'.");
                }
                return fallback;

            default:
                return fallback;
        }
    }

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
