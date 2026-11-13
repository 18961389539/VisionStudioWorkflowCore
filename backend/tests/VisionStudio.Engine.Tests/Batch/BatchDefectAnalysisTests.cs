using System.Text.Json;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 缺陷检出之后的六个节点（2026-10-28）。断言集中在"分桶是否正确""几何是否量对"
/// "缺口是否被切出来""趋势是否报警"这些会直接影响停机的判断上。
/// </summary>
public sealed class BatchDefectAnalysisTests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);

    /// <summary>按 region.toDetections 的口径构造：X/Y 是包围盒左上角。</summary>
    private static VisionDetection Det(int index, double left, double top, double width, double height,
        double? area = null, double circularity = 0.8, int? classId = null)
    {
        var realArea = area ?? width * height;
        return new VisionDetection(index, left, top, width, height, realArea, 2 * (width + height), circularity,
            new VisionCircle(left + width / 2, top + height / 2, Math.Max(width, height) / 2),
            new VisionRectangle2D(left, top, width, height), null, classId);
    }

    private static VisionValue Dets(params VisionDetection[] items) => VisionValue.Detections(items);

    [Fact]
    public async Task Filter_BucketsEveryRejectionReason()
    {
        // 100x100 画布：正常 / 面积过小 / 细长 / 贴边 / 面积过大
        var detections = new[]
        {
            Det(0, 40, 40, 10, 10, circularity: 0.9),
            Det(1, 50, 50, 1, 1, area: 1),
            Det(2, 20, 70, 60, 2, area: 120, circularity: 0.1),
            Det(3, 0, 90, 6, 6, area: 36),
            Det(4, 10, 10, 60, 60, area: 3600)
        };

        var result = await new DefectFilterNode().ExecuteAsync(
            Ctx(new() { ["detections"] = Dets(detections) }),
            Def("defect.filter", """
                {"minArea":3,"maxArea":1000,"maxAspectRatio":8,"minCircularity":0.3,"edgeMarginPx":5,"canvasWidth":100,"canvasHeight":100}
                """), CancellationToken.None);

        var removedBy = JsonSerializer.Deserialize<Dictionary<string, int>>((string)result.Outputs["removedByJson"].Value!)!;
        Assert.True((long)result.Outputs["count"].Value! == 1L,
            $"count={result.Outputs["count"].Value} removedBy={string.Join(",", removedBy.Select(pair => $"{pair.Key}={pair.Value}"))}");
        Assert.Equal(2, removedBy["area"]);      // 1 px² 太小、3600 px² 太大，各记一次
        Assert.Equal(1, removedBy["aspect"]);
        Assert.Equal(1, removedBy["edge"]);
        Assert.Equal(4L, (long)result.Outputs["removedCount"].Value!);    }

    [Fact]
    public async Task Filter_ContrastGateNeedsImageAndRejectsFlatBoxes()
    {
        using var image = new OpenCvSharp.Mat(100, 100, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(120));
        // 缺陷框内必须同时有背景和亮斑，否则框内灰度是常量、对比度为 0。
        OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(63, 63, 4, 4), OpenCvSharp.Scalar.All(250), -1);

        var result = await new DefectFilterNode().ExecuteAsync(
            Ctx(new()
            {
                ["detections"] = Dets(Det(0, 10, 10, 10, 10), Det(1, 60, 60, 10, 10)),
                ["image"] = VisionValue.Image(VisionStudio.Engine.Camera.VisionImage.Own(image.Clone()))
            }),
            Def("defect.filter", """{"minContrast":30,"canvasWidth":100,"canvasHeight":100}"""),
            CancellationToken.None);

        var removedBy = JsonSerializer.Deserialize<Dictionary<string, int>>((string)result.Outputs["removedByJson"].Value!)!;
        Assert.True((long)result.Outputs["count"].Value! == 1L,
            $"count={result.Outputs["count"].Value} removedBy={string.Join(",", removedBy.Select(pair => $"{pair.Key}={pair.Value}"))}");
        Assert.Equal(1, removedBy["contrast"]);
    }

    [Fact]
    public async Task Geometry_MeasuresLengthDirectionAndElongation()
    {
        var detections = new[]
        {
            Det(0, 10, 40, 100, 3, area: 300),   // 细长划痕
            Det(1, 60, 60, 10, 10, area: 78)     // 圆形脏污
        };

        var result = await new DefectGeometryNode().ExecuteAsync(
            Ctx(new() { ["detections"] = Dets(detections) }),
            Def("defect.geometry", """{"elongationThreshold":3}"""), CancellationToken.None);

        Assert.Equal(2L, (long)result.Outputs["count"].Value!);
        Assert.Equal(100d, (double)result.Outputs["maxLength"].Value!, 1);
        Assert.Equal(1L, (long)result.Outputs["elongatedCount"].Value!);
        Assert.Equal(378d, (double)result.Outputs["totalArea"].Value!, 1);
        var json = (string)result.Outputs["geometryJson"].Value!;
        Assert.Contains("\"source\":\"bbox\"", json);
    }

    [Fact]
    public async Task Geometry_UsesContoursForRotatedMinimumAreaRectangle()
    {
        var detections = new[] { Det(0, 10, 40, 100, 3, area: 300) };
        // 沿 45 度方向铺一条线，最小外接矩形的长度应约等于点数 * 步距。
        var points = Enumerable.Range(0, 100).Select(i => new VisionPoint2D(10 + i, 10 + i)).ToArray();
        var contour = new VisionContour(0, points, -1, false, 0, 0, 0,
            new VisionRectangle2D(10, 10, 100, 100), new VisionCircle(60, 60, 70)) { Closed = false };

        var result = await new DefectGeometryNode().ExecuteAsync(
            Ctx(new() { ["detections"] = Dets(detections), ["contours"] = VisionValue.Contours([contour]) }),
            Def("defect.geometry", """{"elongationThreshold":3}"""), CancellationToken.None);

        var json = (string)result.Outputs["geometryJson"].Value!;
        Assert.Contains("\"source\":\"contour\"", json);
        Assert.InRange((double)result.Outputs["maxLength"].Value!, 130, 145);
        Assert.InRange((double)result.Outputs["dominantAngleDeg"].Value!, 44, 46);
    }

    [Fact]
    public async Task EdgeDefect_SplitsChipsFromOneProfile()
    {
        // 水平名义线 y=50；在 x=50..59 与 x=120..124 各有一个向 +y 的缺口。
        var samples = new List<VisionPoint2D>();
        for (var x = 0; x < 200; x++)
        {
            var offset = x is >= 50 and < 60 ? 2.0 : x is >= 120 and < 125 ? 1.6 : 0.1;
            samples.Add(new VisionPoint2D(x, 50 + offset));
        }

        var result = await new EdgeDefectNode().ExecuteAsync(
            Ctx(new()
            {
                ["samples"] = VisionValue.PointList(samples.ToArray()),
                ["nominalLine"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 50), new VisionPoint2D(199, 50)))
            }),
            Def("defect.edgeDefect", """{"maxDeviation":1,"polarity":"both","minSegmentLength":2,"mergeGapPx":1}"""),
            CancellationToken.None);

        Assert.Equal(2L, (long)result.Outputs["count"].Value!);
        Assert.InRange((double)result.Outputs["maxDepth"].Value!, 1.5, 2.1);
        var detections = (IReadOnlyList<VisionDetection>)result.Outputs["detections"].Value!;
        Assert.All(detections, detection => Assert.True(detection.Area > 0));
    }

    [Fact]
    public async Task EdgeDefect_PolarityShortOnlySeesInwardChips()
    {
        var samples = new List<VisionPoint2D>();
        for (var x = 0; x < 100; x++)
            samples.Add(new VisionPoint2D(x, 50 + (x is >= 40 and < 46 ? -2.0 : 0.05)));

        var shortOnly = await new EdgeDefectNode().ExecuteAsync(
            Ctx(new()
            {
                ["samples"] = VisionValue.PointList(samples.ToArray()),
                ["nominalLine"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 50), new VisionPoint2D(99, 50)))
            }),
            Def("defect.edgeDefect", """{"maxDeviation":1,"polarity":"short","minSegmentLength":2}"""),
            CancellationToken.None);
        Assert.Equal(1L, (long)shortOnly.Outputs["count"].Value!);

        var excessOnly = await new EdgeDefectNode().ExecuteAsync(
            Ctx(new()
            {
                ["samples"] = VisionValue.PointList(samples.ToArray()),
                ["nominalLine"] = VisionValue.Line(new VisionLine2D(new VisionPoint2D(0, 50), new VisionPoint2D(99, 50)))
            }),
            Def("defect.edgeDefect", """{"maxDeviation":1,"polarity":"excess","minSegmentLength":2}"""),
            CancellationToken.None);
        Assert.Equal(0L, (long)excessOnly.Outputs["count"].Value!);
    }

    [Fact]
    public async Task Heatmap_ConcentratesClusteredDefects()
    {
        var spread = new[]
        {
            Det(0, 10, 10, 5, 5), Det(1, 20, 12, 5, 5), Det(2, 12, 25, 5, 5), Det(3, 250, 250, 5, 5)
        };

        var result = await new DefectHeatmapNode().ExecuteAsync(
            Ctx(new() { ["detections"] = Dets(spread) }),
            Def("defect.heatmap", """{"gridCols":8,"gridRows":8,"canvasWidth":300,"canvasHeight":300,"cellPixels":8}"""),
            CancellationToken.None);

        Assert.Equal(4L, (long)result.Outputs["count"].Value!);
        Assert.True((double)result.Outputs["concentration"].Value! > 0.7, "3 of 4 defects share the top-left cells");
        Assert.True((long)result.Outputs["occupiedCells"].Value! <= 3);
        Assert.True((long)result.Outputs["hotspotCount"].Value! >= 2);
        var image = (VisionStudio.Engine.Camera.VisionImage)result.Outputs["image"].Value!;
        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
    }

    [Fact]
    public async Task Trend_FlagsOutOfControlSampleAndSlope()
    {
        // 20 个稳定样本后突然 9 个缺陷：基线足够长时 3σ 限才不会被异常点自己撑大。
        var series = VisionValue.NumberList(Enumerable.Repeat(1.0, 20).Append(9.0).ToArray());
        var result = await new DefectTrendNode().ExecuteAsync(
            Ctx(new() { ["series"] = series }),
            Def("defect.trend", """{"window":5,"sigmaK":3,"minSamples":5,"baselineSamples":20}"""), CancellationToken.None);

        Assert.True((bool)result.Outputs["outOfControl"].Value!, (string)result.Outputs["diagnostics"].Value!);
        Assert.Equal(9d, (double)result.Outputs["last"].Value!, 6);
        Assert.Equal("rising", result.Outputs["direction"].Value);
        Assert.True((double)result.Outputs["slope"].Value! > 0);
        Assert.InRange((double)result.Outputs["ucl"].Value!, 1, 4);

        var tooShort = await new DefectTrendNode().ExecuteAsync(
            Ctx(new() { ["series"] = VisionValue.NumberList([1, 2]) }),
            Def("defect.trend", """{"minSamples":5}"""), CancellationToken.None);
        Assert.False((bool)tooShort.Outputs["found"].Value!);
    }

    [Fact]
    public async Task Dataset_ExportsCropsAndRejectsEscapingPaths()
    {
        using var image = new OpenCvSharp.Mat(120, 160, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(30));
        OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(20, 20, 30, 20), OpenCvSharp.Scalar.All(220), -1);
        OpenCvSharp.Cv2.Rectangle(image, new OpenCvSharp.Rect(100, 80, 20, 20), OpenCvSharp.Scalar.All(200), -1);

        var directory = $"artifacts/test-defect-samples-{Guid.NewGuid():N}";
        var result = await new DefectDatasetNode().ExecuteAsync(
            Ctx(new()
            {
                ["detections"] = Dets(Det(0, 20, 20, 30, 20), Det(1, 100, 80, 20, 20)),
                ["image"] = VisionValue.Image(VisionStudio.Engine.Camera.VisionImage.Own(image.Clone()))
            }),
            Def("defect.dataset", JsonSerializer.Serialize(new { dataset = "unit", directory, maxSamples = 5, cropMarginPx = 2 })),
            CancellationToken.None);

        Assert.Equal(2L, (long)result.Outputs["savedCount"].Value!);
        var root = (string)result.Outputs["directory"].Value!;
        Assert.True(System.IO.Directory.Exists(root));
        foreach (var file in System.IO.Directory.GetFiles(root)) System.IO.File.Delete(file);
        System.IO.Directory.Delete(root);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new DefectDatasetNode().ExecuteAsync(
                Ctx(new()
                {
                    ["detections"] = Dets(Det(0, 20, 20, 30, 20)),
                    ["image"] = VisionValue.Image(VisionStudio.Engine.Camera.VisionImage.Own(image.Clone()))
                }),
                Def("defect.dataset", """{"directory":"C:/Windows/Temp"}"""), CancellationToken.None));
    }
}
