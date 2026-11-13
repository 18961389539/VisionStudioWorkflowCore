using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

public sealed class JlVisionAdvancedIntegrationTests
{
    private static NodeDefinition Node(IVisionNodeExecutor executor, object parameters) => new("advanced", executor.Type, null, null,
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters)));
    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, object parameters, Dictionary<string, VisionValue>? inputs = null) =>
        await executor.ExecuteAsync(new NodeExecutionContext(inputs ?? []), Node(executor, parameters), CancellationToken.None);
    private static void Release(NodeExecutorResult result)
    {
        foreach (var value in result.Outputs.Values.Select(v => v.Value).OfType<IDisposable>().Distinct()) value.Dispose();
    }
    private static VisionRegion Region(int x = 20, int y = 20, int size = 24) => new(96, 96,
        [new(Enumerable.Range(y, size).Select(row => new VisionRegionRun(row, x, x + size - 1)).ToArray())]);
    private static VisionContour Rectangle()
    {
        var points = new List<VisionPoint2D>();
        for (var i = 0; i < 40; i++) points.Add(new(15.25 + i, 20.75));
        for (var i = 0; i < 20; i++) points.Add(new(55.25, 20.75 + i));
        for (var i = 0; i < 40; i++) points.Add(new(55.25 - i, 40.75));
        for (var i = 0; i <= 20; i++) points.Add(new(15.25, 40.75 - i));
        return new(0, points, -1, false, 800, 120, .7, new(15.25, 20.75, 40, 20), new(35.25, 30.75, 23));
    }

    [Theory]
    [InlineData("linesGauss")]
    [InlineData("linesFacet")]
    [InlineData("linesColor")]
    [InlineData("edgesSubPix")]
    [InlineData("edgesColorSubPix")]
    [InlineData("pointsHarris")]
    [InlineData("pointsFoerstner")]
    [InlineData("detectEdgeSegments")]
    public async Task Features_ReturnGeometryOnSyntheticEdges(string operation)
    {
        using var mat = new Mat(96, 96, operation.Contains("Color") ? MatType.CV_8UC3 : MatType.CV_8UC1, Scalar.All(10));
        Cv2.Rectangle(mat, new Rect(20, 20, 50, 40), Scalar.All(230), -1);
        Cv2.Line(mat, new(10, 80), new(80, 80), Scalar.All(240), 3);
        using var image = VisionImage.Own(mat.Clone());
        var result = await Run(new JLVisionFeatureExtractNode(), new { @operator = operation, low = 2, high = 5, threshold = 1, maskSize = 5 }, new() { ["image"] = VisionValue.Image(image) });
        try
        {
            Assert.True(result.Outputs["found"].Require<bool>("found")); Assert.True(result.Outputs["count"].Require<long>("count") > 0);
            var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
            Assert.NotEmpty(result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections"));
            _ = JsonSerializer.Serialize(contours); // Native attributes must also be JSON-safe.
            if (operation == "linesGauss") Assert.Contains(contours, c => c.Attributes?.ContainsKey("width_left") == true);
        }
        finally { Release(result); }
    }

    [Theory]
    [InlineData("watersheds")]
    [InlineData("watershedsMarker")]
    [InlineData("watershedsThreshold")]
    [InlineData("mser")]
    public async Task Segmentation_ReturnsNonemptyRegions(string operation)
    {
        using var mat = new Mat(96, 96, MatType.CV_8UC1, Scalar.All(220));
        Cv2.Circle(mat, new(35, 40), 15, Scalar.All(10), -1); Cv2.Circle(mat, new(65, 60), 12, Scalar.All(30), -1);
        using var image = VisionImage.Own(mat.Clone());
        var result = await Run(new JLVisionSegmentationNode(), new { @operator = operation }, new() { ["image"] = VisionValue.Image(image), ["markers"] = VisionValue.Region(Region(30, 35, 4)) });
        try { Assert.True(result.Outputs["count"].Require<long>("count") > 0); Assert.Equal(96, result.Outputs["region"].Require<VisionRegion>("region").Width); }
        finally { Release(result); }
    }

    [Theory]
    [InlineData("distanceTransform")]
    [InlineData("closestPointTransform")]
    public async Task RegionDistance_UsesNativePixelDistances(string operation)
    {
        var result = await Run(new JLVisionRegionAnalysisNode(), new { @operator = operation, foreground = "false" }, new() { ["region"] = VisionValue.Region(Region()) });
        try
        {
            var image = result.Outputs["image"].Require<VisionImage>("image");
            using var values = new Mat(); image.Mat.ConvertTo(values, MatType.CV_32F);
            Assert.Equal(28, values.At<float>(0, 0)); // Native euclidean distance is quantized to integer pixels. Assert.Equal(0, values.At<float>(30, 30));
            if (operation == "closestPointTransform")
            {
                Assert.InRange(result.Outputs["closestRows"].Require<VisionImage>("closestRows").Mat.At<float>(0, 0), 19.9f, 20.1f);
                Assert.InRange(result.Outputs["closestColumns"].Require<VisionImage>("closestColumns").Mat.At<float>(0, 0), 19.9f, 20.1f);
            }
        }
        finally { Release(result); }
    }

    [Theory]
    [InlineData("spatialRelation")]
    [InlineData("selectRegionSpatial")]
    [InlineData("findNeighbors")]
    public async Task RegionRelations_KeepObjectIndices(string operation)
    {
        var result = await Run(new JLVisionRegionAnalysisNode(), new { @operator = operation, maxDistance = 50, direction = "right" }, new() { ["region"] = VisionValue.Region(Region()), ["region2"] = VisionValue.Region(Region(50, 20, 20)) });
        using var json = JsonDocument.Parse(result.Outputs["relations"].Require<string>("relations"));
        Assert.Equal(0, json.RootElement.GetProperty("indexA")[0].GetDouble());
        Assert.Equal(0, json.RootElement.GetProperty("indexB")[0].GetDouble());
    }

    [Fact]
    public async Task Skeleton_ReturnsEndpointSegments()
    {
        var region = new VisionRegion(96, 96, [new([new(40, 10, 80)])]);
        var result = await Run(new JLVisionRegionAnalysisNode(), new { @operator = "splitSkeletonLines" }, new() { ["region"] = VisionValue.Region(region) });
        Assert.NotEmpty(result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours"));
    }

    [Fact]
    public async Task RectangleFit_RetainsFractionalCenter()
    {
        var result = await Run(new JLVisionFitRectangleNode(), new { }, new() { ["contours"] = VisionValue.Contours([Rectangle()]) });
        var rectangle = result.Outputs["rectangle"].Require<VisionRectangle2D>("rectangle");
        Assert.InRange(rectangle.X, 35.20, 35.30); Assert.InRange(rectangle.Y, 30.70, 30.80);
        Assert.InRange(rectangle.Width, 39.9, 40.1); Assert.InRange(rectangle.Height, 19.9, 20.1);
    }

    [Theory]
    [InlineData("lineCircle", 2)]
    [InlineData("circles", 2)]
    [InlineData("segments", 1)]
    public async Task Intersections_ProduceExpectedPoints(string operation, int expected)
    {
        var result = await Run(new JLVisionIntersectionsNode(), new { @operator = operation }, new()
        {
            ["line"] = VisionValue.Line(new(new(-10, 0), new(10, 0))), ["line2"] = VisionValue.Line(new(new(0, -10), new(0, 10))),
            ["circle"] = VisionValue.Circle(new(0, 0, 5)), ["circle2"] = VisionValue.Circle(new(5, 0, 5))
        });
        Assert.Equal(expected, result.Outputs["count"].Require<long>("count"));
        if (operation == "segments") Assert.Equal(new VisionPoint2D(0, 0), result.Outputs["point"].Require<VisionPoint2D>("point"));
    }

    [Fact]
    public async Task Dxf_RoundTripPreservesFractionalCoordinates_AndProtectsExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".dxf");
        try
        {
            await Run(new JLVisionDxfNode(), new { @operator = "write", path }, new() { ["contours"] = VisionValue.Contours([Rectangle()]) });
            await Assert.ThrowsAsync<IOException>(() => Run(new JLVisionDxfNode(), new { @operator = "write", path }, new() { ["contours"] = VisionValue.Contours([Rectangle()]) }));
            var result = await Run(new JLVisionDxfNode(), new { @operator = "read", path });
            var contours = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
            Assert.NotEmpty(contours); Assert.Contains(contours.SelectMany(c => c.Points), p => Math.Abs(p.X - 15.25) < .001);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ContourOperator_PreservesSubpixelCoordinates()
    {
        var result = await Run(new JLVisionContourOpNode(), new { @operator = "closeContours" }, new() { ["contour"] = VisionValue.Contours([Rectangle()]) });
        try { Assert.Contains(result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours")[0].Points, p => p.X == 15.25 && p.Y == 20.75); }
        finally { Release(result); }
    }

    [Fact]
    public async Task ComposeAndLut_UseIndependentChannelsAndLookupValues()
    {
        using var red = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC1, Scalar.All(10)));
        using var green = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC1, Scalar.All(30)));
        using var blue = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC1, Scalar.All(70)));
        var result = await Run(new JLVisionImageOpNode(), new { @operator = "compose3" }, new() { ["image"] = VisionValue.Image(red), ["image2"] = VisionValue.Image(green), ["image3"] = VisionValue.Image(blue) });
        try { Assert.Equal(new Vec3b(70, 30, 10), result.Outputs["image"].Require<VisionImage>("image").Mat.At<Vec3b>(0, 0)); }
        finally { Release(result); }
        var lut = await Run(new JLVisionImageOpNode(), new { @operator = "lutTrans", lutJson = JsonSerializer.Serialize(Enumerable.Range(0, 256).Select(i => 255 - i)) }, new() { ["image"] = VisionValue.Image(red) });
        try { Assert.Equal(245, lut.Outputs["image"].Require<VisionImage>("image").Mat.At<byte>(0, 0)); }
        finally { Release(lut); }
    }

    [Theory]
    [InlineData("harmonic")]
    [InlineData("inpaintingCt")]
    [InlineData("inpaintingMcf")]
    [InlineData("inpaintingCed")]
    [InlineData("inpaintingAniso")]
    public async Task Inpainting_ExecutesOnDefinedMask(string operation)
    {
        using var image = VisionImage.Own(new Mat(96, 96, MatType.CV_8UC1, Scalar.All(100)));
        var result = await Run(new JLVisionRestorationNode(), new { @operator = operation, iterations = 2 }, new() { ["image"] = VisionValue.Image(image), ["region"] = VisionValue.Region(Region(40, 40, 4)) });
        try { var output = result.Outputs["image"].Require<VisionImage>("image"); Assert.Equal(96, output.Width); Assert.Equal(96, output.Height); }
        finally { Release(result); }
    }

    [Theory]
    [InlineData("psfMotion")]
    [InlineData("psfDefocus")]
    public async Task Psf_HasFiniteNormalizedEnergy(string operation)
    {
        var result = await Run(new JLVisionRestorationNode(), new { @operator = operation });
        try { var image = result.Outputs["image"].Require<VisionImage>("image"); Assert.InRange(Cv2.Sum(image.Mat).Val0, .99, 1.01); }
        finally { Release(result); }
    }

    [Theory]
    [InlineData("image")]
    [InlineData("map")]
    [InlineData("contour")]
    public async Task WorldPlane_UsesCameraAndPlaneCalibration(string operation)
    {
        using var image = VisionImage.Own(new Mat(96, 96, MatType.CV_8UC1, Scalar.All(100)));
        var camera = JsonSerializer.Serialize(new object[] { "area_scan_polynomial", .006, 0, 0, 0, 0, 0, .00001, .00001, 48, 48, 96, 96 });
        var result = await Run(new JLVisionWorldPlaneNode(), new { @operator = operation, cameraParameters = camera, worldPoseJson = "[0,0,0.1,0,0,0,0]", width = 32, height = 32 },
            new() { ["image"] = VisionValue.Image(image), ["contours"] = VisionValue.Contours([Rectangle()]) });
        try
        {
            Assert.Equal("mm", result.Outputs["unit"].Require<string>("unit"));
            if (operation == "contour")
            {
                var contour = result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours")[0];
                Assert.Equal("mm", contour.Unit); Assert.Equal((15.25 - 48) / 6, contour.Points[0].X, 5);
                Assert.Equal((20.75 - 48) / 6, contour.Points[0].Y, 5);
            }
            else Assert.Equal(32, result.Outputs[operation == "map" ? "mapRows" : "image"].Require<VisionImage>("image").Width);
        }
        finally { Release(result); }
    }

    [Theory]
    [InlineData("sfsPentland")]
    [InlineData("sfsOrigLr")]
    [InlineData("sfsModLr")]
    [InlineData("shadeHeightField")]
    [InlineData("reconstructGradient")]
    public async Task Surface_ReturnsFiniteHeightField(string operation)
    {
        using var image = VisionImage.Own(new Mat(32, 32, operation.StartsWith("sfs") ? MatType.CV_8UC1 : MatType.CV_32FC1, Scalar.All(operation.StartsWith("sfs") ? 128 : .5)));
        using var columns = VisionImage.Own(new Mat(32, 32, MatType.CV_32FC1, Scalar.All(0)));
        var result = await Run(new JLVisionSurfaceNode(), new { @operator = operation, method = "poisson" }, new() { ["image"] = VisionValue.Image(image), ["gradientColumns"] = VisionValue.Image(columns) });
        try
        {
            var output = result.Outputs["image"].Require<VisionImage>("image");
            Assert.Equal(32, output.Width); Assert.True(Cv2.CheckRange(output.Mat));
        }
        finally { Release(result); }
    }

    [Theory]
    [InlineData("wiener")]
    [InlineData("wienerNi")]
    public async Task Wiener_RestoresInputWithMatchingPsf(string operation)
    {
        using var image = VisionImage.Own(new Mat(96, 96, MatType.CV_8UC1, Scalar.All(100)));
        Cv2.Rectangle(image.Mat, new Rect(10, 10, 20, 30), Scalar.All(150), -1);
        using var smooth = VisionImage.Own(image.Mat.Clone());
        var psf = await Run(new JLVisionRestorationNode(), new { @operator = "psfDefocus", width = 96, height = 96 });
        try
        {
            var result = await Run(new JLVisionRestorationNode(), new { @operator = operation }, new()
            {
                ["image"] = VisionValue.Image(image), ["filteredImage"] = VisionValue.Image(smooth), ["region"] = VisionValue.Region(Region(50, 50, 10)), ["psf"] = psf.Outputs["image"]
            });
            try { Assert.Equal(96, result.Outputs["image"].Require<VisionImage>("image").Width); Assert.True(Cv2.CheckRange(result.Outputs["image"].Require<VisionImage>("image").Mat)); }
            finally { Release(result); }
        }
        finally { Release(psf); }
    }

    [Fact]
    public async Task WorldMap_ApplyMatchesDirectImageProjection()
    {
        using var image = VisionImage.Own(new Mat(96, 96, MatType.CV_8UC1, Scalar.All(100)));
        Cv2.Rectangle(image.Mat, new Rect(52, 52, 20, 20), Scalar.All(200), -1);
        var camera = JsonSerializer.Serialize(new object[] { "area_scan_polynomial", .006, 0, 0, 0, 0, 0, .00001, .00001, 48, 48, 96, 96 });
        var settings = new Dictionary<string, object> { ["operator"] = "map", ["cameraParameters"] = camera, ["worldPoseJson"] = "[0,0,0.1,0,0,0,0]", ["width"] = 32, ["height"] = 32 };
        var map = await Run(new JLVisionWorldPlaneNode(), settings, new() { ["image"] = VisionValue.Image(image) });
        try
        {
            var applied = await Run(new JLVisionWorldPlaneNode(), new { @operator = "applyMap" }, new() { ["image"] = VisionValue.Image(image), ["mapRows"] = map.Outputs["mapRows"], ["mapColumns"] = map.Outputs["mapColumns"], ["mapInfo"] = map.Outputs["mapInfo"] });
            settings["operator"] = "image";
            var direct = await Run(new JLVisionWorldPlaneNode(), settings, new() { ["image"] = VisionValue.Image(image) });
            try { Assert.InRange(Cv2.Norm(applied.Outputs["image"].Require<VisionImage>("image").Mat, direct.Outputs["image"].Require<VisionImage>("image").Mat, NormTypes.INF), 0, 1); }
            finally { Release(applied); Release(direct); }
        }
        finally { Release(map); }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task MotionPsf_SupportsAllVelocityProfiles(int type)
    {
        var result = await Run(new JLVisionRestorationNode(), new { @operator = "psfMotion", psfType = type });
        try { Assert.InRange(Cv2.Sum(result.Outputs["image"].Require<VisionImage>("image").Mat).Val0, .999, 1.001); }
        finally { Release(result); }
    }

    [Fact]
    public async Task ContourProperties_DoNotRoundSubpixelCentroid()
    {
        var result = await Run(new ContourPropertiesNode(), new { }, new() { ["contours"] = VisionValue.Contours([Rectangle()]) });
        var properties = result.Outputs["properties"].Require<IReadOnlyList<VisionContourProperties>>("properties");
        Assert.Equal(35.25, properties[0].Centroid.X, 8); Assert.Equal(30.75, properties[0].Centroid.Y, 8);
    }

    [Fact]
    public async Task InvalidLookupTableAndMissingThirdChannel_AreRejected()
    {
        using var image = VisionImage.Own(new Mat(8, 8, MatType.CV_8UC1, Scalar.All(10)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new JLVisionImageOpNode(), new { @operator = "lutTrans", lutJson = "[1,2,3]" }, new() { ["image"] = VisionValue.Image(image) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new JLVisionImageOpNode(), new { @operator = "compose3" }, new() { ["image"] = VisionValue.Image(image), ["image2"] = VisionValue.Image(image) }));
    }

    [Theory]
    [InlineData("geometry.intersections", "lineCircle", "line")]
    [InlineData("geometry.intersections", "circles", "circle")]
    [InlineData("jlvision.dxf", "write", "contours")]
    [InlineData("jlvision.worldPlane", "contour", "contours")]
    [InlineData("jlvision.restoration", "wiener", "image")]
    public void Compiler_RejectsMissingOperationSpecificInputs(string type, string operation, string required)
    {
        var registry = new VisionNodeRegistry();
        IVisionNodeExecutor executor = type switch { "geometry.intersections" => new JLVisionIntersectionsNode(), "jlvision.dxf" => new JLVisionDxfNode(), "jlvision.worldPlane" => new JLVisionWorldPlaneNode(), _ => new JLVisionRestorationNode() };
        registry.Register(BuiltInNodeCatalog.Items.Single(i => i.Type == type), executor, "test");
        var definition = Node(executor, new { @operator = operation });
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(registry).Compile(new("required", "required", [definition], [])));
        Assert.Contains($"advanced.{required}", error.Message);
    }

    [Fact]
    public void PsfGenerator_CanCompileWithoutImageInput()
    {
        var executor = new JLVisionRestorationNode(); var registry = new VisionNodeRegistry();
        registry.Register(BuiltInNodeCatalog.Items.Single(i => i.Type == executor.Type), executor, "test");
        Assert.NotNull(new VisionWorkflowCompiler(registry).Compile(new("psf", "psf", [Node(executor, new { @operator = "psfMotion" })], [])));
    }
}

