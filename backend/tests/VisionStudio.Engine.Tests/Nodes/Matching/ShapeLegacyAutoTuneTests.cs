using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// legacy 形状匹配（不训练、直接拿模板图建模型）的运行期自动调参验收：
/// parameterSource=auto 时用 determine_shape_model_params 决定层数/角步长/最小对比度，
/// 结果进 summary.autoTuneShape；manual 路径不产生任何自动字段且行为不变。
/// </summary>
public sealed class ShapeLegacyAutoTuneTests
{
    [Fact]
    public async Task AutoLegacyShape_ReportsRecommendation_AndStillFinds()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = Cv2.GetRotationMatrix2D(new Point2f(60, 60), 15, 1))
        {
            matrix.Set(0, 2, matrix.At<double>(0, 2) + 240 - 60);
            matrix.Set(1, 2, matrix.At<double>(1, 2) + 200 - 60);
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        }

        var result = await Run(scene, template,
            """{"parameterSource":"auto","minScore":0.5,"angleRangeDeg":30,"angleStepDeg":1,"minArea":300}""");

        Assert.True(Convert.ToBoolean(result.Outputs["found"].Require<bool>("found")), SummaryText(result));
        var auto = Assert.IsType<Dictionary<string, object?>>(result.Summary["autoTuneShape"]);
        Assert.Equal("auto-v1", auto["version"]);
        // 摘要里的实际层数/步长必须与调参结果一致，不能留下旧值。
        Assert.Equal(Convert.ToInt32(auto["pyramidLevels"]), Convert.ToInt32(result.Summary["pyramidLevels"]));
        Assert.Equal(Convert.ToDouble(auto["angleStepDeg"]), Convert.ToDouble(result.Summary["angleStepDeg"]), 6);
        Assert.InRange(Convert.ToInt32(auto["pyramidLevels"]), 1, 8);
        Assert.InRange(Convert.ToDouble(auto["angleStepDeg"]), 0.1, 30);
        Assert.InRange(Convert.ToInt32(auto["minContrast"]), 1, 255);
    }

    [Fact]
    public async Task AutoLegacyShape_IsStableAcrossRuns()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = Cv2.GetRotationMatrix2D(new Point2f(60, 60), 15, 1))
        {
            matrix.Set(0, 2, matrix.At<double>(0, 2) + 240 - 60);
            matrix.Set(1, 2, matrix.At<double>(1, 2) + 200 - 60);
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        }

        const string json = """{"parameterSource":"auto","minScore":0.5,"angleRangeDeg":30,"angleStepDeg":1,"minArea":300}""";
        var first = await Run(scene, template, json);
        var second = await Run(scene, template, json);

        var firstAuto = Assert.IsType<Dictionary<string, object?>>(first.Summary["autoTuneShape"]);
        var secondAuto = Assert.IsType<Dictionary<string, object?>>(second.Summary["autoTuneShape"]);
        Assert.Equal(Convert.ToInt32(firstAuto["pyramidLevels"]), Convert.ToInt32(secondAuto["pyramidLevels"]));
        Assert.Equal(Convert.ToDouble(firstAuto["angleStepDeg"]), Convert.ToDouble(secondAuto["angleStepDeg"]), 6);
        Assert.Equal(Convert.ToInt32(firstAuto["minContrast"]), Convert.ToInt32(secondAuto["minContrast"]));
    }

    [Fact]
    public async Task ManualLegacyShape_HasNoAutoReport()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = Cv2.GetRotationMatrix2D(new Point2f(60, 60), 15, 1))
        {
            matrix.Set(0, 2, matrix.At<double>(0, 2) + 240 - 60);
            matrix.Set(1, 2, matrix.At<double>(1, 2) + 200 - 60);
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        }

        var result = await Run(scene, template,
            """{"minScore":0.5,"angleRangeDeg":30,"angleStepDeg":1,"minArea":300}""");

        Assert.True(Convert.ToBoolean(result.Outputs["found"].Require<bool>("found")), SummaryText(result));
        Assert.False(result.Summary.ContainsKey("autoTuneShape"));
        Assert.False(result.Summary.ContainsKey("pyramidLevels"));
    }

    private static async Task<NodeExecutorResult> Run(Mat scene, Mat template, string json)
    {
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(VisionImage.Own(scene.Clone())),
            ["template"] = VisionValue.Image(VisionImage.Own(template.Clone()))
        };
        try
        {
            var node = new NodeDefinition("n1", "match.shape", null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
            return await new ShapeMatchNode().ExecuteAsync(
                new NodeExecutionContext(inputs), node, CancellationToken.None);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as VisionImage)?.Dispose();
        }
    }

    private static string SummaryText(NodeExecutorResult result) => JsonSerializer.Serialize(result.Summary);

    private static Mat BuildTemplate()
    {
        var template = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(template, new Rect(20, 20, 30, 90), Scalar.All(255), -1);
        Cv2.Rectangle(template, new Rect(20, 80, 70, 30), Scalar.All(255), -1);
        return template;
    }
}
