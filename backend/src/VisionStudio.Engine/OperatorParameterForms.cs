using System.Text.RegularExpressions;
using VisionStudio.Engine.Algorithms;

namespace VisionStudio.Engine;

/// <summary>Operation-specific forms share stable serialized parameter keys with existing workflows.</summary>
internal static class OperatorParameterForms
{
    internal static NodeCatalogItem Apply(NodeCatalogItem item)
    {
        if (!OperatorParameterUsage.ByNode.TryGetValue(item.Type, out var usage)) return item;
        var notes = item.Type switch
        {
            "jlvision.imageOp" => JlImageOperations.All.ToDictionary(p => p.Key, p => p.Value.Note + (p.Value.NeedsStructuringElement ? "; size=自动结构元尺寸（未连接 image2 时）" : "")),
            "jlvision.regionOp" => JlRegionOperations.All.ToDictionary(p => p.Key, p => p.Value.Note + (p.Value.NeedsStructuringElement ? "; size=自动结构元尺寸" : "")),
            "jlvision.regionFeature" => JlRegionFeatures.All.ToDictionary(p => p.Key, p => p.Value.Note),
            "jlvision.geometry" => JlGeometryOperations.All.ToDictionary(p => p.Key, p => p.Value.Note),
            "jlvision.matrixOp" => JlMatrixOperations.All.ToDictionary(p => p.Key, p => p.Value.Note),
            "jlvision.homMat" => JlHomMatOperations.All.ToDictionary(p => p.Key, p => p.Value.Note),
            "jlvision.contourOp" => JlContourOperations.All.ToDictionary(p => p.Key, p => p.Value.Note),
            "jlvision.threshold" => JlThresholdOperations.All.ToDictionary(p => p.Key, p => p.Value.Note),
            "jlvision.contourFeature" => JlContourFeatures.All.ToDictionary(p => p.Key, p => p.Value.Note),
            _ => throw new InvalidOperationException(item.Type)
        };
        // Fail visibly if a table is extended without regenerating its consumed-argument metadata.
        if (!notes.Keys.Order().SequenceEqual(usage.Keys.Order()))
            throw new InvalidOperationException($"Regenerate operator forms for {item.Type} with scripts/generate-operator-forms.py.");
        var declared = item.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
        string[] nativeInputs = ["image", "secondary", "third", "image2", "second", "region", "structuringElement", "contour", "contour2"];
        var missing = usage.Values.SelectMany(fields => fields).Except(declared).Except(nativeInputs).Distinct().ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"{item.Type} needs parameter descriptors for {string.Join(", ", missing)}.");
        var controller = item.Parameters.Single(p => p.Name is "operator" or "feature").Name;
        return item with { Parameters = item.Parameters.Select(parameter =>
        {
            var operations = usage.Where(pair => pair.Value.Contains(parameter.Name)).Select(pair => pair.Key).ToArray();
            if (item.Type == "jlvision.contourFeature" && parameter.Name == "selectedIndex") operations = usage.Keys.ToArray();
            if (item.Type == "jlvision.contourFeature" && parameter.Name == "secondIndex")
                operations = JlContourFeatures.All.Where(pair => pair.Value.NeedsSecond).Select(pair => pair.Key).ToArray();
            if (item.Type == "jlvision.matrixOp" && parameter.Name == "matrixA") operations = usage.Keys.Where(key => key != "create").ToArray();
            if (item.Type == "jlvision.matrixOp" && parameter.Name == "matrixB")
                operations = JlMatrixOperations.All.Where(pair => pair.Value.NeedsB).Select(pair => pair.Key).ToArray();
            if (parameter.Name == controller) operations = usage.Keys.ToArray();
            return parameter with
            {
                VisibleWhen = parameter.Name == controller ? null : new ParameterVisibility(controller, operations),
                VariantParameter = controller,
                Variants = operations.ToDictionary(key => key, key =>
                {
                    var match = Regex.Match(notes[key], @"\b" + Regex.Escape(parameter.Name) + @"=([^,;；（()]+)");
                    var role = match.Success ? match.Groups[1].Value.Trim() : null;
                    // Keep complete query/mode strings in the help; use concise semantic names for scalar aliases.
                    var label = role is { Length: > 0 and < 24 } && !role.Contains('|') ? role : null;
                    if (item.Type == "jlvision.imageOp" && parameter.Name == "offset") label ??= key == "pow" ? "指数" : "加法偏移";
                    if (item.Type == "jlvision.imageOp" && parameter.Name == "mult") label = key == "gamma" ? "Gamma" : label ?? "倍率";
                    var diagonalIndex = item.Type == "jlvision.matrixOp" && key == "getDiagonal" && parameter.Name == "rows";
                    var presentation = new ParameterPresentation(label, notes[key], Suggestions(item.Type, key, parameter.Name),
                        Min: diagonalIndex ? -100000 : null, Step: diagonalIndex ? 1 : null);
                    return NumericSchema(item.Type, key, parameter, presentation);
                })
            };
        }).ToArray() };
    }

    internal static ParameterDescriptor Resolve(NodeDefinition node, NodeCatalogItem catalog, ParameterDescriptor descriptor)
    {
        if (descriptor.VariantParameter is not { } controller || descriptor.Variants is not { } variants) return descriptor;
        var fallback = Convert.ToString(catalog.Parameters.FirstOrDefault(p => p.Name == controller)?.DefaultValue) ?? "";
        var operation = node.GetString(controller, fallback).Trim();
        if (!variants.TryGetValue(operation, out var presentation))
            presentation = variants.FirstOrDefault(pair => pair.Key.Equals(operation, StringComparison.OrdinalIgnoreCase)).Value;
        if (presentation is null) return descriptor;
        return descriptor with { Min = presentation.Min ?? descriptor.Min, Max = presentation.Max ?? descriptor.Max,
            Step = presentation.Step ?? descriptor.Step, DefaultValue = presentation.DefaultValue ?? descriptor.DefaultValue,
            Unit = presentation.Unit ?? descriptor.Unit };
    }

    internal static double Number(NodeDefinition node, string parameter, double fallback)
    {
        var catalog = BuiltInNodeCatalog.Items.First(item => item.Type.Equals(node.Type, StringComparison.OrdinalIgnoreCase));
        var original = catalog.Parameters.FirstOrDefault(item => item.Name == parameter);
        if (original is null) return node.GetDouble(parameter, fallback);
        var effective = Resolve(node, catalog, original);
        return node.GetDouble(parameter, Convert.ToDouble(effective.DefaultValue ?? fallback, System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static void Validate(NodeDefinition node)
    {
        var catalog = BuiltInNodeCatalog.Items.First(item => item.Type.Equals(node.Type, StringComparison.OrdinalIgnoreCase));
        var controller = catalog.Parameters.First(item => item.Name is "operator" or "feature");
        var operation = node.GetString(controller.Name, Convert.ToString(controller.DefaultValue) ?? "").Trim();
        foreach (var original in catalog.Parameters.Where(item => item.Type == "number"))
        {
            if (original.VisibleWhen is { } visibility && !visibility.Values.Contains(operation)) continue;
            var descriptor = Resolve(node, catalog, original);
            var value = Number(node, descriptor.Name, 0);
            if (!double.IsFinite(value) || descriptor.Min is { } min && value < min || descriptor.Max is { } max && value > max)
                throw new InvalidOperationException($"{node.Type}/{operation}: {descriptor.Name} must be finite and within {descriptor.Min}..{descriptor.Max}.");
        }
    }

    private static ParameterPresentation NumericSchema(string type, string operation, ParameterDescriptor parameter, ParameterPresentation form)
    {
        if (parameter.Type != "number") return form;
        var name = parameter.Name;
        if (type == "jlvision.imageOp")
        {
            if (name is "size" or "size2")
            {
                if (operation == "zoomImageFactor") return form with { Min = 0.001, Max = 100, Step = 0.05, DefaultValue = 1d, Unit = "×" };
                if (operation == "cropRectangle2") return form with { Min = 0.5, Max = 16384, Step = 0.5, DefaultValue = 20d, Unit = "px" };
                if (operation == "cropDomainRel") return form with { Min = 0, Max = 32768, Step = 1, DefaultValue = 0d, Unit = "px" };
                if (operation == "addBorder") return form with { Min = 0, Max = 4096, Step = 1, DefaultValue = 1d, Unit = "px" };
                if (operation is "cropPart" or "changeFormat" or "genImageGrayRamp" or "genImageSurfaceFirstOrder" or "genImageSurfaceSecondOrder")
                    return form with { Min = 1, Max = 32768, Step = 1, DefaultValue = 256d, Unit = "px" };
                return form with { Min = 1, Max = 511, Step = 2, DefaultValue = 3d, Unit = "px" };
            }
            if (name is "row" or "column") return form with { Min = operation == "cropDomainRel" ? 0 : -10000000, Step = operation == "cropRectangle2" ? 0.5 : 1, Unit = "px" };
            if (name == "mult" && operation is "rotateImage" or "cropRectangle2")
                return form with { Min = -2 * Math.PI, Max = 2 * Math.PI, Step = 0.01, DefaultValue = 0d, Unit = "rad" };
            if (name == "count") return form with { Min = operation is "bitLshift" or "bitRshift" or "bitMask" or "bitSlice" ? 0 : 1,
                Max = operation == "bitSlice" ? 7 : operation is "bitLshift" or "bitRshift" ? 31 : operation == "bitMask" ? int.MaxValue : 100000, Step = 1 };
        }
        // Radius, scale and integer count aliases must not inherit an odd-kernel step.
        if (name == "size" && (form.Label?.Contains("比例") == true || form.Label?.Contains("scale", StringComparison.OrdinalIgnoreCase) == true))
            return form with { Min = 0.001, Max = 100, Step = 0.05, DefaultValue = 1d, Unit = "×" };
        if (name == "size" && form.Label?.Contains("半径") == true) return form with { Min = 0.1, Step = 0.5, Unit = "px" };
        if (name is "count" or "rows" or "columns") return form with { Step = 1 };
        return form;
    }

    private static IReadOnlyList<ParameterOption>? Suggestions(string type, string operation, string parameter)
    {
        string[]? values = (type, operation, parameter) switch
        {
            ("jlvision.imageOp", "convertType", "mode") => ["byte", "int2", "uint2", "real"],
            ("jlvision.imageOp", "gamma", "mode") => ["false", "true"],
            ("jlvision.matrixOp", "mult" or "multMod", "mode") => ["AB", "ATB", "ABT", "ATBT"],
            ("jlvision.matrixOp", "sum" or "max" or "min" or "mean", "mode") => ["columns", "rows", "full"],
            ("jlvision.threshold", "varThreshold" or "dynThreshold" or "localThreshold", "mode2") => ["light", "dark"],
            _ => null
        };
        return values?.Select(value => new ParameterOption(value, value)).ToArray();
    }
}
