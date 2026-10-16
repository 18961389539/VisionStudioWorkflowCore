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

    [Theory]
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
            "measure.caliper" => new CaliperNode(),
            "measure.caliperLine" => new CaliperLineNode(),
            _ => new CaliperCircleNode()
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            new NodeExecutionContext(inputs), Node(type, "{}"), CancellationToken.None).AsTask());

        Assert.Contains("Polygon ROI requires 3..500000 finite vertices", error.Message);
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
        Assert.Equal("profile-has-no-finite-samples", result.Summary["reason"]);
        (result.Outputs["image"].Value as IDisposable)?.Dispose();
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
