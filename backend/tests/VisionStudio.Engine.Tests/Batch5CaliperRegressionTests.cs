using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class Batch5CaliperRegressionTests
{
    [Theory]
    [InlineData("measure.caliper", """{"polarity":"sideways"}""", "polarity")]
    [InlineData("measure.caliper", """{"direction":"middle"}""", "direction")]
    [InlineData("measure.caliper", """{"refine":"cubic"}""", "refine")]
    [InlineData("measure.caliperLine", """{"polarity":"sideways"}""", "polarity")]
    [InlineData("measure.caliperLine", """{"direction":"middle"}""", "direction")]
    [InlineData("measure.caliperLine", """{"refine":"cubic"}""", "refine")]
    [InlineData("measure.caliperCircle", """{"polarity":"sideways"}""", "polarity")]
    [InlineData("measure.caliperCircle", """{"searchDirection":"middle"}""", "searchDirection")]
    [InlineData("measure.caliperCircle", """{"samplingMode":"adaptive"}""", "samplingMode")]
    public async Task CaliperNodesRejectUnknownChoicesInsteadOfSilentlyFallingBack(string type, string parameters, string parameter)
    {
        using var image = VisionImage.Own(new Mat(128, 128, MatType.CV_8UC1, Scalar.All(0)));
        IVisionNodeExecutor executor = type switch
        {
            "measure.caliper" => new CaliperNode(),
            "measure.caliperLine" => new CaliperLineNode(),
            _ => new CaliperCircleNode()
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            new NodeExecutionContext(Inputs(image)), Node(type, parameters), CancellationToken.None).AsTask());

        Assert.Contains($"Parameter '{parameter}'", error.Message);
        Assert.Contains("must be one of", error.Message);
    }

    [Fact]
    public async Task CaliperLineAndCircleSummariesExposeEffectiveSearchSettings()
    {
        using var image = VisionImage.Own(new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80)));

        var line = await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(image)),
            Node("measure.caliperLine", """{"startX":10,"startY":20,"endX":30,"endY":20,"edgeLength":0,"caliperCount":5,"minContrast":17,"sampleWidth":7}"""),
            CancellationToken.None);
        Assert.Equal(17d, Convert.ToDouble(line.Summary["minContrast"]));
        Assert.Equal(0d, Convert.ToDouble(line.Summary["edgeLength"]));
        Assert.Equal(20d, Convert.ToDouble(line.Summary["effectiveCaliperSpan"]));
        (line.Outputs["image"].Value as IDisposable)?.Dispose();

        var circle = await new CaliperCircleNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(image),
            ["transform"] = VisionValue.Transform(new VisionTransform2D(
                "Reference", "Current", "px", 0, -1, 133, 1, 0, -3, 0, 0, 1))
        }), Node("measure.caliperCircle", """{"centerX":64,"centerY":64,"radius":30,"angleStartDeg":10,"angleEndDeg":70,"caliperCount":5,"radialBand":6,"minContrast":19,"sampleWidth":7,"maxRadiusDeviation":1.5,"maxFitOutliers":2,"fitOutlierThreshold":0.75}"""),
            CancellationToken.None);
        Assert.Equal(69d, Convert.ToDouble(circle.Summary["centerX"]));
        Assert.Equal(61d, Convert.ToDouble(circle.Summary["centerY"]));
        Assert.Equal(100d, Convert.ToDouble(circle.Summary["angleStartDeg"]));
        Assert.Equal(160d, Convert.ToDouble(circle.Summary["angleEndDeg"]));
        Assert.Equal(19d, Convert.ToDouble(circle.Summary["minContrast"]));
        Assert.Equal(7, Convert.ToInt32(circle.Summary["sampleWidth"]));
        Assert.Equal(1.5, Convert.ToDouble(circle.Summary["maxRadiusDeviation"]));
        Assert.Equal(0.75, Convert.ToDouble(circle.Summary["fitOutlierThreshold"]));
        (circle.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperCircleReportsWhenCoordinateCorrectionMovesTheCenterOutOfRange()
    {
        using var image = VisionImage.Own(new Mat(32, 32, MatType.CV_8UC1, Scalar.All(0)));
        var inputs = Inputs(image);
        inputs["circle"] = VisionValue.Circle(new VisionCircle(100, 16, 8));
        inputs["transform"] = VisionValue.Transform(new VisionTransform2D(
            "ReferenceImage", "CurrentImage", "px", 1, 0, 10_000_001, 0, 1, 0, 0, 0, 1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CaliperCircleNode().ExecuteAsync(
            new NodeExecutionContext(inputs), Node("measure.caliperCircle", "{}"), CancellationToken.None).AsTask());

        Assert.Equal("Caliper circle center after coordinate correction must be finite and within +/-10000000 pixels.", error.Message);
    }

    [Theory]
    [InlineData("feature.edgeSubpixel")]
    [InlineData("measure.caliper")]
    [InlineData("measure.caliperLine")]
    [InlineData("measure.caliperCircle")]
    public async Task CaliperNodesRejectDegenerateConnectedRoiInsteadOfReportingNoEdge(string type)
    {
        using var image = VisionImage.Own(new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80)));
        var inputs = Inputs(image);
        inputs["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Polygon,
            Points: [new VisionRoiPoint(10, 10), new VisionRoiPoint(40, 10)]));
        IVisionNodeExecutor executor = type switch
        {
            "feature.edgeSubpixel" => new EdgeSubpixelNode(),
            "measure.caliper" => new CaliperNode(),
            "measure.caliperLine" => new CaliperLineNode(),
            _ => new CaliperCircleNode()
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            new NodeExecutionContext(inputs), Node(type, "{}"), CancellationToken.None).AsTask());

        Assert.Contains("Polygon ROI requires 3..500000 finite vertices", error.Message);
    }

    [Fact]
    public async Task CaliperLine_ClassifiesProfilesBeyondCoordinateLimitAsOutsideImage()
    {
        using var image = VisionImage.Own(new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80)));

        var result = await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(image)),
            Node("measure.caliperLine", """{"startX":10,"startY":10000000,"endX":110,"endY":10000000,"edgeLength":100,"caliperCount":3,"sampleWidth":1}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("search-region-outside-image", result.Summary["reason"]);
        Assert.Equal(3, Convert.ToInt32(result.Summary["profilesOutsideImage"]));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperLine_ClipsIntersectingProfilesAfterTheirOffsetsCrossTheInputCoordinateLimit()
    {
        using var image = VisionImage.Own(new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80)));

        var result = await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(image)),
            Node("measure.caliperLine", """{"startX":10000000,"startY":100,"endX":-10000000,"endY":0,"edgeLength":2,"caliperCount":3,"sampleWidth":1}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("no-edge-above-threshold", result.Summary["reason"]);
        Assert.Equal(3, Convert.ToInt32(result.Summary["profilesIntersectingImage"]));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperFamily_HandlesUInt16ImagesWithoutReadingOnlyTheLowByte()
    {
        using var lineMat = new Mat(128, 128, MatType.CV_16UC1, Scalar.All(0));
        using (var bright = new Mat(lineMat, new Rect(64, 0, 64, 128))) bright.SetTo(Scalar.All(51200));
        using var lineImage = VisionImage.Own(lineMat.Clone());

        var single = await new CaliperNode().ExecuteAsync(new NodeExecutionContext(Inputs(lineImage)),
            Node("measure.caliper", """{"startX":30,"startY":64,"endX":96,"endY":64,"sampleWidth":1,"edgeThreshold":30}"""), CancellationToken.None);
        Assert.True(Convert.ToBoolean(single.Summary["found"]), Convert.ToString(single.Summary["reason"]));
        Assert.InRange(single.Outputs["point"].Require<VisionPoint2D>("point").X, 63, 65);
        (single.Outputs["image"].Value as IDisposable)?.Dispose();

        var subpixel = await new EdgeSubpixelNode().ExecuteAsync(new NodeExecutionContext(Inputs(lineImage)),
            Node("feature.edgeSubpixel", """{"startX":30,"startY":64,"endX":96,"endY":64,"sampleWidth":1,"minContrast":30}"""), CancellationToken.None);
        Assert.True(Convert.ToBoolean(subpixel.Summary["found"]), Convert.ToString(subpixel.Summary["reason"]));
        Assert.InRange(subpixel.Outputs["point"].Require<VisionPoint2D>("point").X, 63, 65);
        (subpixel.Outputs["image"].Value as IDisposable)?.Dispose();

        var line = await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(lineImage)),
            Node("measure.caliperLine", """{"startX":30,"startY":64,"endX":96,"endY":64,"edgeLength":70,"caliperCount":9,"sampleWidth":1,"minContrast":30}"""), CancellationToken.None);
        Assert.True(Convert.ToBoolean(line.Summary["found"]), Convert.ToString(line.Summary["reason"]));
        Assert.InRange(line.Outputs["point"].Require<VisionPoint2D>("point").X, 63, 65);
        (line.Outputs["image"].Value as IDisposable)?.Dispose();

        using var circleMat = new Mat(128, 128, MatType.CV_16UC1, Scalar.All(0));
        Cv2.Circle(circleMat, new Point(64, 64), 30, Scalar.All(51200), 2);
        using var circleImage = VisionImage.Own(circleMat.Clone());
        var circle = await new CaliperCircleNode().ExecuteAsync(new NodeExecutionContext(Inputs(circleImage)),
            Node("measure.caliperCircle", """{"centerX":64,"centerY":64,"radius":30,"radialBand":5,"samplingMode":"count","caliperCount":72,"sampleWidth":1,"minContrast":30}"""), CancellationToken.None);
        Assert.True(Convert.ToBoolean(circle.Summary["found"]), Convert.ToString(circle.Summary["reason"]));
        Assert.InRange(circle.Outputs["radius"].Require<double>("radius"), 28, 32);
        (circle.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperCircle_ClassifiesOffImageRadialBandAsOutsideImage()
    {
        using var image = VisionImage.Own(new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80)));

        var result = await new CaliperCircleNode().ExecuteAsync(new NodeExecutionContext(Inputs(image)),
            Node("measure.caliperCircle", """{"centerX":10000000,"centerY":64,"radius":100,"radialBand":8,"samplingMode":"count","caliperCount":12}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("search-region-outside-image", result.Summary["reason"]);
        Assert.Equal(12, Convert.ToInt32(result.Summary["spokesOutsideImage"]));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(-0.7)]
    public async Task CaliperLine_FitsMeasuredDiagonalEdge(double slope)
    {
        using var mat = MakeEdgeImage(slope);
        using var input = VisionImage.Own(mat.Clone());
        using var registry = new VisionNodeRegistry();
        var executor = new CaliperLineNode();
        registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var node = Node(executor.Type, """{"startX":25,"startY":64,"endX":103,"endY":64,"edgeLength":58,"caliperCount":9,"sampleWidth":1,"minContrast":30}""");

        var result = await dispatcher.ExecuteAsync(node, Inputs(input), null, CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var fitted = result.Outputs["line"].Require<VisionLine2D>("line");
        var fittedSlope = (fitted.End.Y - fitted.Start.Y) / (fitted.End.X - fitted.Start.X);
        Assert.InRange(Math.Abs(fittedSlope - 1d / slope), 0, 0.12);
        Assert.True(Convert.ToInt32(result.Summary["sampleCount"]) >= 3);
        Assert.True(Convert.ToDouble(result.Summary["fitError"]) < 1.5);
        Assert.True(result.Outputs["image"].Require<VisionImage>("image").Mat.Rows > 0);
        Assert.True(input.Mat.Rows > 0);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperLine_NoEdgeAndRoiClippedSpan_ReturnFoundFalseWithImage()
    {
        using var flat = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80));
        using var input = VisionImage.Own(flat.Clone());
        var executor = new CaliperLineNode();
        var none = await executor.ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node(executor.Type, """{"startX":20,"startY":64,"endX":100,"endY":64,"sampleWidth":1}"""), CancellationToken.None);
        Assert.False(Convert.ToBoolean(none.Summary["found"]));
        Assert.Equal("no-edge-above-threshold", none.Summary["reason"]);
        Assert.True(none.Outputs.ContainsKey("image"));
        (none.Outputs["image"].Value as IDisposable)?.Dispose();

        using var edge = MakeEdgeImage(1);
        using var clippedInput = VisionImage.Own(edge.Clone());
        var clipped = await executor.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(clippedInput),
            ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Rectangle, 0, 62, 128, 3))
        }), Node(executor.Type, """{"startX":25,"startY":64,"endX":103,"endY":64,"edgeLength":58,"caliperCount":9,"sampleWidth":1,"minContrast":30}"""), CancellationToken.None);
        Assert.False(Convert.ToBoolean(clipped.Summary["found"]));
        Assert.True(clipped.Outputs.ContainsKey("image"));
        (clipped.Outputs["image"].Value as IDisposable)?.Dispose();
        Assert.True(clippedInput.Mat.Rows > 0);
    }

    [Fact]
    public async Task CaliperLine_ShowsDetectedCandidatesWhenThereAreTooFewPointsToFit()
    {
        using var mat = MakeEdgeImage(0);
        using var input = VisionImage.Own(mat.Clone());
        var context = new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(input),
            ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Rectangle, 0, 31, 128, 2))
        });
        var result = await new CaliperLineNode().ExecuteAsync(context,
            Node("measure.caliperLine", """{"startX":40,"startY":32,"endX":88,"endY":32,"caliperCount":3,"sampleWidth":1,"minContrast":20}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("too-few-edge-points", result.Summary["reason"]);
        var candidates = result.Overlays?.Where(overlay => overlay.Id.StartsWith("caliper-line-candidate-point-", StringComparison.Ordinal)).ToArray();
        Assert.Single(candidates!);
        Assert.InRange(candidates![0].X ?? double.NaN, 63.5, 64.5);
        Assert.InRange(candidates[0].Y ?? double.NaN, 31, 33);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperLine_RejectsZeroLengthSearchLine()
    {
        using var mat = MakeEdgeImage(1);
        using var input = VisionImage.Own(mat.Clone());
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(input)),
                Node("measure.caliperLine", """{"startX":64,"startY":64,"endX":64,"endY":64}"""), CancellationToken.None));
        Assert.Contains("too short", exception.Message);
        Assert.True(input.Mat.Rows > 0);
    }

    [Fact]
    public async Task CaliperLine_FullyOutsideSearchRegionReturnsNotFound()
    {
        using var mat = MakeEdgeImage(1);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node("measure.caliperLine", """{"startX":1000,"startY":1000,"endX":1100,"endY":1100}"""), CancellationToken.None);
        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("search-region-outside-image", result.Summary["reason"]);
        Assert.Equal(0L, result.Outputs["sampleCount"].Require<long>("sampleCount"));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperLine_ExplainsClippedAndRoiMaskedProfiles()
    {
        using var mat = MakeEdgeImage(1);
        using var input = VisionImage.Own(mat.Clone());
        var executor = new CaliperLineNode();

        var clipped = await executor.ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node(executor.Type, """{"startX":-100,"startY":0,"endX":0,"endY":0,"edgeLength":10000,"caliperCount":3,"sampleWidth":1} """.Trim()),
            CancellationToken.None);
        Assert.False(Convert.ToBoolean(clipped.Summary["found"]));
        Assert.Equal("search-region-clipped-too-short", clipped.Summary["reason"]);
        Assert.Equal(1, Convert.ToInt32(clipped.Summary["profilesTooShortAfterClip"]));
        (clipped.Outputs["image"].Value as IDisposable)?.Dispose();

        var masked = await executor.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(input),
            ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Rectangle, 0, 0, 128, 16))
        }), Node(executor.Type, """{"startX":24,"startY":64,"endX":104,"endY":64,"caliperCount":9,"sampleWidth":1} """.Trim()),
            CancellationToken.None);
        Assert.False(Convert.ToBoolean(masked.Summary["found"]));
        Assert.Equal("profile-has-no-finite-samples", masked.Summary["reason"]);
        Assert.Equal(9, Convert.ToInt32(masked.Summary["profilesWithoutFiniteSamples"]));
        (masked.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperLine_ReportsRoiFragmentedProfilesSeparatelyFromEmptyProfiles()
    {
        using var image = MakeEdgeImage(0);
        using var input = VisionImage.Own(image.Clone());
        var strokes = new List<VisionRoiBrushStroke>();
        foreach (var y in new[] { 62d, 64d, 66d })
        foreach (var x in new[] { 20d, 40d, 70d })
            strokes.Add(new(VisionRoiStrokeMode.Add, 1, [new VisionRoiPoint(x, y)]));

        var result = await new CaliperLineNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(input),
            ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Brush, Strokes: strokes))
        }), Node("measure.caliperLine", """{"startX":10,"startY":64,"endX":118,"endY":64,"edgeLength":4,"caliperCount":3,"sampleWidth":1,"minContrast":20}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("profile-has-insufficient-contiguous-samples", result.Summary["reason"]);
        Assert.Equal(3, Convert.ToInt32(result.Summary["profilesWithInsufficientContiguousSamples"]));
        Assert.Equal(0, Convert.ToInt32(result.Summary["profilesWithoutFiniteSamples"]));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperCircle_ExplainsOutsideAndRoiMaskedSpokes()
    {
        using var flat = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80));
        using var input = VisionImage.Own(flat.Clone());
        var executor = new CaliperCircleNode();

        var outside = await executor.ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node(executor.Type, """{"centerX":500,"centerY":500,"radius":25,"radialBand":5,"samplingMode":"count","caliperCount":12} """.Trim()),
            CancellationToken.None);
        Assert.False(Convert.ToBoolean(outside.Summary["found"]));
        Assert.Equal("search-region-outside-image", outside.Summary["reason"]);
        Assert.Equal(12, Convert.ToInt32(outside.Summary["spokesOutsideImage"]));
        Assert.Equal(0L, outside.Outputs["sampleCount"].Require<long>("sampleCount"));
        (outside.Outputs["image"].Value as IDisposable)?.Dispose();

        var masked = await executor.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(input),
            ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Rectangle, 0, 0, 10, 10))
        }), Node(executor.Type, """{"centerX":64,"centerY":64,"radius":25,"radialBand":5,"samplingMode":"count","caliperCount":12} """.Trim()),
            CancellationToken.None);
        Assert.False(Convert.ToBoolean(masked.Summary["found"]));
        Assert.Equal("profile-has-no-finite-samples", masked.Summary["reason"]);
        Assert.Equal(12, Convert.ToInt32(masked.Summary["spokesWithoutFiniteSamples"]));
        (masked.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperCircle_ReportsRoiFragmentedSpokesSeparatelyFromEmptySpokes()
    {
        using var image = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(80));
        using var input = VisionImage.Own(image.Clone());
        var strokes = new List<VisionRoiBrushStroke>();
        foreach (var radius in new[] { 13d, 20d, 27d })
        foreach (var (x, y) in new[] { (64 + radius, 64d), (64d, 64 + radius), (64 - radius, 64d) })
            strokes.Add(new(VisionRoiStrokeMode.Add, 1, [new VisionRoiPoint(x, y)]));

        var result = await new CaliperCircleNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(input),
            ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Brush, Strokes: strokes))
        }), Node("measure.caliperCircle", """{"centerX":64,"centerY":64,"radius":20,"radialBand":8,"samplingMode":"count","caliperCount":3,"angleStartDeg":0,"angleEndDeg":180,"sampleWidth":1}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("profile-has-insufficient-contiguous-samples", result.Summary["reason"]);
        Assert.Equal(3, Convert.ToInt32(result.Summary["spokesWithInsufficientContiguousSamples"]));
        Assert.Equal(0, Convert.ToInt32(result.Summary["spokesWithoutFiniteSamples"]));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperCircle_ShortStraightEdgeArcDoesNotReportAnUnstableRadius()
    {
        using var image = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(20));
        using (var brightHalf = new Mat(image, new Rect(64, 0, 64, 128))) brightHalf.SetTo(Scalar.All(220));
        using var input = VisionImage.Own(image.Clone());
        var result = await new CaliperCircleNode().ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node("measure.caliperCircle", """{"centerX":32,"centerY":64,"radius":32,"radialBand":8,"samplingMode":"count","caliperCount":5,"angleStartDeg":-12,"angleEndDeg":12,"sampleWidth":1,"minContrast":20} """.Trim()),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.True(Convert.ToInt32(result.Summary["edgePoints"]) >= 3);
        Assert.Equal("degenerate-circle-fit", result.Summary["reason"]);
        Assert.Equal(Convert.ToInt32(result.Summary["edgePoints"]), result.Outputs["sampleCount"].Require<long>("sampleCount"));
        Assert.Equal(Convert.ToInt32(result.Summary["edgePoints"]), result.Overlays?.Count(overlay =>
            overlay.Id.StartsWith("caliper-circle-candidate-point-", StringComparison.Ordinal)));
        Assert.False(result.Outputs.ContainsKey("circle"));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperCircle_VisualizesCandidatesRejectedByRadiusDeviation()
    {
        using var image = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Circle(image, new Point(67, 64), 25, Scalar.All(220), 2);
        using var input = VisionImage.Own(image.Clone());

        var result = await new CaliperCircleNode().ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node("measure.caliperCircle", """{"centerX":64,"centerY":64,"radius":25,"radialBand":8,"samplingMode":"count","caliperCount":36,"maxRadiusDeviation":1,"sampleWidth":1,"minContrast":20}"""),
            CancellationToken.None);

        var rejected = Convert.ToInt32(result.Summary["rejectedByDeviation"]);
        Assert.True(rejected > 0);
        var rejectedPoints = result.Overlays?.Where(overlay =>
            overlay.Id.StartsWith("caliper-circle-radius-rejected-", StringComparison.Ordinal)).ToArray() ?? [];
        Assert.Equal(rejected, rejectedPoints.Length);
        Assert.All(rejectedPoints, overlay => Assert.Equal("#faad14", overlay.Stroke));
        Assert.Equal(rejected, result.Overlays?.Count(overlay =>
            overlay.Id.StartsWith("caliper-circle-search-", StringComparison.Ordinal)
            && overlay.Stroke == "#faad14"));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task EdgeSubpixel_FitErrorUsesOverdeterminedWindow()
    {
        using var mat = new Mat(64, 128, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(mat, new Rect(64, 0, 64, 64), Scalar.All(220), -1);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new EdgeSubpixelNode().ExecuteAsync(
            new NodeExecutionContext(Inputs(input)),
            Node("feature.edgeSubpixel", """{"startX":30,"startY":32,"endX":98,"endY":32,"sampleWidth":1,"minContrast":20}"""),
            CancellationToken.None);

        var error = result.Outputs["fitError"].Require<double>("fitError");
        Assert.True(double.IsFinite(error));
        Assert.True(error > 0, "A five-sample least-squares fit should report a non-zero residual for this non-parabolic peak.");
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task EdgeSubpixel_ClipsSearchSegmentWithoutChangingItsDirection()
    {
        using var mat = MakeEdgeImage(0.5);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new EdgeSubpixelNode().ExecuteAsync(
            new NodeExecutionContext(Inputs(input)),
            Node("feature.edgeSubpixel", """{"startX":-20,"startY":160,"endX":140,"endY":80,"sampleWidth":1,"minContrast":30,"refine":"none"}"""),
            CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        Assert.True(Convert.ToBoolean(result.Summary["lineClampedToImage"]));
        var clipped = result.Outputs["searchLine"].Require<VisionLine2D>("searchLine");
        Assert.InRange(clipped.Start.X, 45.9, 46.1);
        Assert.InRange(clipped.Start.Y, 126.9, 127.1);
        Assert.InRange(clipped.End.X, 126.9, 127.1);
        Assert.InRange(clipped.End.Y, 86.4, 86.6);
        var point = result.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, 84.6, 86.6);
        Assert.InRange(point.Y, 106.2, 108.2);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task EdgeSubpixel_FullyOutsideSearchLineReturnsNotFound()
    {
        using var mat = MakeEdgeImage(1);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new EdgeSubpixelNode().ExecuteAsync(
            new NodeExecutionContext(Inputs(input)),
            Node("feature.edgeSubpixel", """{"startX":-40,"startY":-40,"endX":-20,"endY":-20}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("search-line-outside-image", result.Summary["reason"]);
        Assert.False(result.Outputs.ContainsKey("point"));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperMeasurement_ClipsTheSearchLineWithoutChangingTheMeasuredPosition()
    {
        using var mat = MakeEdgeImage(0.5);
        using var input = VisionImage.Own(mat.Clone());
        using var registry = new VisionNodeRegistry();
        var executor = new CaliperNode();
        registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "test");
        var result = await new VisionNodeDispatcher(registry).ExecuteAsync(
            Node(executor.Type, """{"startX":-20,"startY":160,"endX":140,"endY":80,"sampleWidth":1,"edgeThreshold":30}"""),
            Inputs(input), null, CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var point = result.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, 84.6, 86.6);
        Assert.InRange(point.Y, 106.2, 108.2);
        Assert.True(Convert.ToBoolean(result.Summary["lineClampedToImage"]));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(60.25)]
    [InlineData(60.5)]
    [InlineData(60.75)]
    public async Task CaliperMeasurement_SubpixelSamplingIsInvariantToPixelPhase(double edgeX)
    {
        using var mat = new Mat(64, 128, MatType.CV_8UC1);
        var rows = mat.Rows;
        var columns = mat.Cols;
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
        {
            var intensity = 20 + 200 / (1 + Math.Exp(-(x - edgeX) * 1.5));
            mat.Set(y, x, (byte)Math.Round(intensity));
        }
        using var input = VisionImage.Own(mat.Clone());
        var result = await new CaliperNode().ExecuteAsync(new NodeExecutionContext(Inputs(input)),
            Node("measure.caliper", """{"startX":20.25,"startY":32.5,"endX":100.25,"endY":32.5,"sampleWidth":1,"edgeThreshold":5,"polarity":"rising","refine":"parabolic"}"""),
            CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        Assert.InRange(result.Outputs["point"].Require<VisionPoint2D>("point").X, edgeX - 0.05, edgeX + 0.05);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task SingleCaliper_RoiMaskExcludesEdgesOutsideTheSelectedRegion()
    {
        using var mat = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(10));
        using (var brightHalf = new Mat(mat, new Rect(32, 0, 32, 64))) brightHalf.SetTo(Scalar.All(220));
        using var input = VisionImage.Own(mat.Clone());
        var executor = new CaliperNode();
        var node = Node(executor.Type, """{"startX":8,"startY":32,"endX":55,"endY":32,"sampleWidth":1,"edgeThreshold":20,"refine":"none"}""");

        var unmasked = await executor.ExecuteAsync(new NodeExecutionContext(Inputs(input)), node, CancellationToken.None);
        Assert.True(Convert.ToBoolean(unmasked.Summary["found"]));
        (unmasked.Outputs["image"].Value as IDisposable)?.Dispose();

        var masked = await executor.ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(input),
            ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Rectangle, 0, 0, 24, 64))
        }), node, CancellationToken.None);
        Assert.False(Convert.ToBoolean(masked.Summary["found"]));
        Assert.Equal("no-edge-above-threshold", masked.Summary["reason"]);
        (masked.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task SingleCaliper_ReportsInsufficientSamplingWhenRoiSplitsTheProfileIntoShortRuns()
    {
        using var image = MakeEdgeImage(0);
        using var input = VisionImage.Own(image.Clone());
        var sparseRoi = new VisionRoi(VisionRoiType.Brush, Strokes:
        [
            new(VisionRoiStrokeMode.Add, 1, [new VisionRoiPoint(20, 32)]),
            new(VisionRoiStrokeMode.Add, 1, [new VisionRoiPoint(32, 32)]),
            new(VisionRoiStrokeMode.Add, 1, [new VisionRoiPoint(64, 32)])
        ]);
        var result = await new CaliperNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(input),
            ["roi"] = VisionValue.Roi(sparseRoi)
        }), Node("measure.caliper", """{"startX":10,"startY":32,"endX":70,"endY":32,"sampleWidth":1,"edgeThreshold":20}"""),
            CancellationToken.None);

        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal("profile-has-insufficient-contiguous-samples", result.Summary["reason"]);
        Assert.Equal(3, Convert.ToInt32(result.Summary["sampleCount"]));
        Assert.Equal(3L, result.Outputs["sampleCount"].Require<long>("sampleCount"));
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CroppedRoi_CanMaskArtificialCropBoundaryEdgesDuringCaliperMeasurement()
    {
        using var source = MakeEdgeImage(0);
        using var input = VisionImage.Own(source.Clone());
        using var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.crop"), new ImageCropNode(), "test");
        registry.Register(BuiltInNodeCatalog.Require("measure.caliper"), new CaliperNode(), "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var crop = await dispatcher.ExecuteAsync(Node("image.crop", """{"roi":{"type":"Circle","x":64,"y":64,"radius":20}}"""),
            Inputs(input), null, CancellationToken.None);
        using var croppedImage = (VisionImage)crop.Outputs["image"].Require<IVisionImage>("image");
        var node = Node("measure.caliper", """{"startX":0,"startY":5,"endX":40,"endY":5,"sampleWidth":1,"edgeThreshold":20,"direction":"strongest"}""");

        var unmasked = await dispatcher.ExecuteAsync(node, Inputs(croppedImage), null, CancellationToken.None);
        Assert.True(Convert.ToBoolean(unmasked.Summary["found"]));
        var unmaskedPoint = unmasked.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.True(Math.Abs(unmaskedPoint.X - 20) > 5, $"Expected the zeroed crop boundary to dominate, got x={unmaskedPoint.X}.");
        (unmasked.Outputs["image"].Value as IDisposable)?.Dispose();

        var maskedInputs = Inputs(croppedImage);
        maskedInputs["roi"] = crop.Outputs["croppedRoi"];
        var masked = await dispatcher.ExecuteAsync(node, maskedInputs, null, CancellationToken.None);
        Assert.True(Convert.ToBoolean(masked.Summary["found"]));
        var measuredPoint = masked.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(measuredPoint.X, 19, 21);
        (masked.Outputs["image"].Value as IDisposable)?.Dispose();
        (crop.Outputs["roiMask"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CropTransform_MapsReferenceCaliperLineIntoCropCoordinates()
    {
        using var mat = MakeEdgeImage(0.5);
        using var input = VisionImage.Own(mat.Clone());
        using var registry = new VisionNodeRegistry();
        var cropExecutor = new ImageCropNode();
        var caliperExecutor = new CaliperNode();
        registry.Register(BuiltInNodeCatalog.Require(cropExecutor.Type), cropExecutor, "test");
        registry.Register(BuiltInNodeCatalog.Require(caliperExecutor.Type), caliperExecutor, "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var cropped = await dispatcher.ExecuteAsync(
            Node("image.crop", """{"x":10,"y":10,"width":108,"height":108}"""), Inputs(input), null, CancellationToken.None);
        using var cropImage = (VisionImage)cropped.Outputs["image"].Require<IVisionImage>("image");
        using var cropMask = (VisionImage)cropped.Outputs["roiMask"].Require<IVisionImage>("roiMask");

        var measurement = await dispatcher.ExecuteAsync(
            Node("measure.caliper", """{"startX":-20,"startY":160,"endX":140,"endY":80,"sampleWidth":1,"edgeThreshold":30}"""),
            new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(cropImage),
                ["transform"] = cropped.Outputs["transform"]
            }, null, CancellationToken.None);

        Assert.True(Convert.ToBoolean(measurement.Summary["found"]));
        var point = measurement.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, 74.6, 76.6);
        Assert.InRange(point.Y, 96.2, 98.2);
        using var outputImage = (VisionImage)measurement.Outputs["image"].Require<IVisionImage>("image");
        Assert.Equal((108, 108), (outputImage.Width, outputImage.Height));
    }

    [Fact]
    public async Task CropTransformsComposeAcrossAnImagePreservingNode()
    {
        using var source = VisionImage.Own(new Mat(100, 100, MatType.CV_8UC1, Scalar.All(80)));
        using var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Require("image.crop"), new ImageCropNode(), "test");
        registry.Register(BuiltInNodeCatalog.Require("image.grayscale"), new GrayscaleNode(), "test");
        registry.Register(BuiltInNodeCatalog.Require("coordinate.transformCompose"), new ComposeTransformNode(), "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        NodeDefinition ConfiguredNode(string id, string type, string json) => new(id, type, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));

        var firstCrop = await dispatcher.ExecuteAsync(ConfiguredNode("crop1", "image.crop",
                """{"x":10,"y":10,"width":80,"height":80}"""), Inputs(source), null, CancellationToken.None);
        using var firstImage = (VisionImage)firstCrop.Outputs["image"].Require<IVisionImage>("image");
        var gray = await dispatcher.ExecuteAsync(ConfiguredNode("gray", "image.grayscale", "{}"), Inputs(firstImage), null, CancellationToken.None);
        using var grayImage = (VisionImage)gray.Outputs["image"].Require<IVisionImage>("image");
        var secondCrop = await dispatcher.ExecuteAsync(ConfiguredNode("crop2", "image.crop",
                """{"x":5,"y":5,"width":60,"height":60}"""), Inputs(grayImage), null, CancellationToken.None);
        using var secondImage = (VisionImage)secondCrop.Outputs["image"].Require<IVisionImage>("image");

        var composed = await dispatcher.ExecuteAsync(ConfiguredNode("compose", "coordinate.transformCompose", "{}"),
            new Dictionary<string, VisionValue>
            {
                ["transformA"] = firstCrop.Outputs["transform"],
                ["transformB"] = secondCrop.Outputs["transform"]
            }, null, CancellationToken.None);
        var transform = composed.Outputs["transform"].Require<VisionTransform2D>("transform");

        Assert.Equal("CurrentImage", transform.SourceFrame);
        Assert.Equal("image:crop2:crop", transform.TargetFrame);
        Assert.Equal(new VisionPoint2D(5, 5), transform.Apply(new VisionPoint2D(20, 20)));
        (firstCrop.Outputs["roiMask"].Value as IDisposable)?.Dispose();
        (secondCrop.Outputs["roiMask"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task RoiCorrectionAndCropTransformsComposeForCaliperMeasurement()
    {
        using var sceneMat = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(20));
        using (var brightSide = new Mat(sceneMat, new Rect(50, 0, 50, 100))) brightSide.SetTo(Scalar.All(220));
        using var scene = VisionImage.Own(sceneMat.Clone());
        var corrected = await new RoiTransformNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("roi.transform", """{"roi":{"type":"Circle","x":40,"y":30,"radius":5},"baseX":0,"baseY":0,"currentX":20,"currentY":10}"""),
            CancellationToken.None);
        var crop = await new ImageCropNode().ExecuteAsync(new NodeExecutionContext(Inputs(scene)),
            Node("image.crop", """{"x":5,"y":7,"width":90,"height":80}"""), CancellationToken.None);
        using var croppedImage = (VisionImage)crop.Outputs["image"].Require<IVisionImage>("image");
        var poseCorrection = corrected.Outputs["transform"].Require<VisionTransform2D>("transform");
        var cropTransform = crop.Outputs["transform"].Require<VisionTransform2D>("transform");

        var composedResult = await new ComposeTransformNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["transformA"] = VisionValue.Transform(poseCorrection),
            ["transformB"] = VisionValue.Transform(cropTransform)
        }), Node("coordinate.transformCompose", "{}"), CancellationToken.None);
        var composed = composedResult.Outputs["transform"].Require<VisionTransform2D>("transform");
        var measurement = await new CaliperNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(croppedImage),
            ["searchLine"] = VisionValue.Line(new(new(0, 20), new(80, 20))),
            ["transform"] = VisionValue.Transform(composed)
        }), Node("measure.caliper", """{"sampleWidth":1,"edgeThreshold":20,"direction":"strongest","refine":"none"}"""),
            CancellationToken.None);

        Assert.True(Convert.ToBoolean(measurement.Summary["found"]));
        var point = measurement.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, 44, 46);
        Assert.Equal(23, point.Y, 6);
        Assert.Equal("ReferenceImage", composed.SourceFrame);
        Assert.Equal("CropImage", composed.TargetFrame);
        (measurement.Outputs["image"].Value as IDisposable)?.Dispose();
        (crop.Outputs["roiMask"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task RoiCorrectionAndCropTransformsRotateCaliperCircleArcIntoCropCoordinates()
    {
        using var sceneMat = new Mat(100, 120, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Ellipse(sceneMat, new Point(70, 30), new Size(15, 15), 0, -90, 90, Scalar.All(220), 2);
        using var scene = VisionImage.Own(sceneMat.Clone());
        var corrected = await new RoiTransformNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>()),
            Node("roi.transform", """{"roi":{"type":"Circle","x":30,"y":30,"radius":15},"baseX":0,"baseY":0,"currentX":40,"currentY":60,"currentThetaDeg":90}"""),
            CancellationToken.None);
        var crop = await new ImageCropNode().ExecuteAsync(new NodeExecutionContext(Inputs(scene)),
            Node("image.crop", """{"x":5,"y":2,"width":110,"height":96}"""), CancellationToken.None);
        using var croppedImage = (VisionImage)crop.Outputs["image"].Require<IVisionImage>("image");
        var composedResult = await new ComposeTransformNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["transformA"] = corrected.Outputs["transform"],
            ["transformB"] = crop.Outputs["transform"]
        }), Node("coordinate.transformCompose", "{}"), CancellationToken.None);

        var measurement = await new CaliperCircleNode().ExecuteAsync(new NodeExecutionContext(new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(croppedImage),
            ["circle"] = VisionValue.Circle(new VisionCircle(30, 30, 15)),
            ["transform"] = composedResult.Outputs["transform"]
        }), Node("measure.caliperCircle", """{"angleStartDeg":0,"angleEndDeg":180,"samplingMode":"count","caliperCount":36,"radialBand":5,"sampleWidth":1,"minContrast":20}"""),
            CancellationToken.None);

        Assert.True(Convert.ToBoolean(measurement.Summary["found"]), Convert.ToString(measurement.Summary["reason"]));
        Assert.True(Convert.ToInt32(measurement.Summary["edgePoints"]) >= 10);
        var circle = measurement.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.InRange(circle.X, 63, 67);
        Assert.InRange(circle.Y, 26, 30);
        Assert.InRange(circle.Radius, 13, 17);
        Assert.Equal("ReferenceImage", composedResult.Outputs["transform"].Require<VisionTransform2D>("transform").SourceFrame);
        Assert.Equal("CropImage", composedResult.Outputs["transform"].Require<VisionTransform2D>("transform").TargetFrame);
        (measurement.Outputs["image"].Value as IDisposable)?.Dispose();
        (crop.Outputs["roiMask"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task Crop_UsesConnectedCorrectedRoiAheadOfLocalSelection()
    {
        using var source = new Mat(60, 60, MatType.CV_8UC1, Scalar.All(90));
        using var image = VisionImage.Own(source.Clone());
        using var registry = new VisionNodeRegistry();
        var roiExecutor = new RoiTransformNode();
        var cropExecutor = new ImageCropNode();
        registry.Register(BuiltInNodeCatalog.Require(roiExecutor.Type), roiExecutor, "test");
        registry.Register(BuiltInNodeCatalog.Require(cropExecutor.Type), cropExecutor, "test");
        var dispatcher = new VisionNodeDispatcher(registry);
        var corrected = await dispatcher.ExecuteAsync(
            Node("roi.transform", """{"roi":{"type":"Rectangle","x":20,"y":10,"width":10,"height":12},"baseX":0,"baseY":0,"currentX":-25,"currentY":7}"""),
            new Dictionary<string, VisionValue>(), null, CancellationToken.None);
        var cropped = await dispatcher.ExecuteAsync(
            Node("image.crop", """{"x":2,"y":3,"width":5,"height":4}"""),
            new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(image), ["roi"] = corrected.Outputs["roi"] },
            null, CancellationToken.None);
        using var cropImage = (VisionImage)cropped.Outputs["image"].Require<IVisionImage>("image");

        Assert.Equal((5, 12), (cropImage.Width, cropImage.Height));
        Assert.Equal("Rectangle", cropped.Summary["roiType"]);
        Assert.Equal(0, Convert.ToInt32(cropped.Summary["cropOriginX"]));
        Assert.Equal(17, Convert.ToInt32(cropped.Summary["cropOriginY"]));
        var transform = cropped.Outputs["transform"].Require<VisionTransform2D>("transform");
        Assert.Equal(new VisionPoint2D(0, 0), transform.Apply(new VisionPoint2D(0, 17)));
        (cropped.Outputs["roiMask"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task CaliperLine_ClipsProfilesAndPreservesExpectedEdgeAngle()
    {
        using var mat = MakeEdgeImage(0.5);
        using var input = VisionImage.Own(mat.Clone());
        var result = await new CaliperLineNode().ExecuteAsync(
            new NodeExecutionContext(Inputs(input)),
            Node("measure.caliperLine", """{"startX":-20,"startY":160,"endX":140,"endY":80,"edgeLength":100,"caliperCount":9,"sampleWidth":1,"minContrast":30}"""),
            CancellationToken.None);

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var fitted = result.Outputs["line"].Require<VisionLine2D>("line");
        var slope = (fitted.End.Y - fitted.Start.Y) / (fitted.End.X - fitted.Start.X);
        Assert.InRange(slope, 1.9, 2.1);
        Assert.True(Convert.ToInt32(result.Summary["profilesClippedToImage"]) > 0);
        Assert.True((result.Overlays?.Count(overlay => overlay.Id.StartsWith("caliper-line-profile-", StringComparison.Ordinal)) ?? 0) >= 3);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
    }

    [Fact]
    public async Task EdgeNodes_ImageOutputsAreDispatcherValidAndInputSurvives()
    {
        using var mat = MakeEdgeImage(1);
        foreach (IVisionNodeExecutor executor in new IVisionNodeExecutor[] { new EdgeSubpixelNode(), new CaliperLineNode(), new CaliperCircleNode() })
        {
            using var input = VisionImage.Own(mat.Clone());
            using var registry = new VisionNodeRegistry();
            registry.Register(BuiltInNodeCatalog.Require(executor.Type), executor, "test");
            var result = await new VisionNodeDispatcher(registry).ExecuteAsync(Node(executor.Type, "{}"), Inputs(input), null, CancellationToken.None);
            Assert.True(result.Outputs["image"].Require<VisionImage>("image").Mat.Rows > 0);
            Assert.True(input.Mat.Rows > 0);
            (result.Outputs["image"].Value as IDisposable)?.Dispose();
        }
    }

    private static Mat MakeEdgeImage(double slope)
    {
        var image = new Mat(128, 128, MatType.CV_8UC1);
        var rows = image.Rows;
        var cols = image.Cols;
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
            image.Set(y, x, x >= 64 + slope * (y - 64) ? (byte)220 : (byte)25);
        return image;
    }

    private static Dictionary<string, VisionValue> Inputs(VisionImage image) => new() { ["image"] = VisionValue.Image(image) };
    private static NodeDefinition Node(string type, string json) => new("n1", type, null, null,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json));
}
