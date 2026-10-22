using System.Text.Json;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Contract tests for match.template / match.shape and the diagnostics helpers they use.</summary>
public sealed class MatchingContractTests
{
    [Fact]
    public void Catalog_MatchNodes_ExposePyramidAndRefineDiagnostics()
    {
        var template = BuiltInNodeCatalog.Items.Single(x => x.Type == "match.template");
        Assert.Contains(template.Outputs, o => o.Name == "diagnostics");
        Assert.Contains(template.Outputs, o => o.Name == "pyramidLevels");
        Assert.Contains(template.Outputs, o => o.Name == "nmsSuppressedCount");
        Assert.Contains(template.Outputs, o => o.Name == "subpixelMethod");
        Assert.Contains(template.Outputs, o => o.Name == "scoreMap");
        Assert.Contains(template.Parameters, p => p.Name == "diagnosticsLevel");
        Assert.Contains(template.Parameters, p => p.Name == "pyramidLevels");
        Assert.Contains(template.Parameters, p => p.Name == "nmsMode");

        var shape = BuiltInNodeCatalog.Items.Single(x => x.Type == "match.shape");
        Assert.Contains(shape.Outputs, o => o.Name == "diagnostics");
        Assert.Contains(shape.Outputs, o => o.Name == "pyramidLevels");
        Assert.Contains(shape.Outputs, o => o.Name == "angleRefineDeltaDeg");
        Assert.Contains(shape.Outputs, o => o.Name == "xldSnapDeltaPx");
        Assert.Contains(shape.Outputs, o => o.Name == "anisotropicScale");
        Assert.Contains(shape.Outputs, o => o.Name == "subpixelMethod");
        Assert.Contains(shape.Parameters, p => p.Name == "chamferContinuousRefine");
        Assert.Contains(shape.Parameters, p => p.Name == "chamferXldSnap");
        Assert.Contains(shape.Parameters, p => p.Name == "maxDeformationPx");
        Assert.Contains("PatMax", shape.Description ?? "");
    }

    [Fact]
    public void ChamferShapeMatching_ModelMetrics_AndContinuousOptions_CompileSurface()
    {
        var pts = Enumerable.Range(0, 32)
            .Select(i => new ChamferShapeMatching.ModelPoint(Math.Cos(i / 5.0) * 10, Math.Sin(i / 5.0) * 10, 1, 0))
            .ToArray();
        var model = ChamferShapeMatching.BuildModel(pts, 24);
        var metrics = ChamferShapeMatching.ModelMetrics(model);
        Assert.True(metrics.PointCount >= 8);
        Assert.True(metrics.OrientedCount >= 1);
        Assert.True(metrics.MeanRadiusPx > 0);

        var o = new ChamferShapeMatching.Options(
            AngleMinDeg: -5, AngleMaxDeg: 5, AngleStepDeg: 5,
            ScaleMin: 1, ScaleMax: 1, ScaleStep: 0.1,
            MinScore: 0.99, MaxTargets: 1, MinCenterDistance: 10,
            PyramidLevels: 2, ContinuousPoseRefine: true, XldContourSnap: true);
        Assert.Equal(2, o.PyramidLevels);
        Assert.True(o.ContinuousPoseRefine);
        Assert.True(o.XldContourSnap);
    }

    [Fact]
    public void DiagnosticsHelpers_BuildVerbosePayloads()
    {
        var node = new NodeDefinition("t", "match.template", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"diagnosticsLevel":"verbose","retryPolicy":"once"}""")!);
        Assert.Equal("verbose", NodeCompletionGuards.DiagnosticsLevel(node));
        Assert.Equal("once", NodeCompletionGuards.RetryPolicy(node));
        Assert.Equal(2, NodeCompletionGuards.RetryAttempts("once"));
        var diag = NodeCompletionGuards.BuildDiagnostics("match.template", "verbose", 0, 1,
            ("pyramidLevels", 3), ("subpixelMethod", "jlvision_ncc_subpixel"));
        Assert.Contains("match.template", diag);
        Assert.Contains("pyramidLevels", diag);
        var shapeDiag = NodeCompletionGuards.BuildDiagnostics("match.shape", "standard", 1, 1,
            ("engine", "managedChamfer"), ("xldSnapDeltaPx", 0.4));
        Assert.Contains("match.shape", shapeDiag);
    }

    [Fact]
    public void ShapeEngine_Catalog_AutoIsNativeOnly_NoSilentChamferPromise()
    {
        var shape = BuiltInNodeCatalog.Items.Single(x => x.Type == "match.shape");
        var engine = Assert.Single(shape.Parameters, p => p.Name == "engine");
        Assert.Equal("native", Convert.ToString(engine.DefaultValue));
        var blob = (shape.Description ?? "") + "\n" + (engine.Description ?? "");
        Assert.NotNull(engine.Options);
        foreach (var opt in engine.Options!)
            blob += "\n" + opt.Label + "|" + opt.Value;
        Assert.DoesNotContain("chamfer fallback", blob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("falls back to chamfer only when the native library is unavailable", blob, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never silently", blob, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(engine.Options, o => o.Value == "managedChamfer");
        Assert.Contains(engine.Options, o => o.Value == "auto");
    }
}
