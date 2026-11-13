using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// measure.metrology 验收：混合对象建一个计量模型，几何结果按类型解码，
/// 位姿对齐把模型坐标搬到图像坐标，circle/line 残差来自原生测量点。
/// </summary>
public sealed class MetrologyNodeTests
{
    [Fact]
    public async Task MeasuresCircle_WithSubpixelAccuracy()
    {
        const double row = 125, column = 130, radius = 58;
        using var mat = Disk(row, column, radius, 260);
        var result = await Run(mat, new()
        {
            ["objects"] = """[{"type":"circle","name":"bore","row":125,"column":130,"radius":58}]""",
            ["measureLength1"] = 24, ["measureLength2"] = 6, ["measureThreshold"] = 20
        });
        using var _ = result.Outputs["image"].Require<VisionImage>("image");

        Assert.True(result.Outputs["found"].Require<bool>("found"), Convert.ToString(result.Summary["reason"]));
        Assert.Equal(1L, result.Outputs["foundCount"].Require<long>("foundCount"));

        using var doc = JsonDocument.Parse(result.Outputs["measurements"].Require<string>("measurements"));
        var item = doc.RootElement[0];
        Assert.Equal("circle", item.GetProperty("type").GetString());
        Assert.InRange(item.GetProperty("row").GetDouble(), row - 0.25, row + 0.25);
        Assert.InRange(item.GetProperty("column").GetDouble(), column - 0.25, column + 0.25);
        Assert.InRange(item.GetProperty("radius").GetDouble(), radius - 0.3, radius + 0.3);
        Assert.True(item.GetProperty("residualPx").GetDouble() < 0.25);
        Assert.True(result.Outputs["residualRms"].Require<double>("residualRms") < 0.25);

        Assert.Contains(result.Overlays ?? [], o => o.Id == "metrology-bore" && o.Type == VisionOverlayType.Circle);
    }

    [Fact]
    public async Task MeasuresLines_InHesseForm()
    {
        using var mat = Block(55, 195, 60, 200, 260);
        var result = await Run(mat, new()
        {
            ["objects"] = """
                [{"type":"line","name":"top","rowBegin":70,"columnBegin":60,"rowEnd":70,"columnEnd":200},
                 {"type":"line","name":"bottom","rowBegin":190,"columnBegin":60,"rowEnd":190,"columnEnd":200}]
                """,
            ["measureLength1"] = 20, ["measureLength2"] = 5, ["measureThreshold"] = 20
        });
        using var _ = result.Outputs["image"].Require<VisionImage>("image");

        Assert.True(result.Outputs["found"].Require<bool>("found"), Convert.ToString(result.Summary["reason"]));
        using var doc = JsonDocument.Parse(result.Outputs["measurements"].Require<string>("measurements"));

        var top = doc.RootElement[0];
        Assert.Equal(1d, top.GetProperty("ny").GetDouble(), 3);
        Assert.Equal(0d, top.GetProperty("nx").GetDouble(), 3);
        Assert.InRange(top.GetProperty("distancePx").GetDouble(), 54.5 - 0.3, 54.5 + 0.3);
        Assert.True(top.GetProperty("residualPx").GetDouble() < 0.3);

        var bottom = doc.RootElement[1];
        // 方块填充到 row=194（矩形右下开区间），边界位于 194/195 之间 → 194.5。
        Assert.InRange(bottom.GetProperty("distancePx").GetDouble(), 194.5 - 0.3, 194.5 + 0.3);
    }

    [Fact]
    public async Task PoseAlignment_MovesObjectsIntoImageCoordinates()
    {
        // 图像里的圆在 (135,140)，模型定义在 (125,130)，pose 把模型平移 (+10,+10)。
        using var mat = Disk(135, 140, 58, 260);
        var pose = VisionValue.Pose(new VisionPose2D(10, 10, 0));
        var result = await Run(mat, new()
        {
            ["objects"] = """[{"type":"circle","name":"bore","row":125,"column":130,"radius":58}]""",
            ["alignMode"] = "pose",
            ["measureLength1"] = 24, ["measureLength2"] = 6, ["measureThreshold"] = 20
        }, new Dictionary<string, VisionValue> { ["pose"] = pose });
        using var _ = result.Outputs["image"].Require<VisionImage>("image");

        Assert.True(result.Outputs["found"].Require<bool>("found"), Convert.ToString(result.Summary["reason"]));
        using var doc = JsonDocument.Parse(result.Outputs["measurements"].Require<string>("measurements"));
        var item = doc.RootElement[0];
        Assert.InRange(item.GetProperty("row").GetDouble(), 135 - 0.3, 135 + 0.3);
        Assert.InRange(item.GetProperty("column").GetDouble(), 140 - 0.3, 140 + 0.3);
    }

    [Fact]
    public async Task RequireAllFound_ReportsPartialSuccessAsNotFound()
    {
        using var mat = Disk(125, 130, 58, 260);
        var result = await Run(mat, new()
        {
            ["objects"] = """
                [{"type":"circle","name":"good","row":125,"column":130,"radius":58},
                 {"type":"line","name":"missing","rowBegin":10,"columnBegin":10,"rowEnd":10,"columnEnd":40}]
                """,
            ["measureLength1"] = 24, ["measureLength2"] = 6, ["measureThreshold"] = 20,
            ["requireAllFound"] = true
        });
        using var _ = result.Outputs["image"].Require<VisionImage>("image");

        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(1L, result.Outputs["foundCount"].Require<long>("foundCount"));
        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
    }

    [Fact]
    public async Task RejectsUnknownObjectType()
    {
        using var mat = Disk(125, 130, 58, 260);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(mat, new()
        {
            ["objects"] = """[{"type":"spiral","row":1,"column":1,"radius":2}]"""
        }));
        Assert.Contains("unsupported type", error.Message);
    }

    [Fact]
    public async Task RejectsZeroLengthLine()
    {
        using var mat = Disk(125, 130, 58, 260);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(mat, new()
        {
            ["objects"] = """[{"type":"line","rowBegin":10,"columnBegin":10,"rowEnd":10,"columnEnd":10}]"""
        }));
        Assert.Contains("zero length", error.Message);
    }

    private static async Task<NodeExecutorResult> Run(
        Mat mat,
        Dictionary<string, object?> parameters,
        Dictionary<string, VisionValue>? extraInputs = null)
    {
        using var image = VisionImage.Own(mat.Clone());
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) };
        foreach (var (key, value) in extraInputs ?? new Dictionary<string, VisionValue>()) inputs[key] = value;
        var node = new NodeDefinition("metrology", "measure.metrology", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters)));
        return await new MetrologyNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    private static Mat Disk(double row, double column, double radius, int size)
    {
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(25));
        Cv2.Circle(mat, new Point((int)column, (int)row), (int)radius, Scalar.All(210), -1, LineTypes.Link8);
        return mat;
    }

    private static Mat Block(int row1, int row2, int column1, int column2, int size)
    {
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(25));
        Cv2.Rectangle(mat, new Rect(column1, row1, column2 - column1, row2 - row1), Scalar.All(210), -1);
        return mat;
    }
}
