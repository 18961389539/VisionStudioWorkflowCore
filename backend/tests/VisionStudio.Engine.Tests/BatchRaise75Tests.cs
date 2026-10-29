using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>Behavioral coverage for diagnostics and robustness gates across measurement and imaging nodes.</summary>
public sealed class BatchRaise75Tests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));

    [Fact]
    public void Catalog_Score75Band_ParametersDescribed()
    {
        string[] types =
        [
            "camera.syncCapture", "contour.filter", "coordinate.transformCoordinatePoint", "coordinate.transformInverse",
            "device.readTag", "device.writeTag", "flow.delay", "flow.loopEnd", "flow.merge",
            "image.flatFieldCorrect", "inference.detection", "match.shape",
            "measure.contourThickness", "measure.regionIntensity", "output.exportResult",
            "robot.currentPose", "robot.j4TcpCompensation", "text.validate"
        ];
        foreach (var type in types)
        {
            var item = BuiltInNodeCatalog.Items.Single(x => x.Type == type);
            Assert.True(item.Parameters.All(p => !string.IsNullOrWhiteSpace(p.Description)),
                $"{type} params missing description: {string.Join(",", item.Parameters.Where(p => string.IsNullOrWhiteSpace(p.Description)).Select(p => p.Name))}");
            Assert.Contains(item.Outputs, o => o.Name == "diagnostics");
        }
    }

    [Fact]
    public async Task GaussianBlur_EmitsIntensityPorts()
    {
        using var img = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(img, new Rect(10, 10, 18, 18), Scalar.All(210), -1);
        var result = await new GaussianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.blur.gaussian", """{"kernelSize":5,"sigmaX":0,"sigmaY":0}"""), CancellationToken.None);
        Assert.True(result.Outputs["maxIntensity"].Require<double>("maxIntensity") >= result.Outputs["meanIntensity"].Require<double>("meanIntensity"));
        Assert.Contains("image.blur.gaussian", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new GaussianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.blur.gaussian", """{"kernelSize":5,"sigmaX":-1}"""), CancellationToken.None));
    }

    [Fact]
    public async Task ContourFilter_DroppedAndTruncationPorts()
    {
        var contours = Enumerable.Range(0, 5).Select(i =>
            new VisionContour(i, [new(0, 0), new(10, 0), new(10, 10), new(0, 10)], -1, false,
                10 + i * 20, 40, 0.7, new VisionRectangle2D(i * 20, 0, 10, 10), new VisionCircle(i * 20 + 5, 5, 7))).ToArray();
        var result = await new ContourFilterNode().ExecuteAsync(
            Ctx(new() { ["contours"] = VisionValue.Contours(contours) }),
            Def("contour.filter", """{"minArea":30,"maxCount":2}"""), CancellationToken.None);
        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(5L, result.Outputs["inputCount"].Require<long>("inputCount"));
        Assert.True(result.Outputs["droppedCount"].Require<long>("droppedCount") > 0);
        Assert.True(result.Outputs["truncated"].Require<bool>("truncated"));
        Assert.Contains("contour.filter", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ContourFilterNode().ExecuteAsync(Ctx(new() { ["contours"] = VisionValue.Contours(contours) }),
                Def("contour.filter", """{"minCircularity":0.9,"maxCircularity":0.1}"""), CancellationToken.None));
    }

    [Fact]
    public async Task FlowMarkers_DelayMergeLoopEnd()
    {
        var delay = await new DelayNode().ExecuteAsync(Ctx(new()), Def("flow.delay", """{"delayMs":1}"""), CancellationToken.None);
        Assert.Equal(1L, delay.Outputs["requestedMs"].Require<long>("requestedMs"));
        Assert.True(delay.Outputs["elapsedMs"].Require<double>("elapsedMs") >= 0);
        Assert.Contains("flow.delay", delay.Outputs["diagnostics"].Require<string>("diagnostics"));

        var merge = await new FlowMergeNode().ExecuteAsync(Ctx(new()), new NodeDefinition("merge-1", "flow.merge", null, null, null), CancellationToken.None);
        Assert.True(merge.Outputs["merged"].Require<bool>("merged"));
        Assert.Equal("merge-1", merge.Outputs["nodeId"].Require<string>("nodeId"));

        var loopEnd = await new LoopEndNode().ExecuteAsync(Ctx(new()), new NodeDefinition("loop-end-1", "flow.loopEnd", null, null, null), CancellationToken.None);
        Assert.Equal("loopEnd", loopEnd.Outputs["boundary"].Require<string>("boundary"));
        Assert.Equal("loopEnd", loopEnd.Outputs["marker"].Require<string>("marker"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new LoopEndNode().ExecuteAsync(Ctx(new()), new NodeDefinition("  ", "flow.loopEnd", null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task TransformCoordinatePointAndInverse_Depth()
    {
        var transform = VisionTransform2DMath.Rigid("A", "B", "mm", 10, 20, 0);
        var point = new VisionCoordinatePoint2D(1, 2, "A", "mm");
        var xf = await new TransformCoordinatePointNode().ExecuteAsync(
            Ctx(new() { ["point"] = VisionValue.CoordinatePoint(point), ["transform"] = VisionValue.Transform(transform) }),
            Def("coordinate.transformCoordinatePoint"), CancellationToken.None);
        Assert.Equal("A", xf.Outputs["sourceFrame"].Require<string>("sourceFrame"));
        Assert.True(xf.Outputs["determinant"].Require<double>("determinant") > 0.5);
        Assert.Contains("coordinate.transformCoordinatePoint", xf.Outputs["diagnostics"].Require<string>("diagnostics"));

        var inv = await new InverseTransformNode().ExecuteAsync(
            Ctx(new() { ["transform"] = VisionValue.Transform(transform) }),
            Def("coordinate.transformInverse"), CancellationToken.None);
        Assert.True(inv.Outputs.ContainsKey("inputDeterminant"));
        Assert.Contains("coordinate.transformInverse", inv.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task ContourThickness_EmitsDiagnostics()
    {
        static VisionPoint2D[] Rect(double x0, double y0, double x1, double y1) =>
            [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];
        var outerPts = Rect(0, 0, 40, 40);
        var innerPts = Rect(10, 10, 30, 30);
        var outer = new VisionContour(0, outerPts, -1, false, 1600, 160, 0.7, new VisionRectangle2D(0, 0, 40, 40), new VisionCircle(20, 20, 20)) { Closed = true };
        var inner = new VisionContour(0, innerPts, -1, true, 400, 80, 0.7, new VisionRectangle2D(10, 10, 20, 20), new VisionCircle(20, 20, 10)) { Closed = true };
        var result = await new ContourThicknessNode().ExecuteAsync(
            Ctx(new()
            {
                ["innerContours"] = VisionValue.Contours([inner]),
                ["outerContours"] = VisionValue.Contours([outer])
            }),
            Def("measure.contourThickness", """{"measurementMode":"shortest","sampleStep":2}"""), CancellationToken.None);
        Assert.Contains("measure.contourThickness", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task ExportResult_FormatAndValueTypePorts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vs-r75-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "out.json");
        try
        {
            var result = await new ExportResultNode().ExecuteAsync(
                Ctx(new() { ["value"] = VisionValue.String("ok") }),
                Def("output.exportResult", JsonSerializer.Serialize(new { path, format = "json", overwrite = true, maxBytes = 4096 })),
                CancellationToken.None);
            Assert.Equal("json", result.Outputs["format"].Require<string>("format"));
            Assert.Equal("String", result.Outputs["valueType"].Require<string>("valueType"));
            Assert.True(result.Outputs["bytesWritten"].Require<long>("bytesWritten") > 0);
            Assert.Contains("output.exportResult", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task TextMatch_ModeAndMatchLength()
    {
        var result = await new TextMatchNode().ExecuteAsync(Ctx(new()),
            Def("text.match", """{"text":"ABC","expected":"AB","mode":"startsWith","allowEmpty":true}"""), CancellationToken.None);
        Assert.True(result.Outputs["pass"].Require<bool>("pass"));
        Assert.Equal("startsWith", result.Outputs["mode"].Require<string>("mode"));
        Assert.Equal(3L, result.Outputs["matchLength"].Require<long>("matchLength"));
    }

    [Fact]
    public async Task J4TcpCompensation_MaxEccentricityGate()
    {
        var target = new VisionRobotTarget2D(100, 200, 0, "RobotBase", "mm", "ABB", "EyeToHand");
        var ok = await new J4TcpCompensationNode().ExecuteAsync(
            Ctx(new() { ["target"] = VisionValue.RobotTarget(target) }),
            Def("robot.j4TcpCompensation", """{"tcpOffsetX":1,"tcpOffsetY":0,"j4PivotOffsetX":0,"j4PivotOffsetY":0,"maxEccentricity":5}"""),
            CancellationToken.None);
        Assert.True(ok.Outputs["eccentricity"].Require<double>("eccentricity") > 0);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new J4TcpCompensationNode().ExecuteAsync(
                Ctx(new() { ["target"] = VisionValue.RobotTarget(target) }),
                Def("robot.j4TcpCompensation", """{"tcpOffsetX":10,"tcpOffsetY":0,"maxEccentricity":1}"""),
                CancellationToken.None));
    }

    [Fact]
    public async Task RegionIntensity_MaxCvGate()
    {
        using var img = new Mat(32, 32, MatType.CV_8UC1, Scalar.All(100));
        img.Set(0, 0, (byte)255);
        img.Set(0, 1, (byte)0);
        var region = new VisionRegion(32, 32, [new VisionRegionComponent([new VisionRegionRun(0, 0, 31), new VisionRegionRun(1, 0, 31)])]);
        var result = await new RegionIntensityNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img), ["region"] = VisionValue.Region(region) }),
            Def("measure.regionIntensity", """{"maxCv":0.0001}"""), CancellationToken.None);
        Assert.False(result.Outputs["withinTolerance"].Require<bool>("withinTolerance"));
        Assert.Contains("measure.regionIntensity", result.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public void MatchShape_CatalogHasDiagnostics()
    {
        var item = BuiltInNodeCatalog.Items.Single(x => x.Type == "match.shape");
        Assert.Contains(item.Outputs, o => o.Name == "diagnostics");
        Assert.True(item.Parameters.All(p => !string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void InferenceDetection_CatalogHasDiagnosticsAndPdesc()
    {
        var item = BuiltInNodeCatalog.Items.Single(x => x.Type == "inference.detection");
        Assert.Contains(item.Outputs, o => o.Name == "diagnostics");
        Assert.Contains(item.Outputs, o => o.Name == "maxScore");
        Assert.True(item.Parameters.All(p => !string.IsNullOrWhiteSpace(p.Description)));
    }
}
