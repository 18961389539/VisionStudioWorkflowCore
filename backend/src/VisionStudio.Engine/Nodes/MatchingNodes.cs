using System.Globalization;
using System.Text.Json;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Native JLVision NCC (find_ncc_model direction) with pyramid levels. Default multi-match
/// hard path prefers FindNccModel MaxOverlap; Soft-NMS and soft ignoreRoi are explicit
/// advanced extras (engine-managed, not JL soft-NCC). Discrete scale uses JLVisionOperators.Resize.
/// scoreMap remains diagnostic OpenCV planes (not the JLVision pyramid cube). Remaining gaps
/// vs PatMax: no anisotropic NCC model, no continuous scale, no soft-weighted NCC integral.
/// </summary>
public sealed class TemplateMatchNode : IVisionNodeExecutor
{
    public string Type => "match.template";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var level = NodeCompletionGuards.DiagnosticsLevel(node);
        const int attempts = 1;
        _ = context.RequireImage("image");
        if (context.RequireImage("image").Mat.Empty())
            throw new InvalidOperationException("match.template requires a non-empty scene image.");

        ValidateTemplatePolicies(node);

        var result = JLVisionMatching.Execute(context, node, false, cancellationToken);
        var outputs = new Dictionary<string, VisionValue>(result.Outputs);
        var summary = new Dictionary<string, object?>(result.Summary);

        var found = outputs.TryGetValue("found", out var foundValue) && foundValue.Require<bool>("found");
        var score = outputs.TryGetValue("score", out var scoreValue) ? scoreValue.Require<double>("score") : 0d;
        var count = outputs.TryGetValue("count", out var countValue) ? countValue.Require<long>("count") : 0L;
        if (!double.IsFinite(score)) throw new InvalidOperationException("match.template score must be finite.");
        if (score is < -1 or > 1) throw new InvalidOperationException("match.template score must be within [-1,1] (NCC).");
        if (count < 0) throw new InvalidOperationException("match.template count must be non-negative.");

        // Surface JLVisionMatching summary metrics as first-class outs (HALCON-style diagnostics).
        EmitSummaryDouble(outputs, summary, "pyramidLevels", "pyramidLevels");
        EmitSummaryDouble(outputs, summary, "nmsSuppressedCount", "nmsSuppressedCount");
        EmitSummaryDouble(outputs, summary, "nmsSoftDecayedCount", "nmsSoftDecayedCount");
        EmitSummaryString(outputs, summary, "nmsMode", "nmsMode");
        EmitSummaryString(outputs, summary, "ignoreMode", "ignoreMode");
        EmitSummaryString(outputs, summary, "scaleSearch", "scaleSearch");
        EmitSummaryBool(outputs, summary, "scoreMapEmitted", "scoreMapEmitted");
        EmitSummaryBool(outputs, summary, "modelCacheHit", "modelCacheHit");
        EmitSummaryString(outputs, summary, "polarityMode", "polarityMode");
        if (!outputs.ContainsKey("scale") && summary.TryGetValue("matchedScale", out var ms) && ms is double msd && double.IsFinite(msd))
            outputs["scale"] = VisionValue.Double(msd);

        var angleStart = node.GetDouble("searchAngleStartDeg", 0);
        var angleEnd = node.GetDouble("searchAngleEndDeg", 0);
        var angleSpan = Math.Abs(angleEnd - angleStart);
        if (angleSpan < 1e-9)
        {
            var half = Math.Abs(node.GetDouble("angleRangeDeg", 0));
            angleSpan = half * 2;
        }
        outputs["angleSpanDeg"] = VisionValue.Double(angleSpan);
        summary["angleSpanDeg"] = angleSpan;
        summary["subpixelMethod"] = summary.TryGetValue("subpixel", out var sp) && sp is true
            ? "jlvision_ncc_subpixel"
            : "none";
        outputs["subpixelMethod"] = VisionValue.String(Convert.ToString(summary["subpixelMethod"], CultureInfo.InvariantCulture) ?? "none");

        var warn = NodeCompletionGuards.CountWarnings(
            count == 0 && found,
            found && score < node.GetDouble("minScore", 0.5) - 1e-9);

        var diag = NodeCompletionGuards.BuildDiagnostics(
            Type, level, warn, attempts,
            ("found", found), ("score", score), ("count", count),
            ("pyramidLevels", summary.GetValueOrDefault("pyramidLevels")),
            ("nmsSuppressedCount", summary.GetValueOrDefault("nmsSuppressedCount")),
            ("nmsSoftDecayedCount", summary.GetValueOrDefault("nmsSoftDecayedCount")),
            ("nmsMode", summary.GetValueOrDefault("nmsMode")),
            ("ignoreMode", summary.GetValueOrDefault("ignoreMode")),
            ("scaleSearch", summary.GetValueOrDefault("scaleSearch")),
            ("scoreMapEmitted", summary.GetValueOrDefault("scoreMapEmitted")),
            ("polarityMode", summary.GetValueOrDefault("polarityMode")),
            ("modelCacheHit", summary.GetValueOrDefault("modelCacheHit")),
            ("angleSpanDeg", angleSpan),
            ("subpixelMethod", summary["subpixelMethod"]),
            ("limits", "JLVision NCC pyramid; hard multi-match prefers Find MaxOverlap; Soft-NMS/soft-ignore are explicit engine extras; scoreMap is diagnostic OpenCV; discrete JL resize scale only (no anisotropic NCC / PatMax)"));

        outputs["diagnostics"] = VisionValue.String(diag);
        summary["diagnostics"] = diag;
        summary["warningCount"] = warn;
        summary["matchingBackend"] = "jlvision";
        summary["algorithmBackend"] = "jlvision";
        summary["scoreMapBackend"] = "opencv-diagnostic";
        summary["diagnosticsLevel"] = level;

        return ValueTask.FromResult(result with { Outputs = outputs, Summary = summary });
    }

    private static void ValidateTemplatePolicies(NodeDefinition node)
    {
        var pyramid = node.GetInt("pyramidLevels", 0);
        if (pyramid is < 0 or > 8)
            throw new InvalidOperationException("pyramidLevels must be between 0 (auto) and 8.");
        var minScore = node.GetDouble("minScore", 0.5);
        if (!double.IsFinite(minScore) || minScore is < 0 or > 1)
            throw new InvalidOperationException("minScore must be finite in [0,1].");
        var maxTargets = node.GetInt("maxTargets", 1);
        if (maxTargets is < 1 or > 50)
            throw new InvalidOperationException("maxTargets must be in 1..50.");
    }

    private static void EmitSummaryDouble(Dictionary<string, VisionValue> outputs, Dictionary<string, object?> summary, string outName, string key)
    {
        if (!summary.TryGetValue(key, out var v) || v is null) return;
        var d = v switch
        {
            double x => x,
            float x => x,
            int x => x,
            long x => x,
            _ => double.NaN
        };
        if (!double.IsFinite(d)) return;
        outputs[outName] = VisionValue.Double(d);
    }

    private static void EmitSummaryString(Dictionary<string, VisionValue> outputs, Dictionary<string, object?> summary, string outName, string key)
    {
        if (!summary.TryGetValue(key, out var v) || v is null) return;
        var s = Convert.ToString(v, CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(s)) return;
        outputs[outName] = VisionValue.String(s);
    }

    private static void EmitSummaryBool(Dictionary<string, VisionValue> outputs, Dictionary<string, object?> summary, string outName, string key)
    {
        if (!summary.TryGetValue(key, out var v) || v is null) return;
        if (v is bool b) outputs[outName] = VisionValue.Boolean(b);
    }
}
