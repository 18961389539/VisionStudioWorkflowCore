using System.Text.Json;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Algorithms;

// 2026-11-12 从单文件 JLVisionMatching.cs（2809 行）按域拆出，纯移动、未改方法体。
// 模型序列化（legacy 多模型信封）与训练配置指纹。
// 配置指纹是「参数变了必须重训」这条规则的判定依据；改指纹算法等于让现场已训模型全部失效。
internal static partial class JLVisionMatching
{
    private static ShapeModelSetEnvelope? DeserializeShapeModelSet(string savedModel)
    {
        if (!savedModel.StartsWith(ShapeModelSetPrefix, StringComparison.Ordinal)) return null;
        ShapeModelSetEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ShapeModelSetEnvelope>(savedModel[ShapeModelSetPrefix.Length..], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException ex) { throw new InvalidOperationException("The saved shape model set is invalid.", ex); }
        if (envelope is null || envelope.Version != 1 || envelope.Models is not { Length: >= 2 and <= 4 })
            throw new InvalidOperationException("A shape model set must contain 2–4 compatible trained samples.");
        if (envelope.Models.Any(item => item.Width != envelope.Models[0].Width || item.Height != envelope.Models[0].Height))
            throw new InvalidOperationException("Every sample in a shape model set must use the same trained region size.");
        long totalBytes = 0;
        foreach (var item in envelope.Models)
        {
            if (string.IsNullOrWhiteSpace(item.Data) || item.Width < 8 || item.Height < 8 || item.Width > 100_000 || item.Height > 100_000
                || !double.IsFinite(item.OriginX) || !double.IsFinite(item.OriginY)
                || item.OriginX < 0 || item.OriginY < 0 || item.OriginX >= item.Width || item.OriginY >= item.Height)
                throw new InvalidOperationException("A shape model set contains invalid training geometry.");
            try { totalBytes += Convert.FromBase64String(item.Data).Length; }
            catch (FormatException ex) { throw new InvalidOperationException("A shape model set contains invalid model data.", ex); }
            if (totalBytes > 16 * 1024 * 1024) throw new InvalidOperationException("The saved shape model set exceeds the 16 MB combined limit.");
        }
        return envelope;
    }

    private static string ShapeTrainingConfig(NodeDefinition node)
    {
        var clutterRoi = node.GetRoi("clutterRoi");
        var clutterSettings = clutterRoi is null ? "none"
            : FormattableString.Invariant($"{RoiFingerprint(clutterRoi)}:{Math.Clamp(node.GetInt("clutterContrast", node.GetInt("minContrast", 5)), 1, 255)}");
        var autoTag = MatchingAutoTune.AutoFingerprint(node);
        var autoSettings = autoTag.Length == 0 ? string.Empty : $"|{autoTag}";
        return FormattableString.Invariant($"shape-v6|{ShapeTrainingConfigV5(node)[9..]}|{clutterSettings}{autoSettings}");
    }

    private static string ShapeTrainingConfigV5(NodeDefinition node)
    {
        return FormattableString.Invariant($"shape-v5|{ShapeTrainingConfigV4(node)[9..]}|{Math.Clamp(node.GetDouble("scaleToleranceXPercent", 0), 0, 50):R}|{Math.Clamp(node.GetDouble("scaleToleranceYPercent", 0), 0, 50):R}");
    }

    private static string ShapeTrainingConfigV4(NodeDefinition node)
    {
        return FormattableString.Invariant($"shape-v4|{ShapeTrainingConfigV3(node)[9..]}|{Math.Clamp(node.GetDouble("trainingMinSampleFraction", .75), .5, 1):R}|{Math.Clamp(node.GetDouble("trainingFeatureTolerancePx", 2), .5, 10):R}");
    }

    private static string ShapeTrainingConfigV3(NodeDefinition node)
    {
        var roi = node.GetRoi();
        var roiText = RoiFingerprint(roi);
        return FormattableString.Invariant($"shape-v3|{node.GetString("templatePath", string.Empty)}|{node.GetString("binarize", "auto")}|{node.GetDouble("threshold", 128):R}|{node.GetBool("invert", false)}|{node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)):R}|{node.GetDouble("angleStepDeg", 1):R}|{node.GetInt("pyramidLevels", 0)}|{roiText}|{node.GetString("trainingOriginMode", "centroid")}|{node.GetDouble("trainingOriginX", 0):R}|{node.GetDouble("trainingOriginY", 0):R}|{node.GetDouble("scaleTolerancePercent", 0):R}|{(node.GetDouble("scaleTolerancePercent", 0) > 0 ? node.GetDouble("scaleStepPercent", 1) : 0):R}|{node.GetBool("ignorePolarity", false)}|{node.GetInt("minContrast", 5)}");
    }

    private static string ShapeTrainingConfigV2(NodeDefinition node)
    {
        var roi = node.GetRoi();
        var roiText = RoiFingerprint(roi);
        return FormattableString.Invariant($"shape-v2|{node.GetString("templatePath", string.Empty)}|{node.GetString("binarize", "auto")}|{node.GetDouble("threshold", 128):R}|{node.GetBool("invert", false)}|{node.GetDouble("angleRangeDeg", 180):R}|{node.GetDouble("angleStepDeg", 1):R}|{roiText}|{node.GetString("trainingOriginMode", "centroid")}|{node.GetDouble("trainingOriginX", 0):R}|{node.GetDouble("trainingOriginY", 0):R}|{node.GetDouble("scaleTolerancePercent", 0):R}|{(node.GetDouble("scaleTolerancePercent", 0) > 0 ? node.GetDouble("scaleStepPercent", 1) : 0):R}|{node.GetBool("ignorePolarity", false)}|{node.GetInt("minContrast", 5)}");
    }

    private static string ShapeTrainingConfigV1(NodeDefinition node)
    {
        var roi = node.GetRoi();
        var roiText = roi is null ? "none" : roi.Type == VisionRoiType.Rectangle
            ? FormattableString.Invariant($"{roi.X:R},{roi.Y:R},{roi.Width:R},{roi.Height:R}") : roi.Type.ToString();
        return FormattableString.Invariant($"shape-v1|{node.GetString("templatePath", string.Empty)}|{node.GetString("binarize", "auto")}|{node.GetDouble("threshold", 128):R}|{node.GetBool("invert", false)}|{node.GetDouble("angleRangeDeg", 180):R}|{node.GetDouble("angleStepDeg", 1):R}|{roiText}|{node.GetString("trainingOriginMode", "centroid")}|{node.GetDouble("trainingOriginX", 0):R}|{node.GetDouble("trainingOriginY", 0):R}");
    }

    private static bool IsCompatibleLegacyShapeConfig(NodeDefinition node, string savedConfig)
    {
        if (savedConfig.StartsWith("shape-v5|", StringComparison.Ordinal)
            && node.GetRoi("clutterRoi") is null)
            return string.Equals(savedConfig, ShapeTrainingConfigV5(node), StringComparison.Ordinal);
        if (savedConfig.StartsWith("shape-v4|", StringComparison.Ordinal)
            && node.GetRoi("clutterRoi") is null
            && Math.Abs(node.GetDouble("scaleToleranceXPercent", 0)) < 1e-9
            && Math.Abs(node.GetDouble("scaleToleranceYPercent", 0)) < 1e-9)
            return string.Equals(savedConfig, ShapeTrainingConfigV4(node), StringComparison.Ordinal);
        if (savedConfig.StartsWith("shape-v3|", StringComparison.Ordinal)
            && node.GetRoi("clutterRoi") is null
            && Math.Abs(node.GetDouble("trainingMinSampleFraction", .75) - .75) < 1e-9
            && Math.Abs(node.GetDouble("trainingFeatureTolerancePx", 2) - 2) < 1e-9
            && Math.Abs(node.GetDouble("scaleToleranceXPercent", 0)) < 1e-9
            && Math.Abs(node.GetDouble("scaleToleranceYPercent", 0)) < 1e-9)
            return string.Equals(savedConfig, ShapeTrainingConfigV3(node), StringComparison.Ordinal);
        if (savedConfig.StartsWith("shape-v2|", StringComparison.Ordinal)
            && node.GetRoi("clutterRoi") is null
            && node.GetInt("pyramidLevels", 0) == 0
            && Math.Abs(node.GetDouble("scaleToleranceXPercent", 0)) < 1e-9
            && Math.Abs(node.GetDouble("scaleToleranceYPercent", 0)) < 1e-9
            && Math.Abs(node.GetDouble("trainingAngleRangeDeg", node.GetDouble("angleRangeDeg", 180)) - node.GetDouble("angleRangeDeg", 180)) < 1e-9)
            return string.Equals(savedConfig, ShapeTrainingConfigV2(node), StringComparison.Ordinal);
        var saved = savedConfig.Split('|');
        var current = ShapeTrainingConfigV1(node).Split('|');
        if (saved.Length != current.Length || saved[0] != "shape-v1"
            || node.GetDouble("scaleTolerancePercent", 0) != 0
            || node.GetDouble("scaleToleranceXPercent", 0) != 0
            || node.GetDouble("scaleToleranceYPercent", 0) != 0
            || node.GetRoi("clutterRoi") is not null
            || node.GetInt("pyramidLevels", 0) != 0
            || node.GetBool("ignorePolarity", false)
            || node.GetInt("minContrast", 5) != 5)
            return false;
        return string.Equals(savedConfig, ShapeTrainingConfigV1(node), StringComparison.Ordinal);
    }

    private static string RoiFingerprint(VisionRoi? roi)
    {
        if (roi is null) return "none";
        return roi.Type switch
        {
            VisionRoiType.Rectangle => FormattableString.Invariant($"Rectangle:{roi.X:R},{roi.Y:R},{roi.Width:R},{roi.Height:R}"),
            VisionRoiType.Circle => FormattableString.Invariant($"Circle:{roi.X:R},{roi.Y:R},{roi.Radius:R}"),
            VisionRoiType.Polygon => "Polygon:" + string.Join(';', (roi.Points ?? []).Select(point => FormattableString.Invariant($"{point.X:R},{point.Y:R}"))),
            VisionRoiType.Brush => "Brush:" + string.Join(';', (roi.Strokes ?? []).Select(FormatBrushStroke)),
            _ => roi.Type.ToString()
        };

        static string FormatBrushStroke(VisionRoiBrushStroke stroke) =>
            FormattableString.Invariant($"{stroke.Mode}:{stroke.Width:R}:")
            + string.Join(',', stroke.Points.Select(point => FormattableString.Invariant($"{point.X:R},{point.Y:R}")));
    }
}
