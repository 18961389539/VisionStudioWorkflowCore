using System.Text.Json;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// Compile-time parameter validation: catalog-declared parameters with wrong types, out-of-range
/// numbers or invalid select options must be rejected with node id + parameter name + reason.
/// </summary>
public sealed class ParameterValidationTests
{
    [Theory]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":"3"}""", "expects a number")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":null}""", "received null")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":-1}""", "below the minimum 0")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":1000000}""", "above the maximum 999999")]
    [InlineData("calibration.planar", "targetUnit", """{"targetUnit":"furlong"}""", "not an allowed option")]
    [InlineData("calibration.planar", "targetUnit", """{"targetUnit":7}""", "expects one of [mm, cm, m]")]
    [InlineData("calibration.planar", "targetFrame", """{"targetFrame":42}""", "expects text")]
    [InlineData("image.acquire", "autoStart", """{"autoStart":"true"}""", "expects a boolean")]
    [InlineData("image.acquire", "timeoutMs", """{"timeoutMs":{"value":100}}""", "expects a number")]
    public void InvalidParameter_IsRejectedWithNodeIdAndParameterName(
        string nodeType,
        string parameterName,
        string parametersJson,
        string reasonFragment)
    {
        var compiler = CreateCompiler();

        var error = Assert.Throws<InvalidOperationException>(
            () => compiler.Compile(CreateWorkflow(nodeType, parametersJson)));

        Assert.Contains($"Node 'n1' parameter '{parameterName}'", error.Message);
        Assert.Contains(reasonFragment, error.Message);
    }

    [Theory]
    [InlineData("calibration.planar", """{"targetUnit":"MM","assetVersion":0,"pairsJson":"[]","targetFrame":"Workpiece"}""")]
    [InlineData("calibration.planar", """{"assetVersion":999999}""")]
    [InlineData("calibration.planar", """{"assetVersion":3.5}""")]
    [InlineData("calibration.planar", "{}")]
    [InlineData("calibration.planar", "null")]
    // Undeclared keys must stay ignored: roi is consumed by VisionRoiParameterExtensions, not by the catalog.
    [InlineData("calibration.planar", """{"roi":{"type":"Rectangle","x":1,"y":2,"width":3,"height":4},"customKey":7}""")]
    [InlineData("image.acquire", """{"frameMode":"next","timeoutMs":1500,"autoStart":true}""")]
    [InlineData("image.acquire", """{"timeoutMs":50}""")]
    [InlineData("image.acquire", """{"timeoutMs":10000}""")]
    [InlineData("roi.transform", """{"baseX":120000,"baseY":80000,"currentX":160000,"currentY":80000}""")]
    public void AcceptedParameterValues_Compile(string nodeType, string parametersJson)
    {
        var compiler = CreateCompiler();

        var compiled = compiler.Compile(CreateWorkflow(nodeType, parametersJson));

        Assert.NotEmpty(compiled.DslJson);
        Assert.Contains("n1", compiled.OrderedNodeIds);
    }

    [Theory]
    [InlineData("measure.caliper", """{"startX":120000,"startY":80000,"endX":160000,"endY":80000}""")]
    [InlineData("feature.edgeSubpixel", """{"startX":120000,"startY":80000,"endX":160000,"endY":80000}""")]
    [InlineData("measure.caliperLine", """{"startX":120000,"startY":80000,"endX":160000,"endY":80000}""")]
    [InlineData("measure.caliperCircle", """{"centerX":120000,"centerY":80000,"radius":80000}""")]
    public void HighResolutionCaliperGeometry_Compiles(string nodeType, string parametersJson)
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("n1", nodeType, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson));
        var workflow = new WorkflowDefinition("high-resolution-caliper", "High resolution caliper", [source, target],
        [
            new EdgeDefinition("control", "source", "next", "n1", "exec", "control"),
            new EdgeDefinition("image", "source", "image", "n1", "image", "data")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.Contains("n1", compiled.OrderedNodeIds);
    }

    [Fact]
    public void CaliperCircleRejectsASecondTransformOnAnAlreadyCorrectedRoiCircle()
    {
        var workflow = new WorkflowDefinition("circle-frame-conflict", "Circle frame conflict",
        [
            new NodeDefinition("image", "image.synthetic", null, null, null),
            new NodeDefinition("corrected-roi", "roi.transform", null, null, null),
            new NodeDefinition("caliper", "measure.caliperCircle", null, null, null)
        ],
        [
            new EdgeDefinition("image-input", "image", "image", "caliper", "image", "data"),
            new EdgeDefinition("corrected-circle", "corrected-roi", "circle", "caliper", "circle", "data"),
            new EdgeDefinition("duplicate-transform", "corrected-roi", "transform", "caliper", "transform", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("already corrected circle", error.Message);
        Assert.Contains("Remove that transform input", error.Message);
    }

    [Fact]
    public void CaliperCircleAllowsCropTransformAfterReceivingACorrectedRoiCircle()
    {
        var workflow = new WorkflowDefinition("circle-crop-frame", "Circle crop frame",
        [
            new NodeDefinition("image", "image.synthetic", null, null, null),
            new NodeDefinition("corrected-roi", "roi.transform", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null),
            new NodeDefinition("caliper", "measure.caliperCircle", null, null, null)
        ],
        [
            new EdgeDefinition("start-roi", "image", "next", "corrected-roi", "exec", "control"),
            new EdgeDefinition("roi-crop", "corrected-roi", "next", "crop", "exec", "control"),
            new EdgeDefinition("crop-caliper", "crop", "next", "caliper", "exec", "control"),
            new EdgeDefinition("crop-image", "image", "image", "crop", "image", "data"),
            new EdgeDefinition("circle-image", "crop", "image", "caliper", "image", "data"),
            new EdgeDefinition("corrected-circle", "corrected-roi", "circle", "caliper", "circle", "data"),
            new EdgeDefinition("crop-transform", "crop", "transform", "caliper", "transform", "data")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.Contains("caliper", compiled.OrderedNodeIds);
    }

    [Theory]
    [InlineData("feature.line", "line", "measure.caliperLine", "searchLine")]
    [InlineData("feature.circle", "circle", "measure.caliperCircle", "circle")]
    public void MeasurementRejectsGeometryAlreadyInTheTransformTargetFrame(
        string geometryType,
        string geometryOutput,
        string measurementType,
        string measurementInput)
    {
        var workflow = new WorkflowDefinition("geometry-frame-conflict", "Geometry frame conflict",
        [
            new NodeDefinition("image", "image.synthetic", null, null, null),
            new NodeDefinition("pose", "roi.transform", null, null, null),
            new NodeDefinition("geometry", geometryType, null, null, null),
            new NodeDefinition("measure", measurementType, null, null, null)
        ],
        [
            new EdgeDefinition("image-pose-control", "image", "next", "pose", "exec", "control"),
            new EdgeDefinition("pose-geometry-control", "pose", "next", "geometry", "exec", "control"),
            new EdgeDefinition("geometry-measure-control", "geometry", "next", "measure", "exec", "control"),
            new EdgeDefinition("pose-image", "image", "image", "pose", "debugImage", "data"),
            new EdgeDefinition("geometry-image", "image", "image", "geometry", "image", "data"),
            new EdgeDefinition("measurement-image", "image", "image", "measure", "image", "data"),
            new EdgeDefinition("geometry-input", "geometry", geometryOutput, "measure", measurementInput, "data"),
            new EdgeDefinition("pose-transform", "pose", "transform", "measure", "transform", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("already in transform frame 'image.image'", error.Message);
        Assert.Contains("Remove the transform input", error.Message);
    }

    [Fact]
    public void MeasurementAcceptsSourceFrameGeometryWithCropTransform()
    {
        var workflow = new WorkflowDefinition("source-geometry-crop", "Source geometry with crop transform",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null),
            new NodeDefinition("line", "feature.line", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("scene-crop-control", "scene", "next", "crop", "exec", "control"),
            new EdgeDefinition("crop-line-control", "crop", "next", "line", "exec", "control"),
            new EdgeDefinition("line-measure-control", "line", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-crop", "scene", "image", "crop", "image", "data"),
            new EdgeDefinition("scene-line", "scene", "image", "line", "image", "data"),
            new EdgeDefinition("crop-measure", "crop", "image", "measure", "image", "data"),
            new EdgeDefinition("line-measure", "line", "line", "measure", "searchLine", "data"),
            new EdgeDefinition("crop-transform", "crop", "transform", "measure", "transform", "data")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.Contains("measure", compiled.OrderedNodeIds);
    }

    [Theory]
    [InlineData("feature.edgeSubpixel")]
    [InlineData("measure.caliper")]
    [InlineData("measure.caliperLine")]
    [InlineData("measure.caliperCircle")]
    public void MeasurementRejectsCropTransformWhenSamplingTheUncroppedImage(string measurementType)
    {
        var workflow = new WorkflowDefinition("mismatched-crop-frame", "Mismatched crop frame",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null),
            new NodeDefinition("measure", measurementType, null, null, null)
        ],
        [
            new EdgeDefinition("scene-crop-control", "scene", "next", "crop", "exec", "control"),
            new EdgeDefinition("crop-measure-control", "crop", "next", "measure", "exec", "control"),
            new EdgeDefinition("crop-image", "scene", "image", "crop", "image", "data"),
            new EdgeDefinition("measurement-image", "scene", "image", "measure", "image", "data"),
            new EdgeDefinition("crop-transform", "crop", "transform", "measure", "transform", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("samples image frame 'scene.image'", error.Message);
        Assert.Contains("transform targets 'crop.image (crop frame)'", error.Message);
        Assert.Contains("compose", error.Message);
    }

    [Fact]
    public void MeasurementAcceptsImagePreservingNodesAfterTheTransformTarget()
    {
        var workflow = new WorkflowDefinition("crop-grayscale-measurement", "Crop grayscale measurement",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null),
            new NodeDefinition("gray", "image.grayscale", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("scene-crop-control", "scene", "next", "crop", "exec", "control"),
            new EdgeDefinition("crop-gray-control", "crop", "next", "gray", "exec", "control"),
            new EdgeDefinition("gray-measure-control", "gray", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-crop", "scene", "image", "crop", "image", "data"),
            new EdgeDefinition("crop-gray", "crop", "image", "gray", "image", "data"),
            new EdgeDefinition("gray-measure", "gray", "image", "measure", "image", "data"),
            new EdgeDefinition("crop-transform", "crop", "transform", "measure", "transform", "data")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.Contains("measure", compiled.OrderedNodeIds);
    }

    [Fact]
    public void AffineWarpPropagatesRegistrationTargetFrameToDownstreamMeasurement()
    {
        var workflow = new WorkflowDefinition("registered-warp-measurement", "Registered warp measurement",
        [
            new NodeDefinition("current", "image.synthetic", null, null, null),
            new NodeDefinition("reference", "image.acquire", null, null, null),
            new NodeDefinition("registration", "image.register", null, null, null),
            new NodeDefinition("warp", "image.warpAffine", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("current-reference-control", "current", "next", "reference", "exec", "control"),
            new EdgeDefinition("reference-registration-control", "reference", "next", "registration", "exec", "control"),
            new EdgeDefinition("registration-warp-control", "registration", "next", "warp", "exec", "control"),
            new EdgeDefinition("warp-measure-control", "warp", "next", "measure", "exec", "control"),
            new EdgeDefinition("current-registration-image", "current", "image", "registration", "image", "data"),
            new EdgeDefinition("reference-registration-image", "reference", "image", "registration", "reference", "data"),
            new EdgeDefinition("current-warp-image", "current", "image", "warp", "image", "data"),
            new EdgeDefinition("registration-warp-transform", "registration", "transform", "warp", "transform", "data"),
            new EdgeDefinition("warp-measure-image", "warp", "image", "measure", "image", "data"),
            new EdgeDefinition("registration-measure-transform", "registration", "transform", "measure", "transform", "data")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.Contains("measure", compiled.OrderedNodeIds);
    }

    [Fact]
    public void AffineWarpRejectsImageFromDifferentSourceFrame()
    {
        var workflow = new WorkflowDefinition("wrong-warp-source", "Wrong affine warp source",
        [
            new NodeDefinition("current", "image.synthetic", null, null, null),
            new NodeDefinition("other", "image.acquire", null, null, null),
            new NodeDefinition("reference", "image.acquire", null, null, null),
            new NodeDefinition("registration", "image.register", null, null, null),
            new NodeDefinition("warp", "image.warpAffine", null, null, null)
        ],
        [
            new EdgeDefinition("current-other-control", "current", "next", "other", "exec", "control"),
            new EdgeDefinition("other-reference-control", "other", "next", "reference", "exec", "control"),
            new EdgeDefinition("reference-registration-control", "reference", "next", "registration", "exec", "control"),
            new EdgeDefinition("registration-warp-control", "registration", "next", "warp", "exec", "control"),
            new EdgeDefinition("current-registration-image", "current", "image", "registration", "image", "data"),
            new EdgeDefinition("reference-registration-image", "reference", "image", "registration", "reference", "data"),
            new EdgeDefinition("other-warp-image", "other", "image", "warp", "image", "data"),
            new EdgeDefinition("registration-warp-transform", "registration", "transform", "warp", "transform", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("warps image frame 'other.image'", error.Message);
        Assert.Contains("transform expects source frame 'current.image'", error.Message);
    }

    [Fact]
    public void PoseTransformMustTargetTheSameImageFrameTheCaliperSamples()
    {
        WorkflowDefinition BuildWorkflow(string measurementImageSource) => new(
            "pose-measurement-frame", "Pose measurement frame",
            [
                new NodeDefinition("scene-a", "image.synthetic", null, null, null),
                new NodeDefinition("scene-b", "image.acquire", null, null, null),
                new NodeDefinition("pose", "roi.transform", null, null, null),
                new NodeDefinition("measure", "measure.caliperLine", null, null, null)
            ],
            [
                new EdgeDefinition("scene-a-control", "scene-a", "next", "scene-b", "exec", "control"),
                new EdgeDefinition("scene-b-control", "scene-b", "next", "pose", "exec", "control"),
                new EdgeDefinition("pose-control", "pose", "next", "measure", "exec", "control"),
                new EdgeDefinition("pose-reference-image", "scene-a", "image", "pose", "debugImage", "data"),
                new EdgeDefinition("measurement-image", measurementImageSource, "image", "measure", "image", "data"),
                new EdgeDefinition("pose-transform", "pose", "transform", "measure", "transform", "data")
            ]);

        var valid = CreateCompiler().Compile(BuildWorkflow("scene-a"));
        Assert.Contains("measure", valid.OrderedNodeIds);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(BuildWorkflow("scene-b")));
        Assert.Contains("samples image frame 'scene-b.image'", error.Message);
        Assert.Contains("transform targets 'scene-a.image'", error.Message);
    }

    [Fact]
    public void PoseAndCropTransformsRejectDifferentIntermediateImageFrames()
    {
        var workflow = new WorkflowDefinition("mismatched-compose-frame", "Mismatched transform composition",
        [
            new NodeDefinition("scene-a", "image.synthetic", null, null, null),
            new NodeDefinition("scene-b", "image.acquire", null, null, null),
            new NodeDefinition("pose", "roi.transform", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null),
            new NodeDefinition("compose", "coordinate.transformCompose", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("scene-a-control", "scene-a", "next", "scene-b", "exec", "control"),
            new EdgeDefinition("scene-b-control", "scene-b", "next", "pose", "exec", "control"),
            new EdgeDefinition("pose-control", "pose", "next", "crop", "exec", "control"),
            new EdgeDefinition("crop-control", "crop", "next", "compose", "exec", "control"),
            new EdgeDefinition("compose-control", "compose", "next", "measure", "exec", "control"),
            new EdgeDefinition("pose-reference-image", "scene-a", "image", "pose", "debugImage", "data"),
            new EdgeDefinition("crop-input-image", "scene-b", "image", "crop", "image", "data"),
            new EdgeDefinition("pose-crop-transform", "pose", "transform", "compose", "transformA", "data"),
            new EdgeDefinition("crop-compose-transform", "crop", "transform", "compose", "transformB", "data"),
            new EdgeDefinition("measurement-image", "crop", "image", "measure", "image", "data"),
            new EdgeDefinition("composed-transform", "compose", "transform", "measure", "transform", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("cannot compose transforms", error.Message);
        Assert.Contains("scene-a.image", error.Message);
        Assert.Contains("scene-b.image", error.Message);
    }

    [Fact]
    public void ComposedPoseAndCropTransformMustTargetTheMeasuredImageFrame()
    {
        WorkflowDefinition BuildWorkflow(string measurementImageSource) => new(
            "composed-measurement-frame", "Composed measurement frame",
            [
                new NodeDefinition("scene", "image.synthetic", null, null, null),
                new NodeDefinition("pose", "roi.transform", null, null, null),
                new NodeDefinition("crop", "image.crop", null, null, null),
                new NodeDefinition("compose", "coordinate.transformCompose", null, null, null),
                new NodeDefinition("measure", "measure.caliperLine", null, null, null)
            ],
            [
                new EdgeDefinition("scene-pose-control", "scene", "next", "pose", "exec", "control"),
                new EdgeDefinition("pose-crop-control", "pose", "next", "crop", "exec", "control"),
                new EdgeDefinition("crop-compose-control", "crop", "next", "compose", "exec", "control"),
                new EdgeDefinition("compose-measure-control", "compose", "next", "measure", "exec", "control"),
                new EdgeDefinition("scene-pose", "scene", "image", "pose", "debugImage", "data"),
                new EdgeDefinition("scene-crop", "scene", "image", "crop", "image", "data"),
                new EdgeDefinition("pose-crop-transform", "pose", "transform", "compose", "transformA", "data"),
                new EdgeDefinition("crop-compose-transform", "crop", "transform", "compose", "transformB", "data"),
                new EdgeDefinition("measured-image", measurementImageSource, "image", "measure", "image", "data"),
                new EdgeDefinition("composed-transform", "compose", "transform", "measure", "transform", "data")
            ]);

        var valid = CreateCompiler().Compile(BuildWorkflow("crop"));
        Assert.Contains("measure", valid.OrderedNodeIds);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(BuildWorkflow("scene")));
        Assert.Contains("transform targets 'crop.image (crop frame)'", error.Message);
    }

    [Theory]
    [InlineData("roi.transform", "roi", """{"type":"Ellipse"}""", "known string type")]
    [InlineData("match.shape", "searchRoi", """{"type":"Ellipse"}""", "known string type")]
    [InlineData("match.shape", "clutterRoi", "[]", "must be an object or null")]
    public void MalformedRoiParametersAreRejectedDuringCompilation(string nodeType, string parameterName, string roiJson, string message)
    {
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            $"{{\"{parameterName}\":{roiJson}}}");
        var node = new NodeDefinition("n1", nodeType, null, null, parameters);
        WorkflowDefinition workflow;
        if (nodeType.Equals("match.shape", StringComparison.OrdinalIgnoreCase))
        {
            var source = new NodeDefinition("source", "image.synthetic", null, null, null);
            workflow = new WorkflowDefinition("malformed-roi", "Malformed ROI", [source, node],
            [
                new EdgeDefinition("start", "source", "next", "n1", "exec", "control"),
                new EdgeDefinition("image", "source", "image", "n1", "image", "data")
            ]);
        }
        else workflow = new WorkflowDefinition("malformed-roi", "Malformed ROI", [node], []);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("Node 'n1'", error.Message);
        Assert.Contains($"ROI parameter '{parameterName}'", error.Message);
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("image.crop")]
    [InlineData("contour.extract")]
    [InlineData("feature.edgeSubpixel")]
    [InlineData("measure.caliper")]
    [InlineData("measure.caliperLine")]
    [InlineData("measure.caliperCircle")]
    public void MalformedLocalRoiIsIgnoredWhenConnectedRoiInputOverridesIt(string targetType)
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var roi = new NodeDefinition("roi", "roi.transform", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"roi":{"type":"Rectangle","x":2,"y":3,"width":20,"height":15}}"""));
        var target = new NodeDefinition("target", targetType, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"roi":{"type":"Rectangle","x":0,"y":0,"width":0,"height":10}}"""));
        var workflow = new WorkflowDefinition("roi-input-overrides-local", "ROI input override",
            [source, roi, target],
            [
                new EdgeDefinition("start-roi", "source", "next", "roi", "exec", "control"),
                new EdgeDefinition("next-target", "roi", "next", "target", "exec", "control"),
                new EdgeDefinition("target-image", "source", "image", "target", "image", "data"),
                new EdgeDefinition("target-roi", "roi", "roi", "target", "roi", "data")
            ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.NotEmpty(compiled.DslJson);
    }

    [Fact]
    public void RoiTransformCanCaptureReferenceEvenWhenItsOldLocalRoiIsMalformed()
    {
        var node = new NodeDefinition("roi", "roi.transform", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"captureReference":true,"roi":{"type":"Ellipse"}}"""));

        var compiled = CreateCompiler().Compile(new WorkflowDefinition("capture-reference", "Capture reference", [node], []));

        Assert.NotEmpty(compiled.DslJson);
    }

    [Theory]
    [InlineData("feature.edgeSubpixel")]
    [InlineData("measure.caliper")]
    [InlineData("measure.caliperLine")]
    public void MalformedLocalSearchLineIsIgnoredWhenConnectedSearchLineOverridesIt(string targetType)
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var line = new NodeDefinition("line", "feature.line", null, null, null);
        var target = new NodeDefinition("target", targetType, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"startX":"inactive","startY":20,"endX":40,"endY":null}"""));
        var workflow = new WorkflowDefinition("search-line-input-overrides-local", "Search line input override",
            [source, line, target],
            [
                new EdgeDefinition("start-line", "source", "next", "line", "exec", "control"),
                new EdgeDefinition("next-target", "line", "next", "target", "exec", "control"),
                new EdgeDefinition("line-image", "source", "image", "line", "image", "data"),
                new EdgeDefinition("target-image", "source", "image", "target", "image", "data"),
                new EdgeDefinition("target-search-line", "line", "line", "target", "searchLine", "data")
            ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.NotEmpty(compiled.DslJson);
    }

    [Fact]
    public void MalformedLocalCircleIsIgnoredWhenConnectedCircleOverridesIt()
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var roi = new NodeDefinition("roi", "roi.transform", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"roi":{"type":"Circle","x":32,"y":24,"radius":12}}"""));
        var target = new NodeDefinition("target", "measure.caliperCircle", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"centerX":"inactive","centerY":24,"radius":0}"""));
        var workflow = new WorkflowDefinition("circle-input-overrides-local", "Circle input override",
            [source, roi, target],
            [
                new EdgeDefinition("start-roi", "source", "next", "roi", "exec", "control"),
                new EdgeDefinition("next-target", "roi", "next", "target", "exec", "control"),
                new EdgeDefinition("target-image", "source", "image", "target", "image", "data"),
                new EdgeDefinition("target-circle", "roi", "circle", "target", "circle", "data")
            ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.NotEmpty(compiled.DslJson);
    }

    [Theory]
    [InlineData("feature.edgeSubpixel")]
    [InlineData("measure.caliper")]
    [InlineData("measure.caliperLine")]
    public void SearchLineShorterThanTwoPixelsIsRejectedDuringCompilation(string nodeType)
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("target", nodeType, null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"startX":20,"startY":30,"endX":21.99,"endY":30}"""));
        var workflow = new WorkflowDefinition("short-search-line", "Short search line", [source, target],
            [new EdgeDefinition("image", "source", "image", "target", "image", "data")]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("search line must be at least 2 px", error.Message);
        Assert.Contains("Adjust its start/end coordinates", error.Message);
    }

    [Fact]
    public void SearchLineExactlyTwoPixelsLongCompiles()
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("target", "measure.caliperLine", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"startX":20,"startY":30,"endX":22,"endY":30}"""));
        var workflow = new WorkflowDefinition("minimum-search-line", "Minimum search line", [source, target],
            [new EdgeDefinition("image", "source", "image", "target", "image", "data")]);

        Assert.NotEmpty(CreateCompiler().Compile(workflow).DslJson);
    }

    [Fact]
    public void CaliperCircleRejectsAnArcWithEqualStartAndEndAnglesDuringCompilation()
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("target", "measure.caliperCircle", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"angleStartDeg":30,"angleEndDeg":30}"""));
        var workflow = new WorkflowDefinition("zero-span-circle-arc", "Zero span circle arc", [source, target],
            [new EdgeDefinition("image", "source", "image", "target", "image", "data")]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("only one radial caliper", error.Message);
        Assert.Contains("Widen the arc angle range", error.Message);
    }

    [Theory]
    [InlineData(350, 10)]
    [InlineData(0, 360)]
    public void CaliperCircleAllowsWraparoundAndFullCircleArcs(double start, double end)
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("target", "measure.caliperCircle", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { angleStartDeg = start, angleEndDeg = end })));
        var workflow = new WorkflowDefinition("valid-circle-arc", "Valid circle arc", [source, target],
            [new EdgeDefinition("image", "source", "image", "target", "image", "data")]);

        Assert.NotEmpty(CreateCompiler().Compile(workflow).DslJson);
    }

    [Fact]
    public void CaliperLineRejectsMinimumValidCountAboveConfiguredCount()
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("target", "measure.caliperLine", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"caliperCount":5,"minValidCalipers":6}"""));
        var workflow = new WorkflowDefinition("caliper-line-impossible-gate", "Caliper line impossible gate", [source, target],
            [new EdgeDefinition("image", "source", "image", "target", "image", "data")]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("requires 6 valid calipers, but caliperCount is 5", error.Message);
        Assert.Contains("Lower minValidCalipers or increase caliperCount", error.Message);
    }

    [Fact]
    public void CaliperCircleRejectsMinimumValidCountAboveCountSampling()
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("target", "measure.caliperCircle", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"caliperCount":5,"minValidCalipers":6}"""));
        var workflow = new WorkflowDefinition("caliper-circle-impossible-gate", "Caliper circle impossible gate", [source, target],
            [new EdgeDefinition("image", "source", "image", "target", "image", "data")]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("requires 6 valid calipers", error.Message);
        Assert.Contains("generate only 5", error.Message);
    }

    [Fact]
    public void CaliperCircleRejectsStepArcThatCannotProvideThreePointsForAFit()
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("target", "measure.caliperCircle", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"samplingMode":"step","angleStartDeg":10,"angleEndDeg":30,"angleStepDeg":15}"""));
        var workflow = new WorkflowDefinition("caliper-circle-too-few-steps", "Caliper circle too few steps", [source, target],
            [new EdgeDefinition("image", "source", "image", "target", "image", "data")]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("generate only 2 radial caliper(s)", error.Message);
        Assert.Contains("circle fitting requires at least 3", error.Message);
    }

    [Fact]
    public void CaliperCircleRejectsStepModeGateAboveGeneratedCaliperCount()
    {
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var target = new NodeDefinition("target", "measure.caliperCircle", null, null,
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"samplingMode":"step","angleStartDeg":0,"angleEndDeg":90,"angleStepDeg":30,"minValidCalipers":5}"""));
        var workflow = new WorkflowDefinition("caliper-circle-step-gate", "Caliper circle step gate", [source, target],
            [new EdgeDefinition("image", "source", "image", "target", "image", "data")]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("requires 5 valid calipers", error.Message);
        Assert.Contains("generate only 4", error.Message);
    }

    [Theory]
    [InlineData("image.filter", """{"method":"median","sigmaX":"inactive"}""")]
    [InlineData("image.morphology", """{"operation":"gradient","kernelSize":"inactive"}""")]
    [InlineData("image.morphology", """{"operation":"open","kernelWidth":"inactive"}""")]
    [InlineData("text.validate", """{"mode":"contains","pattern":7,"timeoutMs":"inactive"}""")]
    public void InactiveModeParameters_AreNotValidated(string nodeType, string parametersJson)
    {
        var compiler = CreateCompiler();
        var nodeCatalog = BuiltInNodeCatalog.Require(nodeType);
        var nodes = new List<NodeDefinition>();
        var edges = new List<EdgeDefinition>();
        if (nodeCatalog.Inputs.Any(port => port.Name == "image" && port.DataType != VisionDataType.Control))
        {
            nodes.Add(new NodeDefinition("source", "image.synthetic", null, null, null));
            edges.Add(new EdgeDefinition("image", "source", "image", "n1", "image", "data"));
        }
        nodes.Add(new NodeDefinition("n1", nodeType, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson)));

        var compiled = compiler.Compile(new WorkflowDefinition("wf-conditional", "conditional parameters", nodes, edges));

        Assert.NotEmpty(compiled.DslJson);
    }

    private static WorkflowDefinition CreateWorkflow(string nodeType, string parametersJson)
        => new(
            "wf-params",
            "Parameter validation",
            [new NodeDefinition("n1", nodeType, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson))],
            []);

    private static VisionWorkflowCompiler CreateCompiler()
    {
        var registry = new VisionNodeRegistry();
        foreach (var catalog in BuiltInNodeCatalog.Items)
            registry.Register(catalog, new NoopExecutor(catalog.Type), "test-catalog");
        return new VisionWorkflowCompiler(registry);
    }

    private sealed class NoopExecutor(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => throw new NotSupportedException("Compilation-only executor.");
    }
}
