using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class ManagedShapeMatchPolarityClutterTests
{
    [Fact]
    public async Task IgnorePolarityFindsInvertedScene_UsePolarityRejectsIt()
    {
        using var template = MakeBrightRectOnDark(96, 96, 28, 28, 40, 28);
        using var scene = MakeBrightRectOnDark(160, 160, 60, 60, 40, 28);
        using var inverted = Invert(scene);

        var useOnNormal = await Run(scene, template, new Dictionary<string, object?> { ["ignorePolarity"] = false, ["minScore"] = 0.45, ["minContrast"] = 5 });
        DisposeImage(useOnNormal);
        Assert.True(Found(useOnNormal), "polarity-on should find same-polarity scene");
        Assert.Equal("use_polarity", useOnNormal.Outputs["polarityMode"].Require<string>("polarityMode"));
        Assert.True(useOnNormal.Outputs["orientedModelPointCount"].Require<long>("orientedModelPointCount") > 0);
        Assert.True(useOnNormal.Outputs.ContainsKey("coverageScore"));
        Assert.True(useOnNormal.Outputs.ContainsKey("distanceScore"));

        var useOnInverted = await Run(inverted, template, new Dictionary<string, object?> { ["ignorePolarity"] = false, ["minScore"] = 0.45, ["minContrast"] = 5 });
        DisposeImage(useOnInverted);
        Assert.False(Found(useOnInverted), $"polarity-on must reject inverted; score={useOnInverted.Outputs["score"].Require<double>("score")}");

        var ignoreOnInverted = await Run(inverted, template, new Dictionary<string, object?> { ["ignorePolarity"] = true, ["minScore"] = 0.45 });
        DisposeImage(ignoreOnInverted);
        Assert.True(Found(ignoreOnInverted), "ignorePolarity must recover inverted scene");
        Assert.Equal("ignore_polarity", ignoreOnInverted.Outputs["polarityMode"].Require<string>("polarityMode"));
    }

    [Fact]
    public async Task ClutterRoiRejectsAndScoreUsingClutterDepresses()
    {
        using var template = MakeBrightRectOnDark(80, 80, 20, 20, 40, 28);
        using var cluttered = MakeBrightRectOnDark(180, 180, 70, 70, 40, 28);
        for (var x = 8; x < 172; x += 8)
        {
            Cv2.Line(cluttered.Mat, new Point(x, 6), new Point(x, 48), Scalar.All(255), 2);
            Cv2.Line(cluttered.Mat, new Point(x, 132), new Point(x, 174), Scalar.All(255), 2);
        }
        for (var y = 8; y < 172; y += 8)
        {
            Cv2.Line(cluttered.Mat, new Point(6, y), new Point(48, y), Scalar.All(255), 2);
            Cv2.Line(cluttered.Mat, new Point(132, y), new Point(174, y), Scalar.All(255), 2);
        }

        var baseline = await Run(cluttered, template, new Dictionary<string, object?>
        {
            ["ignorePolarity"] = true,
            ["minScore"] = 0.25,
            ["clutterRoi"] = new { type = "Rectangle", x = 0, y = 0, width = 180, height = 180 },
            ["maxClutterScore"] = 1.0,
            ["scoreUsingClutter"] = false
        });
        DisposeImage(baseline);
        Assert.True(Convert.ToBoolean(baseline.Summary["clutterEnabled"]), "clutterRoi must enable managed clutter");
        Assert.True(Found(baseline), "baseline with maxClutterScore=1 should find");
        var clutter = baseline.Outputs["clutterScore"].Require<double>("clutterScore");
        Assert.True(clutter > 0.2, $"expected unexplained edges; clutter={clutter}");

        var rejected = await Run(cluttered, template, new Dictionary<string, object?>
        {
            ["ignorePolarity"] = true,
            ["minScore"] = 0.25,
            ["clutterRoi"] = new { type = "Rectangle", x = 0, y = 0, width = 180, height = 180 },
            ["maxClutterScore"] = Math.Max(0.01, clutter * 0.5),
            ["scoreUsingClutter"] = false
        });
        DisposeImage(rejected);
        Assert.False(Found(rejected), $"maxClutterScore below measured clutter ({clutter}) must reject");

        var depressed = await Run(cluttered, template, new Dictionary<string, object?>
        {
            ["ignorePolarity"] = true,
            ["minScore"] = 0.05,
            ["clutterRoi"] = new { type = "Rectangle", x = 0, y = 0, width = 180, height = 180 },
            ["maxClutterScore"] = 1.0,
            ["scoreUsingClutter"] = true
        });
        DisposeImage(depressed);
        Assert.True(Found(depressed));
        var native = depressed.Outputs["nativeScore"].Require<double>("nativeScore");
        var score = depressed.Outputs["score"].Require<double>("score");
        var clut2 = depressed.Outputs["clutterScore"].Require<double>("clutterScore");
        Assert.True(native > score + 1e-6, $"native={native} score={score} clutter={clut2}");
    }

    private static bool Found(NodeExecutorResult r) => r.Outputs["found"].Require<bool>("found");
    private static void DisposeImage(NodeExecutorResult r) => r.Outputs["image"].Require<VisionImage>("image").Dispose();

    private static VisionImage MakeBrightRectOnDark(int w, int h, int x, int y, int rw, int rh)
    {
        var mat = new Mat(h, w, MatType.CV_8UC1, Scalar.All(30));
        using (var region = new Mat(mat, new Rect(x, y, rw, rh))) region.SetTo(Scalar.All(220));
        return VisionImage.Own(mat);
    }

    private static VisionImage Invert(VisionImage source)
    {
        var mat = new Mat();
        Cv2.BitwiseNot(source.Mat, mat);
        return VisionImage.Own(mat);
    }

    private static async Task<NodeExecutorResult> Run(VisionImage scene, VisionImage template, Dictionary<string, object?> parameters)
    {
        var settings = new Dictionary<string, object?>
        {
            ["engine"] = "managedChamfer",
            ["searchAngleStartDeg"] = -15,
            ["searchAngleEndDeg"] = 15,
            ["chamferAngleStepDeg"] = 5,
            ["searchScaleMinPercent"] = 100,
            ["searchScaleMaxPercent"] = 100,
            ["chamferTolerancePx"] = 3,
            ["maxTargets"] = 1,
            ["chamferCannyLow"] = 40,
            ["chamferCannyHigh"] = 120
        };
        foreach (var (k, v) in parameters) settings[k] = v;
        var node = new NodeDefinition("m", "match.shape", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(settings)));
        return await new ShapeMatchNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(scene),
                ["template"] = VisionValue.Image(template)
            }),
            node, CancellationToken.None);
    }
}