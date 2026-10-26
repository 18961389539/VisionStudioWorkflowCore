using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 条码质量指标测试。用合成棋盘格构造已知 SC/MOD/AN，验证分级与边界。
/// 注意：这是"指示性等级"的实现，测试验证的是本节点的口径，不是 ISO 认证能力。
/// </summary>
public sealed class BarcodeQualityNodeTests
{
    [Fact]
    public async Task HighContrastCheckerboardGradesA()
    {
        using var image = Checkerboard(cells: 10, cellPx: 8, dark: 20, light: 220);

        var result = await Run(image, Node(("rows", "10"), ("cols", "10")));

        Assert.Equal(200d, result.Outputs["symbolContrast"].Require<double>("symbolContrast"), 0.5);
        Assert.True(result.Outputs["modulation"].Require<double>("modulation") > 0.9);
        Assert.True(result.Outputs["axialNonUniformity"].Require<double>("axialNonUniformity") < 0.02);
        Assert.Equal("A", result.Outputs["grade"].Require<string>("grade"));
        Assert.Equal(4d, result.Outputs["numericGrade"].Require<double>("numericGrade"), 1e-9);
        Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        // 棋盘格深色单元约占一半。
        Assert.Equal(0.5d, result.Outputs["darkFraction"].Require<double>("darkFraction"), 0.02);
    }

    [Fact]
    public async Task LowContrastIsCappedByContrastGrade()
    {
        // SC=50 → 对比度 C；调制与轴向都很好，总等级仍取最差项。
        using var image = Checkerboard(cells: 10, cellPx: 8, dark: 100, light: 150);

        var result = await Run(image, Node(("rows", "10"), ("cols", "10")));
        Assert.Equal("C", result.Outputs["grade"].Require<string>("grade"));

        // minGrade=B 时 C 不合格。
        var strict = await Run(image, Node(("rows", "10"), ("cols", "10"), ("minGrade", "B")));
        Assert.False(strict.Outputs["pass"].Require<bool>("pass"));
    }

    [Fact]
    public async Task AxialNonUniformityCatchesBrightnessGradient()
    {
        // 左右半区亮度差很大：SC 仍高，但 AN 应把等级压到 F。
        using var image = GradientCheckerboard(leftLight: 250, rightLight: 150, dark: 30);

        var result = await Run(image, Node(("rows", "10"), ("cols", "10")));
        Assert.True(result.Outputs["axialNonUniformity"].Require<double>("axialNonUniformity") > 0.12);
        Assert.Equal("F", result.Outputs["grade"].Require<string>("grade"));
    }

    [Fact]
    public async Task UndecodableSymbolGradesFImmediately()
    {
        using var image = Checkerboard(cells: 10, cellPx: 8, dark: 20, light: 220);

        var result = await Run(image, Node(("rows", "10"), ("cols", "10")), ("decodeOk", VisionValue.Boolean(false)));

        // 指标本身是 A，但解码失败按 ISO 直接 F。
        Assert.True(result.Outputs["modulation"].Require<double>("modulation") > 0.9);
        Assert.Equal("F", result.Outputs["grade"].Require<string>("grade"));
        Assert.False(result.Outputs["pass"].Require<bool>("pass"));
    }

    [Fact]
    public async Task WholeRoiModeEmitsContrastOnly()
    {
        using var image = Checkerboard(cells: 10, cellPx: 8, dark: 20, light: 220);

        var result = await Run(image, Node(("gridMode", "whole")));
        Assert.Equal(200d, result.Outputs["symbolContrast"].Require<double>("symbolContrast"), 0.5);
        // 没有网格就没有调制/轴向指标，不输出这两个端口（避免给出无依据的数字）。
        Assert.False(result.Outputs.ContainsKey("modulation"));
        Assert.False(result.Outputs.ContainsKey("axialNonUniformity"));
        Assert.Equal("A", result.Outputs["grade"].Require<string>("grade"));
    }

    [Fact]
    public async Task CellSizeModeDerivesGridFromPitch()
    {
        using var image = Checkerboard(cells: 10, cellPx: 8, dark: 20, light: 220);

        var result = await Run(image, Node(("gridMode", "cellsize"), ("cellSizePx", "8")));
        Assert.Equal("A", result.Outputs["grade"].Require<string>("grade"));
        Assert.True(result.Outputs["modulation"].Require<double>("modulation") > 0.9);
    }

    [Fact]
    public async Task RoiRestrictsTheGridToTheSymbol()
    {
        // 200x200 亮场，只在 (10,10) 放一块 80x80 的 10x10 棋盘格。
        using var mat = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(240));
        using (var pattern = Checkerboard(cells: 10, cellPx: 8, dark: 20, light: 220))
        using (var target = new Mat(mat, new Rect(10, 10, 80, 80)))
            pattern.Mat.CopyTo(target);

        // 不接 ROI：网格摊到整幅图，单元均值被背景拉平，指标与等级都会变差。
        var wholeImage = await Run(mat, Node(("rows", "10"), ("cols", "10")));
        Assert.NotEqual("A", wholeImage.Outputs["grade"].Require<string>("grade"));

        // 接上 ROI：网格精确落在符号上，等级回到 A。
        var roi = new VisionRoi(VisionRoiType.Rectangle, 10, 10, 80, 80);
        var withRoi = await Run(mat, Node(("rows", "10"), ("cols", "10")), ("roi", VisionValue.Roi(roi)));
        Assert.Equal("A", withRoi.Outputs["grade"].Require<string>("grade"));
        Assert.True(withRoi.Outputs["modulation"].Require<double>("modulation") > 0.9);
    }

    [Fact]
    public async Task FlatImageFailsWithExplicitMessage()
    {
        using var mat = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(128));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(mat, Node()));
        Assert.Contains("no readable symbol", error.Message);
    }

    [Fact]
    public async Task RejectsInvalidGridConfiguration()
    {
        using var image = Checkerboard(cells: 10, cellPx: 8, dark: 20, light: 220);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(image, Node(("gridMode", "rows"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(image, Node(("gridMode", "modules"), ("rows", "0"), ("cols", "10"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(image, Node(("minGrade", "E"))));
    }

    // ===== 夹具 =====

    private static VisionImage Checkerboard(int cells, int cellPx, byte dark, byte light)
    {
        var size = cells * cellPx;
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(light));
        for (var row = 0; row < cells; row++)
            for (var col = 0; col < cells; col++)
                if ((row + col) % 2 == 0)
                    Cv2.Rectangle(mat, new Rect(col * cellPx, row * cellPx, cellPx, cellPx), Scalar.All(dark), -1);
        return VisionImage.Own(mat);
    }

    private static VisionImage GradientCheckerboard(byte leftLight, byte rightLight, byte dark)
    {
        const int cells = 10, cellPx = 8;
        var size = cells * cellPx;
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(leftLight));
        for (var row = 0; row < cells; row++)
            for (var col = 0; col < cells; col++)
            {
                var light = col < cells / 2 ? leftLight : rightLight;
                var value = (row + col) % 2 == 0 ? dark : light;
                Cv2.Rectangle(mat, new Rect(col * cellPx, row * cellPx, cellPx, cellPx), Scalar.All(value), -1);
            }
        return VisionImage.Own(mat);
    }

    private static async Task<NodeExecutorResult> Run(Mat source, NodeDefinition node, params (string Port, VisionValue Value)[] extra)
    {
        using var owned = VisionImage.Own(source.Clone());
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(owned) };
        foreach (var (port, value) in extra) inputs[port] = value;
        return await new BarcodeQualityNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    private static async Task<NodeExecutorResult> Run(VisionImage image, NodeDefinition node, params (string Port, VisionValue Value)[] extra)
    {
        var inputs = new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image) };
        foreach (var (port, value) in extra) inputs[port] = value;
        return await new BarcodeQualityNode().ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    private static NodeDefinition Node(params (string Key, string Value)[] parameters)
    {
        var payload = parameters.ToDictionary(x => x.Key, x => (object)x.Value);
        return new NodeDefinition("n1", "recognition.barcodeQuality", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload)));
    }
}
