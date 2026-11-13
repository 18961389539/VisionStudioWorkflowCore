using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 训练期自动调参验收：parameterSource=auto 时由 determine_shape_model_params 决定
/// 层数/角步长/最小对比度，并把结果与模板质量写进训练摘要；manual 路径必须逐字节不变。
/// </summary>
public sealed class MatchingAutoTuneTrainingTests
{
    [Fact]
    public async Task AutoTraining_OverridesLevelsAndReportsQuality()
    {
        using var template = BuildTemplate();

        var result = await Train(template,
            """{"modelMode":"trained","trainingMode":true,"angleRangeDeg":180,"angleStepDeg":2,"minContrast":5,"pyramidLevels":0,"parameterSource":"auto","autoTuneProfile":"balanced","minArea":100}""");

        Assert.True(Convert.ToBoolean(result.Summary["trainingSucceeded"]));
        var auto = Assert.IsType<Dictionary<string, object?>>(result.Summary["trainingAutoTune"]);
        Assert.Equal("auto-v1", auto["version"]);
        Assert.Equal(Convert.ToInt32(auto["pyramidLevels"]), Convert.ToInt32(result.Summary["trainingPyramidLevels"]));
        Assert.InRange(Convert.ToDouble(auto["angleStepDeg"]), 0.1, 30);
        Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(auto["quality"])));
        Assert.NotNull(result.Summary["trainingModelQuality"]);
        Assert.True(Convert.ToInt32(result.Summary["trainingModelPointCount"]) > 0);
        Assert.Contains("auto-v1", Convert.ToString(result.Summary["trainingConfig"]));
        Assert.NotEmpty(Convert.ToString(result.Summary["trainingModelData"])!);
    }

    [Fact]
    public async Task ManualTraining_KeepsConfigAndOmitsAutoReport()
    {
        using var template = BuildTemplate();

        var result = await Train(template,
            """{"modelMode":"trained","trainingMode":true,"angleRangeDeg":180,"angleStepDeg":2,"minContrast":5,"pyramidLevels":0,"minArea":100}""");

        Assert.True(Convert.ToBoolean(result.Summary["trainingSucceeded"]));
        Assert.Null(result.Summary["trainingAutoTune"]);
        Assert.DoesNotContain("auto-v1", Convert.ToString(result.Summary["trainingConfig"]));
        Assert.Equal(0, Convert.ToInt32(result.Summary["trainingPyramidLevels"]));
    }

    [Fact]
    public async Task AutoTraining_IsDeterministicAcrossRuns()
    {
        using var template = BuildTemplate();

        var first = await Train(template, AutoParameters());
        var second = await Train(template, AutoParameters());

        Assert.Equal(Convert.ToString(first.Summary["trainingConfig"]), Convert.ToString(second.Summary["trainingConfig"]));
        Assert.Equal(Convert.ToInt32(first.Summary["trainingPyramidLevels"]), Convert.ToInt32(second.Summary["trainingPyramidLevels"]));
        var firstAuto = Assert.IsType<Dictionary<string, object?>>(first.Summary["trainingAutoTune"]);
        var secondAuto = Assert.IsType<Dictionary<string, object?>>(second.Summary["trainingAutoTune"]);
        Assert.Equal(Convert.ToInt32(firstAuto["pyramidLevels"]), Convert.ToInt32(secondAuto["pyramidLevels"]));
        Assert.Equal(Convert.ToDouble(firstAuto["angleStepDeg"]), Convert.ToDouble(secondAuto["angleStepDeg"]));
        Assert.Equal(Convert.ToInt32(firstAuto["minContrast"]), Convert.ToInt32(secondAuto["minContrast"]));
        // 序列化模型含原生运行时时间戳，不做字节级比较；两侧都必须产出可用资产。
        Assert.NotEmpty(Convert.ToString(first.Summary["trainingModelData"])!);
        Assert.NotEmpty(Convert.ToString(second.Summary["trainingModelData"])!);
    }

    [Fact]
    public async Task AutoTraining_LockedLevelsAreHonoured()
    {
        using var template = BuildTemplate();

        var result = await Train(template,
            """{"modelMode":"trained","trainingMode":true,"angleRangeDeg":180,"angleStepDeg":2,"minContrast":5,"pyramidLevels":2,"parameterSource":"auto","autoTuneLockedParams":"num_levels","minArea":100}""");

        Assert.True(Convert.ToBoolean(result.Summary["trainingSucceeded"]));
        Assert.Equal(2, Convert.ToInt32(result.Summary["trainingPyramidLevels"]));
        var auto = Assert.IsType<Dictionary<string, object?>>(result.Summary["trainingAutoTune"]);
        Assert.Contains("levels-locked", Assert.IsType<string[]>(auto["warnings"]));
    }

    private static string AutoParameters()
        => """{"modelMode":"trained","trainingMode":true,"angleRangeDeg":180,"angleStepDeg":2,"minContrast":5,"pyramidLevels":0,"parameterSource":"auto","minArea":100}""";

    private static async Task<NodeExecutorResult> Train(Mat template, string parameters)
    {
        using var scene = template.Clone();
        using var image = VisionImage.Own(scene.Clone());
        using var templateImage = VisionImage.Own(template.Clone());
        var node = new NodeDefinition("shape", "match.shape", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters));
        return await new ShapeMatchNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(image),
                ["template"] = VisionValue.Image(templateImage)
            }),
            node, CancellationToken.None);
    }

    private static Mat BuildTemplate()
    {
        var mat = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(mat, new Rect(25, 30, 60, 45), Scalar.All(200), -1);
        Cv2.Circle(mat, new Point(88, 88), 14, Scalar.All(160), -1);
        Cv2.Line(mat, new Point(20, 100), new Point(100, 100), Scalar.All(120), 3);
        return mat;
    }
}
