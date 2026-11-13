using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// NCC 运行期自动调参验收：parameterSource=auto 时用 determine_ncc_model_params
/// 决定层数与角步长，结果进 summary；manual 路径不产生任何自动字段。
/// </summary>
public sealed class NccAutoTuneTests
{
    [Fact]
    public async Task AutoNcc_ReportsRecommendation_AndStillFinds()
    {
        using var template = Template();
        using var scene = SceneWith(template);

        var result = await Run(scene, template,
            """{"parameterSource":"auto","angleRangeDeg":0,"minScore":0.6,"maxTargets":1}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"), Reason(result));
        var auto = Assert.IsType<Dictionary<string, object?>>(result.Summary["autoTuneNcc"]);
        Assert.Equal("auto-v1", auto["version"]);
        Assert.Equal(Convert.ToInt32(auto["pyramidLevels"]), Convert.ToInt32(result.Summary["pyramidLevels"]));
        Assert.InRange(Convert.ToDouble(auto["angleStepDeg"]), 0.1, 30);
    }

    [Fact]
    public async Task AutoNcc_IsStableAcrossRuns()
    {
        using var template = Template();
        using var scene = SceneWith(template);
        const string json = """{"parameterSource":"auto","angleRangeDeg":0,"minScore":0.6}""";

        var first = await Run(scene, template, json);
        var second = await Run(scene, template, json);

        var firstAuto = Assert.IsType<Dictionary<string, object?>>(first.Summary["autoTuneNcc"]);
        var secondAuto = Assert.IsType<Dictionary<string, object?>>(second.Summary["autoTuneNcc"]);
        Assert.Equal(Convert.ToInt32(firstAuto["pyramidLevels"]), Convert.ToInt32(secondAuto["pyramidLevels"]));
        Assert.Equal(Convert.ToDouble(firstAuto["angleStepDeg"]), Convert.ToDouble(secondAuto["angleStepDeg"]));
    }

    [Fact]
    public async Task ManualNcc_HasNoAutoReport()
    {
        using var template = Template();
        using var scene = SceneWith(template);

        var result = await Run(scene, template, """{"angleRangeDeg":0,"minScore":0.6}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"), Reason(result));
        Assert.False(result.Summary.ContainsKey("autoTuneNcc"));
    }

    private static string? Reason(NodeExecutorResult result)
        => result.Summary.TryGetValue("reason", out var value) ? Convert.ToString(value) : null;

    private static async Task<NodeExecutorResult> Run(Mat scene, Mat template, string json)
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(VisionImage.Own(scene.Clone())),
            ["template"] = VisionValue.Image(VisionImage.Own(template.Clone()))
        };
        try
        {
            var node = new NodeDefinition("n1", "match.template", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
            return await new TemplateMatchNode().ExecuteAsync(
                new NodeExecutionContext(inputs), node, CancellationToken.None);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
        }
    }

    private static Mat Template()
    {
        var template = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(template, new Rect(6, 8, 20, 8), Scalar.All(230), -1);
        Cv2.Circle(template, new Point(11, 24), 3, Scalar.All(200), -1);
        return template;
    }

    private static Mat SceneWith(Mat template)
    {
        var scene = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(10));
        using var roi = new Mat(scene, new Rect(60, 55, template.Width, template.Height));
        template.CopyTo(roi);
        return scene;
    }
}
