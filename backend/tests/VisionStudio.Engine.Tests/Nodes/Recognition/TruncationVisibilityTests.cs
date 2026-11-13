using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// top-N 截断必须对下游可见：count 只说明发出了多少，inputCount/truncated 才能区分
/// "确实只有这么多个"和"被 maxCount 裁到这么多个"。
/// 之前 measure.blobs（默认 20）、feature.ellipse（默认 10）、contour.extract（默认 200）
/// 都只返回裁剪后的列表，数量统计和 SPC 会静默少算。
/// </summary>
public sealed class TruncationVisibilityTests
{
    private static NodeDefinition Def(string type, string json)
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    private static VisionValue Img(Mat mat) => VisionValue.Image(VisionImage.Own(mat.Clone()));

    [Fact]
    public async Task MeasureBlobs_ReportsThePreCapCandidateCount()
    {
        using var image = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(image, new Rect(10, 10, 12, 12), Scalar.All(255), -1);
        Cv2.Rectangle(image, new Rect(60, 60, 12, 12), Scalar.All(255), -1);

        var capped = await new MultiBlobNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("measure.blobs", """{"minArea":20,"maxCount":1}"""), CancellationToken.None);
        Assert.Equal(1L, (long)capped.Outputs["count"].Value!);
        Assert.Equal(2L, (long)capped.Outputs["inputCount"].Value!);
        Assert.True((bool)capped.Outputs["truncated"].Value!);

        var uncapped = await new MultiBlobNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("measure.blobs", """{"minArea":20,"maxCount":10}"""), CancellationToken.None);
        Assert.Equal(2L, (long)uncapped.Outputs["count"].Value!);
        Assert.Equal(2L, (long)uncapped.Outputs["inputCount"].Value!);
        Assert.False((bool)uncapped.Outputs["truncated"].Value!);
    }

    [Fact]
    public async Task FeatureEllipse_ReportsThePreCapCandidateCount()
    {
        using var image = new Mat(200, 260, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Ellipse(image, new Point(70, 100), new Size(45, 25), 0, 0, 360, Scalar.All(255), 2);
        Cv2.Ellipse(image, new Point(190, 100), new Size(45, 25), 0, 0, 360, Scalar.All(255), 2);

        var capped = await new FeatureEllipseNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("feature.ellipse", """{"minArea":500,"minAspectRatio":0.3,"maxAspectRatio":1,"minCoverageDeg":300,"maxEllipses":1}"""),
            CancellationToken.None);
        Assert.True((bool)capped.Outputs["found"].Value!, (string)capped.Outputs["diagnostics"].Value!);
        Assert.Equal(1L, (long)capped.Outputs["count"].Value!);
        Assert.Equal(2L, (long)capped.Outputs["inputCount"].Value!);
        Assert.True((bool)capped.Outputs["truncated"].Value!);

        var uncapped = await new FeatureEllipseNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("feature.ellipse", """{"minArea":500,"minAspectRatio":0.3,"maxAspectRatio":1,"minCoverageDeg":300,"maxEllipses":10}"""),
            CancellationToken.None);
        Assert.Equal(2L, (long)uncapped.Outputs["count"].Value!);
        Assert.False((bool)uncapped.Outputs["truncated"].Value!);
    }

    [Fact]
    public async Task ContourExtract_ReportsThePreCapCandidateCount()
    {
        using var image = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(image, new Rect(10, 10, 20, 20), Scalar.All(255), -1);
        Cv2.Rectangle(image, new Rect(70, 70, 20, 20), Scalar.All(255), -1);

        var capped = await new ContourExtractNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("contour.extract", """{"mode":"external","binarize":"auto","maxCount":1}"""), CancellationToken.None);
        Assert.Equal(1L, (long)capped.Outputs["count"].Value!);
        Assert.Equal(2L, (long)capped.Outputs["inputCount"].Value!);
        Assert.True((bool)capped.Outputs["truncated"].Value!);

        var uncapped = await new ContourExtractNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(image) }),
            Def("contour.extract", """{"mode":"external","binarize":"auto","maxCount":200}"""), CancellationToken.None);
        Assert.Equal(2L, (long)uncapped.Outputs["count"].Value!);
        Assert.False((bool)uncapped.Outputs["truncated"].Value!);
    }

    [Fact]
    public async Task DetectionSort_ReportsThePreCapItemCount()
    {
        static VisionDetection Item(double x) => new(0, x, 0, 10, 10, 100, 40, 0.8,
            new VisionCircle(x + 5, 5, 5), new VisionRectangle2D(x, 0, 10, 10), 0.9, 0);
        var input = VisionValue.Detections([Item(0), Item(20), Item(40)]);

        var capped = await new DetectionSortNode().ExecuteAsync(
            Ctx(new() { ["detections"] = input }),
            Def("collection.detections.sort", """{"sortBy":"x","topN":2}"""), CancellationToken.None);
        Assert.Equal(2L, (long)capped.Outputs["count"].Value!);
        Assert.Equal(3L, (long)capped.Outputs["inputCount"].Value!);
        Assert.True((bool)capped.Outputs["truncated"].Value!);

        var uncapped = await new DetectionSortNode().ExecuteAsync(
            Ctx(new() { ["detections"] = input }),
            Def("collection.detections.sort", """{"sortBy":"x","topN":0}"""), CancellationToken.None);
        Assert.Equal(3L, (long)uncapped.Outputs["count"].Value!);
        Assert.Equal(3L, (long)uncapped.Outputs["inputCount"].Value!);
        Assert.False((bool)uncapped.Outputs["truncated"].Value!);
    }

    [Fact]
    public void InferenceNmsCap_ReportsTruncationOnlyWhenASurvivorWasDropped()
    {
        // 上限之下：全部保留，不算截断。
        var kept = new List<int>();
        Assert.False(InferenceNmsCap.Fill([3, 2, 1], 3, kept, (_, _) => false, CancellationToken.None));
        Assert.Equal([3, 2, 1], kept);

        // 达到上限后还有存活候选：截断。
        kept.Clear();
        Assert.True(InferenceNmsCap.Fill([3, 2, 1], 2, kept, (_, _) => false, CancellationToken.None));
        Assert.Equal([3, 2], kept);

        // 达到上限后剩余候选全被抑制：没有丢失结果，不算截断。
        kept.Clear();
        Assert.False(InferenceNmsCap.Fill([3, 2, 1], 2, kept, (_, candidate) => candidate < 2, CancellationToken.None));
        Assert.Equal([3, 2], kept);
    }
}
