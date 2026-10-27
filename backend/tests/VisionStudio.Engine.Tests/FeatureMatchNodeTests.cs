using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// match.feature（特征点匹配）行为契约：旋转/缩放找回、门限拒绝、参数与模板缺失的硬失败。
/// 场景与模板都用确定性合成图，避免依赖测试图片资源。
/// </summary>
public sealed class FeatureMatchNodeTests
{
    [Fact]
    public async Task FeatureMatch_RecoversRotationScaleAndCentre()
    {
        using var pattern = TexturedPattern();
        using var scene = WarpedScene(pattern, angleDeg: 25, scale: 1.3, centreX: 300, centreY: 260);
        var result = await Run(new FeatureMatchNode(), scene, pattern,
            """{"detector":"sift","transformType":"similarity","minMatches":8,"minInliers":6}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"), Diagnostics(result));
        Assert.InRange(result.Outputs["x"].Require<double>("x"), 297, 303);
        Assert.InRange(result.Outputs["y"].Require<double>("y"), 257, 263);
        Assert.InRange(result.Outputs["scale"].Require<double>("scale"), 1.27, 1.33);
        // 图像坐标 Y 向下，GetRotationMatrix2D 的正角在屏幕上看起来是逆时针，
        // 因此模板 X 轴在场景里的方向读出来是负角。
        Assert.InRange(result.Outputs["angle"].Require<double>("angle"), -27, -23);
        Assert.True(result.Outputs["inlierCount"].Require<long>("inlierCount") >= 15);
        Assert.InRange(result.Outputs["inlierRatio"].Require<double>("inlierRatio"), 0.4, 1.0);
        Assert.InRange(result.Outputs["reprojErrorPx"].Require<double>("reprojErrorPx"), 0, 3.0);

        var pose = result.Outputs["pose"].Require<VisionPose2D>("pose");
        Assert.Equal(result.Outputs["x"].Require<double>("x"), pose.X, 6);
        Assert.Equal(result.Outputs["angle"].Require<double>("angle"), pose.ThetaDeg, 6);

        using var transform = JsonDocument.Parse(result.Outputs["transformJson"].Require<string>("transformJson"));
        Assert.Equal("similarity", transform.RootElement.GetProperty("type").GetString());
        Assert.Equal(3, transform.RootElement.GetProperty("templateToScene").GetArrayLength());
        Assert.Single(result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections"));
        Assert.True(result.Outputs["inlierPoints"].Require<IReadOnlyList<VisionPoint2D>>("inlierPoints").Count >= 15);
    }

    [Fact]
    public async Task FeatureMatch_OrbDetectorFindsTheSamePlacement()
    {
        using var pattern = TexturedPattern();
        using var scene = WarpedScene(pattern, angleDeg: 12, scale: 1.0, centreX: 280, centreY: 300);
        var result = await Run(new FeatureMatchNode(), scene, pattern,
            """{"detector":"orb","transformType":"similarity","maxFeatures":4000,"minInliers":6}""");

        Assert.True(result.Outputs["found"].Require<bool>("found"), Diagnostics(result));
        Assert.InRange(result.Outputs["x"].Require<double>("x"), 273, 287);
        Assert.InRange(result.Outputs["y"].Require<double>("y"), 293, 307);
        Assert.InRange(result.Outputs["angle"].Require<double>("angle"), -15, -9);
    }

    [Fact]
    public async Task FeatureMatch_IsDeterministicForIdenticalInput()
    {
        using var pattern = TexturedPattern();
        using var scene = WarpedScene(pattern, angleDeg: 8, scale: 1.1, centreX: 300, centreY: 260);
        const string json = """{"detector":"sift","transformType":"similarity"}""";
        var first = await Run(new FeatureMatchNode(), scene, pattern, json);
        var second = await Run(new FeatureMatchNode(), scene, pattern, json);
        Assert.Equal(first.Outputs["x"].Require<double>("x"), second.Outputs["x"].Require<double>("x"), 9);
        Assert.Equal(first.Outputs["angle"].Require<double>("angle"), second.Outputs["angle"].Require<double>("angle"), 9);
        Assert.Equal(first.Outputs["inlierCount"].Require<long>("inlierCount"),
            second.Outputs["inlierCount"].Require<long>("inlierCount"));
    }

    [Fact]
    public async Task FeatureMatch_ReturnsNotFoundInsteadOfFailingOnBlankScene()
    {
        using var pattern = TexturedPattern();
        using var blank = new Mat(320, 320, MatType.CV_8UC1, Scalar.All(128));
        var result = await Run(new FeatureMatchNode(), blank, pattern, """{"detector":"sift"}""");
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(0d, result.Outputs["score"].Require<double>("score"));
        Assert.Contains("no-scene-keypoints", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        // 没估出变换时几何端口整体不输出，避免 NaN 流入下游数值节点。
        Assert.False(result.Outputs.ContainsKey("x"));
        Assert.False(result.Outputs.ContainsKey("pose"));
        Assert.False(result.Outputs.ContainsKey("detections"));
    }

    [Fact]
    public async Task FeatureMatch_RejectsScaleOutsideGate()
    {
        using var pattern = TexturedPattern();
        using var scene = WarpedScene(pattern, angleDeg: 0, scale: 1.0, centreX: 300, centreY: 260);
        var result = await Run(new FeatureMatchNode(), scene, pattern,
            """{"detector":"sift","scaleMinPercent":180,"scaleMaxPercent":220}""");
        Assert.False(result.Outputs["found"].Require<bool>("found"));
        Assert.Contains("scale-out-of-range", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        // 即便判定失败，几何量仍然输出，便于现场判断到底是角度还是尺度越界。
        Assert.InRange(result.Outputs["scale"].Require<double>("scale"), 0.97, 1.03);
    }

    [Fact]
    public async Task FeatureMatch_RejectsInvalidParametersAndMissingTemplate()
    {
        using var pattern = TexturedPattern();
        using var scene = WarpedScene(pattern, angleDeg: 0, scale: 1.0, centreX: 300, centreY: 260);

        var detector = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new FeatureMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
                """{"detector":"surf"}"""));
        Assert.Contains("detector", detector.Message);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new FeatureMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) }, "{}"));
        Assert.Contains("template", missing.Message);

        var scales = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new FeatureMatchNode(), scene, pattern,
                """{"scaleMinPercent":300,"scaleMaxPercent":200}"""));
        Assert.Contains("scaleMinPercent", scales.Message);

        var ratio = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new FeatureMatchNode(), scene, pattern, """{"matchRatio":1.5}"""));
        Assert.Contains("matchRatio", ratio.Message);

        using var sixteen = new Mat(64, 64, MatType.CV_32FC1, Scalar.All(1));
        var depth = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new FeatureMatchNode(), sixteen, pattern, "{}"));
        Assert.Contains("8-bit or 16-bit", depth.Message);
    }

    // ---------------------------------------------------------------- 辅助

    private static string Diagnostics(NodeExecutorResult result)
        => result.Outputs["diagnostics"].Require<string>("diagnostics");

    private static async Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor, Mat scene, Mat template, string json)
        => await Run(executor, new Dictionary<string, VisionValue>
        {
            ["image"] = Image(scene),
            ["template"] = Image(template)
        }, json);

    private static async Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, string json)
    {
        try
        {
            var node = new NodeDefinition("n1", executor.Type, null, null,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
            return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as IDisposable)?.Dispose();
        }
    }

    private static VisionValue Image(Mat mat) => VisionValue.Image(VisionImage.Own(mat.Clone()));

    /// <summary>确定性纹理：随机矩形 + 圆，保证 SIFT/ORB 都有足够的强梯度关键点。</summary>
    private static Mat TexturedPattern(int size = 240)
    {
        var mat = new Mat(size, size, MatType.CV_8UC1, Scalar.All(30));
        var random = new Random(7);
        for (var index = 0; index < 70; index++)
        {
            var x = random.Next(0, size - 12);
            var y = random.Next(0, size - 12);
            var width = Math.Min(random.Next(6, 30), size - x);
            var height = Math.Min(random.Next(6, 30), size - y);
            Cv2.Rectangle(mat, new Rect(x, y, width, height), Scalar.All(index % 2 == 0 ? 235 : 90), -1);
        }
        for (var index = 0; index < 14; index++)
        {
            var centre = new Point(random.Next(20, size - 20), random.Next(20, size - 20));
            Cv2.Circle(mat, centre, random.Next(6, 16), Scalar.All(index % 2 == 0 ? 200 : 60), -1);
        }
        return mat;
    }

    /// <summary>把模板旋转 + 缩放到场景中 (centreX, centreY)，返回 520×520 场景。</summary>
    private static Mat WarpedScene(Mat pattern, double angleDeg, double scale, double centreX, double centreY)
    {
        var scene = new Mat(520, 520, MatType.CV_8UC1, Scalar.All(45));
        using var matrix = Cv2.GetRotationMatrix2D(
            new Point2f(pattern.Width / 2f, pattern.Height / 2f), angleDeg, scale);
        var mapped = Apply(matrix, new Point2d(pattern.Width / 2.0, pattern.Height / 2.0));
        matrix.Set(0, 2, matrix.At<double>(0, 2) + (centreX - mapped.X));
        matrix.Set(1, 2, matrix.At<double>(1, 2) + (centreY - mapped.Y));
        Cv2.WarpAffine(pattern, scene, matrix, scene.Size(), InterpolationFlags.Linear,
            BorderTypes.Constant, new Scalar(45));
        return scene;
    }

    private static Point2d Apply(Mat matrix, Point2d point) => new(
        matrix.At<double>(0, 0) * point.X + matrix.At<double>(0, 1) * point.Y + matrix.At<double>(0, 2),
        matrix.At<double>(1, 0) * point.X + matrix.At<double>(1, 1) * point.Y + matrix.At<double>(1, 2));
}
