using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 批次甲回归：多斑点分析的可取消性与面积口径。
///   · 面积改用填充前连通域的真实像素数（多边形面积会把小目标算小、1–3 px 甚至退化成 0）；
///   · 孔洞不计入面积，而圆度/外接框仍来自填孔后的外轮廓；
///   · 轮廓循环每轮检查取消，上千连通域时也能中断。
/// </summary>
public sealed class BlobAnalysisTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Fact]
    public async Task SmallBlob_AreaIsPixelCountSoMinAreaDoesNotDropIt()
    {
        // 4×4 = 16 像素。旧实现用多边形面积（9），minArea=16 会把整块目标滤掉。
        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(10, 10, 4, 4), Scalar.All(255), -1);

        var result = await new MultiBlobNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("measure.blobs", """{"mode":"all","minArea":16,"maxArea":0,"maxCount":20}"""), CancellationToken.None);

        Assert.Equal(1L, Convert.ToInt64(result.Outputs["count"].Value));
        Assert.Equal(16d, Convert.ToDouble(result.Outputs["area"].Value), 9);
        Assert.True(result.Outputs["found"].Require<bool>("found"));
    }

    [Fact]
    public async Task RingBlob_AreaExcludesTheHoleButContourStaysFilled()
    {
        // 20×20 实心块挖 10×10 孔：像素面积 400-100=300，外轮廓仍按填孔后的方形计算。
        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(10, 10, 20, 20), Scalar.All(255), -1);
        Cv2.Rectangle(img, new Rect(15, 15, 10, 10), Scalar.All(0), -1);

        var result = await new MultiBlobNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("measure.blobs", """{"mode":"all","minArea":1,"maxCount":20}"""), CancellationToken.None);

        Assert.Equal(300d, Convert.ToDouble(result.Outputs["area"].Value), 9);
        Assert.Equal(20d, Convert.ToDouble(result.Outputs["width"].Value), 9);
        Assert.Equal(20d, Convert.ToDouble(result.Outputs["height"].Value), 9);
        var blobs = result.Outputs["blobs"].Require<string>("blobs");
        Assert.Contains("\"area\":300", blobs);
    }

    [Fact]
    public async Task Analysis_HonorsCancellation()
    {
        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(10, 10, 20, 20), Scalar.All(255), -1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new MultiBlobNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("measure.blobs", """{"mode":"all","minArea":1}"""), cancelled.Token));
    }

    [Fact]
    public async Task LargestMode_HonorsFiltersInsteadOfIgnoringThem()
    {
        // 细长条 80×10=800 px（圆度约 0.31）比半径 12 的圆（约 450 px）大，
        // 但设了 minCircularity=0.6 后 largest 必须给出圆——旧实现会把筛选全部置 0、直接返回长条。
        using var img = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(img, new Rect(10, 10, 80, 10), Scalar.All(255), -1);
        Cv2.Circle(img, new Point(150, 150), 12, Scalar.All(255), -1);

        var filtered = await new MultiBlobNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("measure.blobs", """{"mode":"largest","minArea":10,"minCircularity":0.6}"""), CancellationToken.None);
        var unfiltered = await new MultiBlobNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("measure.blobs", """{"mode":"largest","minArea":10}"""), CancellationToken.None);

        Assert.Equal(1L, Convert.ToInt64(filtered.Outputs["count"].Value));
        Assert.InRange(Convert.ToDouble(filtered.Outputs["area"].Value), 380, 520);
        Assert.True(Convert.ToDouble(filtered.Outputs["circularity"].Value) >= 0.6);
        // 不加圆度筛选时，largest 回到面积最大的细长条。
        Assert.InRange(Convert.ToDouble(unfiltered.Outputs["area"].Value), 780, 820);
    }
}
