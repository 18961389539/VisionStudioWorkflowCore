using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.Aruco;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;

namespace VisionStudio.Engine.Tests;

/// <summary>OpenCV 5 colour/denoise/stitch/ArUco nodes: behaviour and failure contracts.</summary>
public sealed class OpenCvExtraNodeTests
{
    [Fact]
    public async Task ColorCluster_RecoversTwoSolidColoursAndIsDeterministic()
    {
        using var source = TwoColourImage();
        var first = await Run(new ColorClassifyNode(), "color.classify", Image(source),
            """{"mode":"cluster","clusterCount":2,"sampleStride":1}""");
        using var firstMap = first.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(2L, first.Outputs["count"].Require<long>("count"));
        Assert.NotEqual(firstMap.Mat.At<byte>(10, 10), firstMap.Mat.At<byte>(10, 50));
        Assert.Equal(2048L, ClassCount(first, "cluster-1"));
        Assert.Equal(2048L, ClassCount(first, "cluster-2"));

        var second = await Run(new ColorClassifyNode(), "color.classify", Image(source),
            """{"mode":"cluster","clusterCount":2,"sampleStride":1}""");
        using var secondMap = second.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0, Cv2.Norm(firstMap.Mat, secondMap.Mat, NormTypes.L1));
        Assert.Equal(first.Outputs["classCounts"].Require<string>("classCounts"),
            second.Outputs["classCounts"].Require<string>("classCounts"));
    }

    [Fact]
    public async Task ColorReference_MatchesKnownColoursAndCountsUnmatchedPixels()
    {
        using var source = new Mat(64, 96, MatType.CV_8UC3, Scalar.All(0));
        source[new Rect(0, 0, 32, 64)].SetTo(new Scalar(0, 0, 255));
        source[new Rect(32, 0, 32, 64)].SetTo(new Scalar(0, 255, 0));
        source[new Rect(64, 0, 32, 64)].SetTo(new Scalar(255, 0, 0));

        var result = await Run(new ColorClassifyNode(), "color.classify", Image(source),
            """{"mode":"reference","colorSpace":"rgb","referenceColorsJson":"[{\"name\":\"red\",\"r\":255,\"g\":0,\"b\":0,\"tolerance\":30},{\"name\":\"green\",\"r\":0,\"g\":255,\"b\":0,\"tolerance\":30}]"}""");
        using var labelMap = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(2L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(2048L, ClassCount(result, "red"));
        Assert.Equal(2048L, ClassCount(result, "green"));
        Assert.Equal(2048L, result.Outputs["unmatchedPixels"].Require<long>("unmatchedPixels"));
        Assert.Equal(1L, result.Outputs["dominantClass"].Require<long>("dominantClass"));
    }

    [Fact]
    public async Task ColorReference_RoiExcludesUnselectedColours()
    {
        using var source = TwoColourImage();
        var result = await Run(new ColorClassifyNode(), "color.classify", Image(source),
            """{"mode":"reference","colorSpace":"rgb","roi":{"type":"Rectangle","x":0,"y":0,"width":32,"height":64},"referenceColorsJson":"[{\"name\":\"red\",\"r\":255,\"g\":0,\"b\":0,\"tolerance\":30},{\"name\":\"green\",\"r\":0,\"g\":255,\"b\":0,\"tolerance\":30}]"}""");
        using var labelMap = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(1L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal(2048L, ClassCount(result, "red"));
        Assert.Equal(0L, ClassCount(result, "green"));
        Assert.Equal(0L, result.Outputs["unmatchedPixels"].Require<long>("unmatchedPixels"));
    }

    [Fact]
    public async Task ColorClassify_RejectsInvalidModeCountAndDepth()
    {
        using var source = TwoColourImage();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ColorClassifyNode(), "color.classify", Image(source),
            """{"mode":"cluster","clusterCount":1}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ColorClassifyNode(), "color.classify", Image(source),
            """{"mode":"reference","referenceColorsJson":"[]"}"""));
        using var sixteen = new Mat(8, 8, MatType.CV_16UC1, Scalar.All(1));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ColorClassifyNode(), "color.classify", Image(sixteen), """{"mode":"cluster"}"""));
        Assert.Contains("8-bit", error.Message);
    }

    [Fact]
    public async Task Denoise_ReducesNoiseAndPreservesPixelsOutsideRoi()
    {
        using var noisy = NoisyImage();
        var whole = await Run(new ImageDenoiseNode(), "image.denoise", Image(noisy),
            """{"mode":"gray","h":10,"templateWindowSize":7,"searchWindowSize":21}""");
        using var wholeImage = whole.Outputs["image"].Require<VisionImage>("image");
        Cv2.MeanStdDev(noisy, out _, out var before);
        Cv2.MeanStdDev(wholeImage.Mat, out _, out var after);
        Assert.True(after[0] < before[0], $"denoise should reduce noise: {before[0]:0.###} -> {after[0]:0.###}");
        wholeImage.Dispose();

        var roiResult = await Run(new ImageDenoiseNode(), "image.denoise", Image(noisy),
            """{"mode":"gray","h":10,"roi":{"type":"Rectangle","x":0,"y":0,"width":32,"height":32}}""");
        using var roiImage = roiResult.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(noisy.At<byte>(50, 50), roiImage.Mat.At<byte>(50, 50));
        Assert.NotEqual(noisy.At<byte>(8, 8), roiImage.Mat.At<byte>(8, 8));
    }

    [Fact]
    public async Task Denoise_ColorModeKeepsThreeChannels()
    {
        using var source = new Mat(48, 48, MatType.CV_8UC3, new Scalar(120, 120, 120));
        for (var y = 0; y < 48; y += 8)
            for (var x = 0; x < 48; x += 8)
                source.Set(y, x, new Vec3b((byte)((x + y) % 16 == 0 ? 0 : 255), 120, 255));
        var result = await Run(new ImageDenoiseNode(), "image.denoise", Image(source),
            """{"mode":"color","h":10,"hColor":10,"templateWindowSize":7,"searchWindowSize":21}""");
        using var image = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(3, image.Mat.Channels());
        Assert.Equal(48, image.Width);
        Assert.Equal(48, image.Height);
    }

    [Fact]
    public async Task Denoise_RejectsWrongDepthChannelModeAndEvenWindow()
    {
        using var source = new Mat(16, 16, MatType.CV_16UC1, Scalar.All(1));
        var depth = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ImageDenoiseNode(), "image.denoise", Image(source), "{}"));
        Assert.Contains("8-bit", depth.Message);

        using var color = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(1));
        var mode = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ImageDenoiseNode(), "image.denoise", Image(color), """{"mode":"gray"}"""));
        Assert.Contains("single-channel", mode.Message);

        using var gray = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(1));
        var window = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ImageDenoiseNode(), "image.denoise", Image(gray), """{"templateWindowSize":8}"""));
        Assert.Contains("odd", window.Message);
    }

    [Fact]
    public async Task Stitch_CombinesOverlappingScans()
    {
        using var source = StitchSource();
        using var left = new Mat(source, new Rect(0, 0, 250, 240)).Clone();
        using var right = new Mat(source, new Rect(50, 0, 250, 240)).Clone();
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image1"] = Image(left),
            ["image2"] = Image(right)
        };
        try
        {
            var result = await Run(new ImageStitchNode(), "image.stitch", inputs,
                """{"mode":"scans","maxInputPixels":1000000}""");
            using var panorama = result.Outputs["image"].Require<VisionImage>("image");
            Assert.True(result.Outputs["found"].Require<bool>("found"));
            Assert.Equal(2L, result.Outputs["inputCount"].Require<long>("inputCount"));
            Assert.True(panorama.Width >= 280, $"panorama width {panorama.Width} should cover both scans");
            Assert.True(panorama.Height >= 200);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Stitch_RejectsSingleImageBudgetAndDepth()
    {
        using var source = StitchSource();
        using var one = new Mat(source, new Rect(0, 0, 100, 120)).Clone();
        var inputs = new Dictionary<string, VisionValue> { ["image1"] = Image(one) };
        try
        {
            var single = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Run(new ImageStitchNode(), "image.stitch", inputs, "{}"));
            Assert.Contains("at least two", single.Message);
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as IDisposable)?.Dispose();
        }

        using var left = new Mat(source, new Rect(0, 0, 100, 120)).Clone();
        using var right = new Mat(source, new Rect(20, 0, 100, 120)).Clone();
        var budgetInputs = new Dictionary<string, VisionValue> { ["image1"] = Image(left), ["image2"] = Image(right) };
        try
        {
            var budget = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Run(new ImageStitchNode(), "image.stitch", budgetInputs, """{"maxInputPixels":1}"""));
            Assert.Contains("maxInputPixels", budget.Message);
        }
        finally
        {
            foreach (var value in budgetInputs.Values) (value.Value as IDisposable)?.Dispose();
        }

        using var sixteenLeft = new Mat(20, 20, MatType.CV_16UC1, Scalar.All(1));
        using var sixteenRight = new Mat(20, 20, MatType.CV_16UC1, Scalar.All(1));
        var depthInputs = new Dictionary<string, VisionValue> { ["image1"] = Image(sixteenLeft), ["image2"] = Image(sixteenRight) };
        try
        {
            var depth = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Run(new ImageStitchNode(), "image.stitch", depthInputs, "{}"));
            Assert.Contains("8-bit", depth.Message);
        }
        finally
        {
            foreach (var value in depthInputs.Values) (value.Value as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public async Task Aruco_DetectsMarkerAndSkipsPoseWithoutCalibration()
    {
        using var image = MarkerImage(7);
        var result = await Run(new ArucoMarkerNode(), "recognition.aruco", Image(image),
            """{"dictionary":"Dict4X4_50","maxMarkers":10}""");
        using var overlay = result.Outputs["overlay"].Require<VisionImage>("overlay");
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.Equal(1L, result.Outputs["count"].Require<long>("count"));
        Assert.Equal("[7]", result.Outputs["markerIds"].Require<string>("markerIds"));
        Assert.False(result.Outputs["poseAvailable"].Require<bool>("poseAvailable"));
        Assert.Single(result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections"));
        Assert.Equal(320, overlay.Width);
    }

    [Fact]
    public async Task Aruco_ComputesPoseWithPinholeCalibration()
    {
        using var image = MarkerImage(7);
        var calibration = JsonSerializer.Serialize(new
        {
            imageWidth = 320,
            imageHeight = 320,
            cameraMatrix = new[] { new[] { 600d, 0d, 160d }, new[] { 0d, 600d, 160d }, new[] { 0d, 0d, 1d } },
            distCoeffs = new[] { 0d, 0d, 0d, 0d, 0d },
            rms = 0.1
        });
        var json = JsonSerializer.Serialize(new
        {
            dictionary = "Dict4X4_50",
            markerSize = 50,
            markerSizeUnit = "mm",
            calibrationJson = calibration
        });
        var result = await Run(new ArucoMarkerNode(), "recognition.aruco", Image(image), json);
        using var overlay = result.Outputs["overlay"].Require<VisionImage>("overlay");
        Assert.True(result.Outputs["poseAvailable"].Require<bool>("poseAvailable"));
        var pose = result.Outputs["pose"].Require<VisionTransform3D>("pose");
        Assert.Equal("mm", pose.Unit);
        Assert.Equal(1d, pose.Matrix[15]);
        Assert.InRange(pose.Matrix[11], 50, 500);
        Assert.Contains("\"id\":7", result.Outputs["poses"].Require<string>("poses"));
    }

    [Fact]
    public async Task Charuco_DetectsBoardAndAppendsCalibrationView()
    {
        using var image = CharucoImage();
        var result = await Run(new CharucoBoardNode(), "calibration.charuco", Image(image),
            """{"squaresX":5,"squaresY":7,"squareLengthMm":60,"markerLengthMm":30,"dictionary":"Dict4X4_50","minCorners":6}""");
        using var overlay = result.Outputs["overlay"].Require<VisionImage>("overlay");
        Assert.True(result.Outputs["found"].Require<bool>("found"));
        Assert.True(result.Outputs["count"].Require<long>("count") >= 6);
        Assert.True(result.Outputs["markerCount"].Require<long>("markerCount") > 0);
        Assert.True(result.Outputs.ContainsKey("viewJson"));
        using var data = JsonDocument.Parse(result.Outputs["calibrationDataJson"].Require<string>("calibrationDataJson"));
        Assert.Equal(1, data.RootElement.GetProperty("Views").GetArrayLength());
        Assert.Equal(image.Width, overlay.Width);
        Assert.Equal(image.Height, overlay.Height);
    }

    [Fact]
    public async Task Charuco_AccumulatesViewsAndHonoursWeakViewRejection()
    {
        using var image = CharucoImage();
        var first = await Run(new CharucoBoardNode(), "calibration.charuco", Image(image),
            """{"squaresX":5,"squaresY":7,"squareLengthMm":60,"markerLengthMm":30,"minCorners":6}""");
        using (var firstOverlay = first.Outputs["overlay"].Require<VisionImage>("overlay")) { }
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image"] = Image(image),
            ["calibrationDataJson"] = VisionValue.String(first.Outputs["calibrationDataJson"].Require<string>("calibrationDataJson"))
        };
        try
        {
            var second = await Run(new CharucoBoardNode(), "calibration.charuco", inputs,
                """{"squaresX":5,"squaresY":7,"squareLengthMm":60,"markerLengthMm":30,"minCorners":6}""");
            using var secondOverlay = second.Outputs["overlay"].Require<VisionImage>("overlay");
            using var data = JsonDocument.Parse(second.Outputs["calibrationDataJson"].Require<string>("calibrationDataJson"));
            Assert.Equal(2, data.RootElement.GetProperty("Views").GetArrayLength());
        }
        finally
        {
            foreach (var value in inputs.Values) (value.Value as IDisposable)?.Dispose();
        }

        var rejected = await Run(new CharucoBoardNode(), "calibration.charuco", Image(image),
            """{"squaresX":5,"squaresY":7,"squareLengthMm":60,"markerLengthMm":30,"minCorners":6,"rejectWeakViews":true,"minBlurVariance":1000000000}""");
        using var rejectedOverlay = rejected.Outputs["overlay"].Require<VisionImage>("overlay");
        Assert.False(rejected.Outputs["found"].Require<bool>("found"));
        using var rejectedData = JsonDocument.Parse(rejected.Outputs["calibrationDataJson"].Require<string>("calibrationDataJson"));
        Assert.Equal(0, rejectedData.RootElement.GetProperty("Views").GetArrayLength());
    }

    [Fact]
    public async Task Charuco_RejectsInvalidGeometryAndDictionary()
    {
        using var image = CharucoImage();
        var geometry = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new CharucoBoardNode(), "calibration.charuco", Image(image),
                """{"squaresX":5,"squaresY":7,"squareLengthMm":30,"markerLengthMm":30}"""));
        Assert.Contains("markerLengthMm", geometry.Message);

        var dictionary = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new CharucoBoardNode(), "calibration.charuco", Image(image),
                """{"squaresX":5,"squaresY":7,"squareLengthMm":60,"markerLengthMm":30,"dictionary":"NOPE"}"""));
        Assert.Contains("dictionary", dictionary.Message);

        using var empty = new Mat();
        var emptyError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new CharucoBoardNode(), "calibration.charuco", Image(empty), "{}"));
        Assert.Contains("non-empty", emptyError.Message);
    }

    private static Mat CharucoImage()
    {
        using var dictionary = Cv2.Aruco.GetPredefinedDictionary(PredefinedDictionaryType.Dict4X4_50);
        using var board = new CharucoBoard(5, 7, 60f, 30f, dictionary, null);
        var image = new Mat();
        board.GenerateImage(new Size(640, 800), image, 20, 1);
        return image;
    }

    [Fact]
    public async Task Aruco_RejectsUnknownDictionaryAndTooManyMarkers()
    {
        using var image = MarkerImage(7);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ArucoMarkerNode(), "recognition.aruco", Image(image), """{"dictionary":"NOPE"}"""));
        Assert.Contains("dictionary", unknown.Message);

        using var twoMarkers = new Mat(320, 640, MatType.CV_8UC1, Scalar.All(255));
        using var dictionary = Cv2.Aruco.GetPredefinedDictionary(PredefinedDictionaryType.Dict4X4_50);
        using var leftMarker = new Mat();
        using var rightMarker = new Mat();
        dictionary.GenerateImageMarker(7, 160, leftMarker, 1);
        dictionary.GenerateImageMarker(8, 160, rightMarker, 1);
        using (var left = new Mat(twoMarkers, new Rect(40, 80, 160, 160)))
            leftMarker.CopyTo(left);
        using (var right = new Mat(twoMarkers, new Rect(440, 80, 160, 160)))
            rightMarker.CopyTo(right);
        var tooMany = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new ArucoMarkerNode(), "recognition.aruco", Image(twoMarkers), """{"dictionary":"Dict4X4_50","maxMarkers":1}"""));
        Assert.Contains("maxMarkers", tooMany.Message);
    }

    private static async Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor, string type, Dictionary<string, VisionValue> inputs, string json)
    {
        var node = new NodeDefinition("n1", type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
        return await executor.ExecuteAsync(new NodeExecutionContext(inputs), node, CancellationToken.None);
    }

    private static Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor, string type, VisionValue image, string json)
        => Run(executor, type, new Dictionary<string, VisionValue> { ["image"] = image }, json);

    private static VisionValue Image(Mat mat)
        => VisionValue.Image(VisionImage.Own(mat.Clone()));

    private static Mat TwoColourImage()
    {
        var mat = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(0));
        mat[new Rect(0, 0, 32, 64)].SetTo(new Scalar(0, 0, 255));
        mat[new Rect(32, 0, 32, 64)].SetTo(new Scalar(0, 255, 0));
        return mat;
    }

    private static Mat NoisyImage()
    {
        var mat = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(128));
        var rows = mat.Rows;
        var columns = mat.Cols;
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
                if (x % 8 == 0 && y % 8 == 0) mat.Set(y, x, (byte)((x + y) % 16 == 0 ? 0 : 255));
        return mat;
    }

    private static Mat StitchSource()
    {
        var mat = new Mat(240, 300, MatType.CV_8UC1, Scalar.All(0));
        var random = new Random(17);
        var rows = mat.Rows;
        var columns = mat.Cols;
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
                mat.Set(y, x, (byte)random.Next(20, 236));
        for (var index = 0; index < 24; index++)
        {
            var x = 12 + index * 11;
            var y = 16 + (index * 37) % 200;
            Cv2.Rectangle(mat, new Rect(x, y, 18, 14), Scalar.All(index % 2 == 0 ? 30 : 220), -1);
        }
        return mat;
    }

    private static Mat MarkerImage(int id)
    {
        var canvas = new Mat(320, 320, MatType.CV_8UC1, Scalar.All(255));
        using var dictionary = Cv2.Aruco.GetPredefinedDictionary(PredefinedDictionaryType.Dict4X4_50);
        using var marker = new Mat();
        dictionary.GenerateImageMarker(id, 160, marker, 1);
        using var target = new Mat(canvas, new Rect(80, 80, 160, 160));
        marker.CopyTo(target);
        return canvas;
    }

    private static long ClassCount(NodeExecutorResult result, string name)
    {
        using var document = JsonDocument.Parse(result.Outputs["classCounts"].Require<string>("classCounts"));
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.GetProperty("name").GetString() == name)
                return item.GetProperty("pixelCount").GetInt64();
        }
        return 0;
    }
}
