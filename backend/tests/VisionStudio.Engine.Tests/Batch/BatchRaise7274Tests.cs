using System.Net;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Device;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Robot;

namespace VisionStudio.Engine.Tests;

/// <summary>Behavioral coverage for F-port contracts, diagnostics gates and catalog descriptions.</summary>
public sealed class BatchRaise7274Tests
{
    private static NodeDefinition Def(string type, string json = "{}")
        => new("test", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
    private static NodeExecutionContext Ctx(Dictionary<string, VisionValue> inputs) => new(inputs);
    private static VisionValue Img(Mat m) => VisionValue.Image(VisionImage.Own(m.Clone()));
    private static VisionRegion Rect(int x, int y, int w, int h, int canvas = 64)
        => new(canvas, canvas, [new VisionRegionComponent(Enumerable.Range(y, h).Select(row => new VisionRegionRun(row, x, x + w - 1)).ToArray())]);

    [Fact]
    public void Catalog_7274Band_ParametersDescribed()
    {
        string[] types =
        [
            "collection.detections.at", "collection.detections.filter", "contour.properties", "coordinate.point",
            "device.writeTag", "device.writeVisionResult", "device.readTag", "image.morphology",
            "image.perspective", "image.warpAffine", "image.filter", "image.synthetic", "image.flatFieldCorrect",
            "image.register", "image.undistort", "measure.colorDifference", "measure.fitEllipse", "measure.lineSpacing",
            "measure.regionIntensity", "measure.blobs", "region.fromRange", "region.properties", "region.transform",
            "robot.j4TcpCompensation", "robot.currentPose", "robot.guidance2d", "text.validate", "image.channel",
            "camera.syncCapture", "match.template", "feature.edge", "flow.merge", "output.httpRequest", "output.saveImage",
            "calibration.handEye3D"
        ];
        foreach (var type in types)
        {
            var item = BuiltInNodeCatalog.Items.Single(x => x.Type == type);
            Assert.True(item.Parameters.All(p => !string.IsNullOrWhiteSpace(p.Description)),
                $"{type} params missing description: {string.Join(",", item.Parameters.Where(p => string.IsNullOrWhiteSpace(p.Description)).Select(p => p.Name))}");
        }
    }

    [Fact]
    public async Task PreprocessStats_MedianMorphologyPerspectiveFilter()
    {
        using var img = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(img, new Rect(10, 10, 18, 18), Scalar.All(210), -1);
        img.Set(5, 5, (byte)255);

        var median = await new MedianBlurNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.blur.median", """{"kernelSize":3}"""), CancellationToken.None);
        Assert.True(median.Outputs["maxIntensity"].Require<double>("maxIntensity") >= median.Outputs["meanIntensity"].Require<double>("meanIntensity"));
        Assert.Contains("image.blur.median", median.Outputs["diagnostics"].Require<string>("diagnostics"));

        var morph = await new MorphologyNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.morphology", """{"operation":"open","kernelSize":3,"iterations":1,"shape":"rect"}"""), CancellationToken.None);
        Assert.True(morph.Outputs["nonZeroCount"].Require<long>("nonZeroCount") > 0);
        Assert.Contains("image.morphology", morph.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MorphologyNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.morphology", """{"operation":"bogus"}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new MorphologyNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("image.morphology", """{"operation":"open","shape":"bogus"}"""), CancellationToken.None));
        var topHat = await new MorphologyNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.morphology", """{"operation":"whiteTopHat","kernelWidth":15,"kernelHeight":15}"""), CancellationToken.None);
        Assert.True(topHat.Outputs.ContainsKey("meanIntensity"));

        var persp = await new PerspectiveCorrectionNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.perspective", """{"x1":2,"y1":2,"x2":44,"y2":4,"x3":45,"y3":45,"x4":3,"y4":44,"width":32,"height":32}"""), CancellationToken.None);
        Assert.True(persp.Outputs.ContainsKey("meanIntensity"));
        Assert.Contains("image.perspective", persp.Outputs["diagnostics"].Require<string>("diagnostics"));

        var filter = await new ImageFilterNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("image.filter", """{"method":"gaussian","kernelSize":3}"""), CancellationToken.None);
        Assert.True(filter.Outputs.ContainsKey("meanIntensity"));
        Assert.Contains("image.filter", filter.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task SyntheticAndUndistort_Emissions()
    {
        var synth = await new SyntheticImageNode().ExecuteAsync(Ctx(new()),
            Def("image.synthetic", """{"width":320,"height":240,"centerX":160,"centerY":120,"radius":40,"placementMode":"fixed"}"""), CancellationToken.None);
        Assert.Equal(320L, synth.Outputs["width"].Require<long>("width"));
        Assert.Equal(240L, synth.Outputs["height"].Require<long>("height"));
        Assert.Contains("image.synthetic", synth.Outputs["diagnostics"].Require<string>("diagnostics"));

        var calib = """{"imageWidth":64,"imageHeight":48,"cameraMatrix":[[600,0,32],[0,600,24],[0,0,1]],"distCoeffs":[0.01,-0.005,0,0,0],"rms":0.1}""";
        using var small = new Mat(48, 64, MatType.CV_8UC1, Scalar.All(120));
        var undist = await new ImageUndistortNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(small), ["calibrationJson"] = VisionValue.String(calib) }),
            Def("image.undistort", """{"calibrationJson":"{}"}"""), CancellationToken.None);
        Assert.Contains("image.undistort", undist.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(undist.Outputs.ContainsKey("meanIntensity"));
    }

    [Fact]
    public async Task ColorDifferenceAndEdge_Diagnostics()
    {
        using var img = new Mat(32, 32, MatType.CV_8UC3, new Scalar(30, 60, 200));
        var diff = await new ColorDifferenceNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("measure.colorDifference", """{"referenceRed":0,"referenceGreen":0,"referenceBlue":0}"""), CancellationToken.None);
        Assert.Contains("measure.colorDifference", diff.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(diff.Outputs["deltaE"].Require<double>("deltaE") > 0);

        using var gray = new Mat(48, 48, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(gray, new Rect(10, 10, 20, 20), Scalar.All(255), -1);
        var edge = await new EdgeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(gray) }),
            Def("feature.edge", """{"lowThreshold":50,"highThreshold":150}"""), CancellationToken.None);
        Assert.True(edge.Outputs["found"].Require<bool>("found"));
        Assert.Contains("feature.edge", edge.Outputs["diagnostics"].Require<string>("diagnostics"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new EdgeNode().ExecuteAsync(Ctx(new() { ["image"] = Img(gray) }),
                Def("feature.edge", """{"lowThreshold":200,"highThreshold":100}"""), CancellationToken.None));
    }

    [Fact]
    public async Task TemplateAndRegister_Diagnostics()
    {
        using var scene = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(scene, new Rect(30, 25, 18, 14), Scalar.All(220), -1);
        using var template = new Mat(scene, new Rect(28, 23, 22, 18)).Clone();
        var match = await new TemplateMatchNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(scene), ["template"] = Img(template) }),
            Def("match.template", """{"minScore":0.5}"""), CancellationToken.None);
        Assert.True(match.Outputs["found"].Require<bool>("found"));
        Assert.Contains("match.template", match.Outputs["diagnostics"].Require<string>("diagnostics"));

        using var reference = VisionImage.Own(new Mat(180, 240, MatType.CV_8UC1, Scalar.All(25)));
        var rng = new Random(7);
        for (var i = 0; i < 35; i++) Cv2.Circle(reference.Mat, new Point(rng.Next(65, 160), rng.Next(55, 120)), rng.Next(2, 7), Scalar.All(rng.Next(50, 255)), -1);
        using var m = Cv2.GetRotationMatrix2D(new Point2f(0, 0), 0, 1);
        m.Set(0, 2, 5d);
        using var shifted = VisionImage.Own(new Mat());
        Cv2.WarpAffine(reference.Mat, shifted.Mat, m, reference.Mat.Size());
        var reg = await new ImageRegisterNode().ExecuteAsync(
            Ctx(new() { ["image"] = VisionValue.Image(shifted), ["reference"] = VisionValue.Image(reference.CloneOwned()) }),
            Def("image.register", """{"x":50,"y":40,"width":130,"height":100,"angleRangeDeg":5,"minScore":0.5}"""), CancellationToken.None);
        Assert.True(reg.Outputs["found"].Require<bool>("found"));
        Assert.Contains("image.register", reg.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task FlowMergeAndFlatFieldAndSaveHttp()
    {
        var merge = await new FlowMergeNode().ExecuteAsync(Ctx(new()), Def("flow.merge"), CancellationToken.None);
        Assert.True(merge.Outputs["merged"].Require<bool>("merged"));
        Assert.Contains("flow.merge", merge.Outputs["diagnostics"].Require<string>("diagnostics"));

        using var img = new Mat(24, 24, MatType.CV_8UC1, Scalar.All(100));
        using var dark = new Mat(24, 24, MatType.CV_8UC1, Scalar.All(10));
        using var bright = new Mat(24, 24, MatType.CV_8UC1, Scalar.All(210));
        var flat = await new FlatFieldCorrectNode().ExecuteAsync(
            Ctx(new() { ["image"] = Img(img), ["dark"] = Img(dark), ["bright"] = Img(bright) }),
            Def("image.flatFieldCorrect", """{"outputType":"byte"}"""), CancellationToken.None);
        Assert.True(flat.Outputs["correctedMax"].Require<double>("correctedMax") >= flat.Outputs["correctedMin"].Require<double>("correctedMin"));

        var dir = Path.Combine(Path.GetTempPath(), "raise7274-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "frame.png");
            var saved = await new SaveImageNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
                Def("output.saveImage", JsonSerializer.Serialize(new { path, overwrite = true })), CancellationToken.None);
            Assert.Equal(".png", saved.Outputs["format"].Require<string>("format"));
            Assert.Equal(24L, saved.Outputs["imageWidth"].Require<long>("imageWidth"));
        }
        finally { Directory.Delete(dir, true); }

        var http = new HttpRequestNode(new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("hello") }))));
        var resp = await http.ExecuteAsync(Ctx(new()), Def("output.httpRequest", """{"url":"https://localhost/"}"""), CancellationToken.None);
        Assert.Equal(5L, resp.Outputs["responseLength"].Require<long>("responseLength"));
        Assert.True(resp.Outputs["elapsedMs"].Require<double>("elapsedMs") >= 0);
    }

    [Fact]
    public async Task TextRegexAndMatch_NewPorts()
    {
        var regex = await new TextRegexNode().ExecuteAsync(Ctx(new() { ["text"] = VisionValue.String("AB-12 CD-34") }),
            Def("text.regex", """{"pattern":"[A-Z]+-\\d+","fullMatch":false}"""), CancellationToken.None);
        Assert.True(regex.Outputs["pass"].Require<bool>("pass"));
        Assert.Equal(5L, regex.Outputs["matchedLength"].Require<long>("matchedLength"));
        Assert.Equal(0L, regex.Outputs["matchIndex"].Require<long>("matchIndex"));
        Assert.Equal(2L, regex.Outputs["matchCount"].Require<long>("matchCount"));

        var gated = await new TextRegexNode().ExecuteAsync(Ctx(new() { ["text"] = VisionValue.String("AB-1") }),
            Def("text.regex", """{"pattern":"[A-Z]+-\\d+","fullMatch":false,"minMatchedLength":10}"""), CancellationToken.None);
        Assert.False(gated.Outputs["pass"].Require<bool>("pass"));

        var match = await new TextMatchNode().ExecuteAsync(Ctx(new() { ["text"] = VisionValue.String("OK-1") }),
            Def("text.match", """{"expected":"OK-1","mode":"equals"}"""), CancellationToken.None);
        Assert.True(match.Outputs["pass"].Require<bool>("pass"));
    }

    [Fact]
    public async Task HandEye3D_DiagnosticsAndGates()
    {
        static double[] Rigid(double ax, double ay, double az, double tx, double ty, double tz)
        {
            using var rv = new Mat(3, 1, MatType.CV_64F);
            rv.Set(0, 0, ax); rv.Set(1, 0, ay); rv.Set(2, 0, az);
            using var r = new Mat();
            Cv2.Rodrigues(rv, r);
            var m = new double[16];
            for (var row = 0; row < 3; row++) for (var col = 0; col < 3; col++) m[row * 4 + col] = r.At<double>(row, col);
            m[3] = tx; m[7] = ty; m[11] = tz; m[15] = 1;
            return m;
        }
        var samples = new List<object>();
        for (var i = 0; i < 8; i++)
        {
            samples.Add(new
            {
                robotToBase = Rigid(0.15 * i, 0.1 * (i % 3), 0.05 * i, 10 * i, 5 * i, 2 * i),
                targetToCamera = Rigid(-0.1 * i, 0.12 * (i % 4), -0.04 * i, 100 + 3 * i, 50 + i, 200)
            });
        }
        var json = JsonSerializer.Serialize(new { samples });
        var result = await new HandEye3DCalibrationNode().ExecuteAsync(Ctx(new()),
            Def("calibration.handEye3D", JsonSerializer.Serialize(new { samplesJson = json, mode = "eyeInHand", method = "DANIILIDIS" })), CancellationToken.None);
        Assert.Contains("calibration.handEye3D", result.Outputs["diagnostics"].Require<string>("diagnostics"));
        Assert.True(result.Outputs.ContainsKey("transform"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new HandEye3DCalibrationNode().ExecuteAsync(Ctx(new()),
                Def("calibration.handEye3D", """{"samplesJson":"{\"samples\":[]}"}"""), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new HandEye3DCalibrationNode().ExecuteAsync(Ctx(new()),
                Def("calibration.handEye3D", JsonSerializer.Serialize(new { samplesJson = json, mode = "bogus" })), CancellationToken.None));
    }

    [Fact]
    public async Task SyncCapture_CamerasSkewAndMaxSkewGate()
    {
        using var image = VisionImage.Own(new Mat(4, 4, MatType.CV_8UC1));
        using var set = new VisionFrameSet("sync-1", "device-ptp", 8200, true,
            new Dictionary<string, IVisionImage> { ["a"] = image },
            new Dictionary<string, VisionFrameSetFrameInfo> { ["a"] = new("a", 7, DateTimeOffset.UtcNow, 100, 9, "Mono8") });
        var node = new SynchronizedCaptureNode(new FakeSync(set));
        var ok = await node.ExecuteAsync(Ctx(new()), Def("camera.syncCapture", """{"groupId":"sync-1","maxSkewUs":10000}"""), CancellationToken.None);
        Assert.Equal("a", ok.Outputs["cameras"].Require<string>("cameras"));
        Assert.Equal(8.2, ok.Outputs["skewMs"].Require<double>("skewMs"), 6);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await node.ExecuteAsync(Ctx(new()), Def("camera.syncCapture", """{"groupId":"sync-1","maxSkewUs":100}"""), CancellationToken.None));
    }

    [Fact]
    public async Task DeviceRobot_NewGatesAndPorts()
    {
        await using var manager = new DeviceManager();
        manager.Register(new FixedDriver("r7274-good", DeviceTagQuality.Good, DateTimeOffset.UtcNow));
        manager.Register(new FixedDriver("r7274-stale", DeviceTagQuality.Good, DateTimeOffset.UtcNow.AddHours(-2)));
        var read = new DeviceReadTagNode(manager);
        var good = await read.ExecuteAsync(Ctx(new()),
            Def("device.readTag", """{"deviceId":"r7274-good","tagId":"trigger","maxAgeMs":60000}"""), CancellationToken.None);
        Assert.True(good.Outputs["ageMs"].Require<double>("ageMs") >= 0);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await read.ExecuteAsync(Ctx(new()),
                Def("device.readTag", """{"deviceId":"r7274-stale","tagId":"trigger","maxAgeMs":1000}"""), CancellationToken.None));

        var writeResult = await new DeviceWriteVisionResultNode(manager).ExecuteAsync(
            Ctx(new() { ["pass"] = VisionValue.Boolean(true), ["x"] = VisionValue.Double(12.5), ["y"] = VisionValue.Double(-3), ["r"] = VisionValue.Double(45) }),
            Def("device.writeVisionResult", """{"deviceId":"r7274-good"}"""), CancellationToken.None);
        Assert.Equal(6L, writeResult.Outputs["tagsWritten"].Require<long>("tagsWritten"));

        await using var robots = new RobotManager();
        robots.Register(new VirtualAbbRobotAdapter());
        var pose = await new RobotCurrentPoseNode(robots).ExecuteAsync(Ctx(new()),
            Def("robot.currentPose", """{"robotId":"virtual-abb-1"}"""), CancellationToken.None);
        Assert.True(pose.Outputs.ContainsKey("x"));
        Assert.True(pose.Outputs.ContainsKey("thetaDeg"));
        Assert.Contains("robot.currentPose", pose.Outputs["diagnostics"].Require<string>("diagnostics"));
    }

    [Fact]
    public async Task ContourAndRegionCatalogNodes_Smoke()
    {
        var fromPoints = await new ContourFromPointsNode().ExecuteAsync(Ctx(new()),
            Def("contour.fromPoints", """{"pointsJson":"[[10,0],[30,0],[40,10],[40,30],[30,40],[10,40],[0,30],[0,10]]","closed":true}"""), CancellationToken.None);
        var props = await new ContourPropertiesNode().ExecuteAsync(
            Ctx(new() { ["contours"] = fromPoints.Outputs["contours"] }),
            Def("contour.properties", """{"selectBy":"largest"}"""), CancellationToken.None);
        Assert.True(props.Outputs["area"].Require<double>("area") > 0);

        var ellipse = await new FitEllipseNode().ExecuteAsync(
            Ctx(new() { ["contours"] = fromPoints.Outputs["contours"] }),
            Def("measure.fitEllipse", """{"selectBy":"largest"}"""), CancellationToken.None);
        Assert.True(ellipse.Outputs.ContainsKey("diagnostics"));

        using var img = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(img, new Rect(8, 8, 24, 24), Scalar.All(200), -1);
        var blob = await new LargestBlobNode().ExecuteAsync(Ctx(new() { ["image"] = Img(img) }),
            Def("measure.blob", """{"minArea":10}"""), CancellationToken.None);
        Assert.True(blob.Outputs["found"].Require<bool>("found"));

        var region = Rect(6, 6, 20, 16, 64);
        var regionProps = await new RegionPropertiesNode().ExecuteAsync(Ctx(new() { ["region"] = VisionValue.Region(region) }),
            Def("region.properties", """{"index":0,"selectBy":"index"}"""), CancellationToken.None);
        Assert.True(regionProps.Outputs["found"].Require<bool>("found"));

        var point = await new CoordinatePointNode().ExecuteAsync(Ctx(new()),
            Def("coordinate.point", """{"x":3,"y":4,"frame":"Workpiece","unit":"mm"}"""), CancellationToken.None);
        Assert.Equal("mm", point.Outputs["unit"].Require<string>("unit"));
    }

    private sealed class FakeSync(VisionFrameSet frameSet) : ISynchronizedFrameSetService
    {
        public Task<VisionFrameSet> CaptureAsync(SynchronizedFrameSetCaptureRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(frameSet);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }

    private sealed class FixedDriver(string id, DeviceTagQuality quality, DateTimeOffset timestamp) : IDeviceDriver
    {
        private DeviceConnectionState _state = DeviceConnectionState.Disconnected;
        public string Id { get; } = id;
        public string Name => Id;
        public string Vendor => "Test";
        public string Model => "Fixed";
        public string Driver => "FixedDriver";
        public string Protocol => "test";
        public string Endpoint => "memory://fixed";
        public DeviceConnectionState ConnectionState => _state;
        public string? Error => null;
        public DeviceDriverCapabilities Capabilities { get; } = new();
        public IReadOnlyList<DeviceTagDefinition> Tags { get; } =
        [
            new("trigger", "Trigger", "0", DeviceTagDataType.Boolean, true),
            new("resultReady", "Result Ready", "1", DeviceTagDataType.Boolean, true),
            new("resultOk", "Result OK", "2", DeviceTagDataType.Boolean, true),
            new("resultX", "Result X", "3", DeviceTagDataType.Double, true),
            new("resultY", "Result Y", "4", DeviceTagDataType.Double, true),
            new("resultR", "Result R", "5", DeviceTagDataType.Double, true)
        ];
        public Task ConnectAsync(CancellationToken cancellationToken = default) { _state = DeviceConnectionState.Connected; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { _state = DeviceConnectionState.Disconnected; return Task.CompletedTask; }
        public Task<DeviceTagSample> ReadAsync(string tagId, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceTagSample(Id, tagId, DeviceTagDataType.Boolean, true, quality, timestamp));
        public Task WriteAsync(string tagId, object? value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
