using System.Globalization;

namespace VisionStudio.Engine.Nodes;

internal static class MeasurementDefinitionValidation
{
    public static double Number(NodeDefinition node, NodeCatalogItem catalog, string name)
    {
        var descriptor = catalog.Parameters.First(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return node.GetDouble(name, Convert.ToDouble(descriptor.DefaultValue, CultureInfo.InvariantCulture));
    }

    public static int Rounded(NodeDefinition node, NodeCatalogItem catalog, string name, int minimum, int maximum)
        => (int)Math.Clamp(Math.Floor(Number(node, catalog, name) + 0.5), minimum, maximum);

    public static void SearchLine(NodeDefinition node, NodeCatalogItem catalog, IReadOnlySet<string> connectedInputs)
    {
        if (connectedInputs.Contains("searchLine")
            && catalog.Inputs.Any(port => port.Name.Equals("searchLine", StringComparison.OrdinalIgnoreCase)
                && port.DataType == VisionDataType.Line2D))
            return;

        var dx = Number(node, catalog, "endX") - Number(node, catalog, "startX");
        var dy = Number(node, catalog, "endY") - Number(node, catalog, "startY");
        if (Math.Sqrt(dx * dx + dy * dy) < 2)
            throw new InvalidOperationException(
                $"Node '{node.Id}' search line must be at least 2 px long. Adjust its start/end coordinates or connect a valid searchLine input.");
    }

    public static void WidthLimits(NodeDefinition node, NodeCatalogItem catalog)
    {
        var minimum = Number(node, catalog, "minWidth");
        var maximum = Number(node, catalog, "maxWidth");
        if (maximum > 0 && maximum < minimum)
            throw new InvalidOperationException($"Node '{node.Id}' maxWidth must be zero (unlimited) or at least minWidth.");
        if (node.GetString("direction", "strongest").Equals("closestwidth", StringComparison.OrdinalIgnoreCase)
            && Number(node, catalog, "nominalWidth") <= 0)
            throw new InvalidOperationException($"Node '{node.Id}' nominalWidth must be positive for closest-width selection.");
    }

    public static void CaliperSamples(NodeDefinition node, NodeCatalogItem catalog, int lowerBound)
    {
        var count = Rounded(node, catalog, "caliperCount", lowerBound, 101);
        var minimum = Rounded(node, catalog, "minValidCalipers", lowerBound, 101);
        if (minimum > count)
            throw new InvalidOperationException(
                $"Node '{node.Id}' requires {minimum} valid calipers, but caliperCount is {count}. " +
                "Lower minValidCalipers or increase caliperCount.");
    }
}
