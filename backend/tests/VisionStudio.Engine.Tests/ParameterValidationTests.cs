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
    [InlineData("collection.detections.at", "index", 0.4)]
    [InlineData("collection.detections.sort", "topN", 0.4)]
    [InlineData("collection.detections.filter", "classId", 1.5)]
    [InlineData("flow.forEach", "maxItems", 1.5)]
    public void CollectionIntegerParameters_RejectFractionsDuringCompilation(string type, string parameter, double value)
    {
        var (compiler, workflow) = CollectionParameterWorkflow(type, JsonSerializer.Serialize(new Dictionary<string, double> { [parameter] = value }));
        var error = Assert.Throws<InvalidOperationException>(() => compiler.Compile(workflow));
        Assert.Contains("n1", error.Message);
        Assert.Contains(parameter, error.Message);
        Assert.Contains("integer", error.Message);
    }

    [Theory]
    [InlineData("collection.detections.at", "{}")]
    [InlineData("collection.detections.at", """{"index":199}""")]
    [InlineData("collection.detections.sort", "{}")]
    [InlineData("collection.detections.sort", """{"topN":0}""")]
    [InlineData("collection.detections.sort", """{"topN":10000}""")]
    [InlineData("collection.detections.filter", "{}")]
    [InlineData("collection.detections.filter", """{"classId":-1}""")]
    [InlineData("collection.detections.filter", """{"classId":999999999,"minArea":0.5}""")]
    [InlineData("flow.forEach", "{}")]
    [InlineData("flow.forEach", """{"maxItems":1}""")]
    [InlineData("flow.forEach", """{"maxItems":500}""")]
    public void CollectionIntegerParameters_PreserveDefaultsBoundariesAndSentinels(string type, string parameters)
    {
        var (compiler, workflow) = CollectionParameterWorkflow(type, parameters);
        Assert.Contains("n1", compiler.Compile(workflow).OrderedNodeIds);
    }

    private static (VisionWorkflowCompiler Compiler, WorkflowDefinition Workflow) CollectionParameterWorkflow(string type, string parameters)
    {
        var source = new NodeCatalogItem("test.collectionSource", "Collection Source", "Test", [],
            [new("detections", VisionDataType.DetectionList), new("next", VisionDataType.Control)], []);
        var nodes = new List<NodeDefinition>
        {
            new("source", source.Type, null, null, null),
            new("n1", type, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters))
        };
        var edges = new List<EdgeDefinition>
        {
            new("start", "source", "next", "n1", "exec", "control"),
            new("items", "source", "detections", "n1", "detections", "data")
        };
        if (type == "flow.forEach")
        {
            nodes.AddRange([
                new("body", "flow.delay", null, null, null),
                new("end", "flow.loopEnd", null, null, null),
                new("after", "flow.delay", null, null, null)
            ]);
            edges.AddRange([
                new("body", "n1", "body", "body", "exec", "control"),
                new("end", "body", "next", "end", "exec", "control"),
                new("repeat", "end", "repeat", "n1", "repeat", "control"),
                new("after", "n1", "done", "after", "exec", "control")
            ]);
        }
        return (CreateCompiler(source), new WorkflowDefinition("collection-parameters", "Collection parameters", nodes, edges));
    }

    [Theory]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":"3"}""", "expects a number")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":null}""", "received null")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":-1}""", "below the minimum 0")]
    [InlineData("calibration.planar", "assetVersion", """{"assetVersion":1000000}""", "above the maximum 999999")]
    [InlineData("calibration.planar", "targetUnit", """{"targetUnit":"furlong"}""", "not an allowed option")]
    [InlineData("calibration.planar", "targetUnit", """{"targetUnit":7}""", "expects one of [mm, cm, m, px]")]
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
    [InlineData("feature.line", "line", "measure.caliper", "searchLine")]
    [InlineData("feature.line", "line", "feature.edgeSubpixel", "searchLine")]
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

    [Theory]
    [InlineData("measure.fitLine", "line", "measure.caliperLine", "searchLine")]
    [InlineData("measure.fitCircle", "circle", "measure.caliperCircle", "circle")]
    public void MeasurementRejectsFittedGeometryAlreadyInTheTransformTargetFrame(
        string fitType,
        string fitOutput,
        string measurementType,
        string measurementInput)
    {
        var workflow = new WorkflowDefinition("fitted-geometry-frame-conflict", "Fitted geometry frame conflict",
        [
            new NodeDefinition("image", "image.synthetic", null, null, null),
            new NodeDefinition("pose", "roi.transform", null, null, null),
            new NodeDefinition("point1", "measure.caliperLine", null, null, null),
            new NodeDefinition("point2", "measure.caliperLine", null, null, null),
            new NodeDefinition("point3", "measure.caliperLine", null, null, null),
            new NodeDefinition("fit", fitType, null, null, null),
            new NodeDefinition("measure", measurementType, null, null, null)
        ],
        [
            new EdgeDefinition("image-pose-control", "image", "next", "pose", "exec", "control"),
            new EdgeDefinition("pose-point1-control", "pose", "next", "point1", "exec", "control"),
            new EdgeDefinition("point1-point2-control", "point1", "next", "point2", "exec", "control"),
            new EdgeDefinition("point2-point3-control", "point2", "next", "point3", "exec", "control"),
            new EdgeDefinition("point3-fit-control", "point3", "next", "fit", "exec", "control"),
            new EdgeDefinition("fit-measure-control", "fit", "next", "measure", "exec", "control"),
            new EdgeDefinition("pose-image", "image", "image", "pose", "debugImage", "data"),
            new EdgeDefinition("point1-image", "image", "image", "point1", "image", "data"),
            new EdgeDefinition("point2-image", "image", "image", "point2", "image", "data"),
            new EdgeDefinition("point3-image", "image", "image", "point3", "image", "data"),
            new EdgeDefinition("fit-point1", "point1", "point", "fit", "point1", "data"),
            new EdgeDefinition("fit-point2", "point2", "point", "fit", "point2", "data"),
            new EdgeDefinition("fit-point3", "point3", "point", "fit", "point3", "data"),
            new EdgeDefinition("measure-image", "image", "image", "measure", "image", "data"),
            new EdgeDefinition("fitted-geometry", "fit", fitOutput, "measure", measurementInput, "data"),
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
    [InlineData("feature.line", "line", "measure.caliperLine", "searchLine")]
    [InlineData("feature.circle", "circle", "measure.caliperCircle", "circle")]
    public void MeasurementRejectsGeometryFromDifferentImageFrameThanTransformSource(
        string geometryType,
        string geometryOutput,
        string measurementType,
        string measurementInput)
    {
        var workflow = new WorkflowDefinition("geometry-transform-source-mismatch", "Geometry transform source mismatch",
        [
            new NodeDefinition("source-image", "image.synthetic", null, null, null),
            new NodeDefinition("other-image", "image.synthetic", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null),
            new NodeDefinition("geometry", geometryType, null, null, null),
            new NodeDefinition("measure", measurementType, null, null, null)
        ],
        [
            new EdgeDefinition("crop-control", "source-image", "next", "crop", "exec", "control"),
            new EdgeDefinition("geometry-control", "crop", "next", "geometry", "exec", "control"),
            new EdgeDefinition("measure-control", "geometry", "next", "measure", "exec", "control"),
            new EdgeDefinition("source-crop", "source-image", "image", "crop", "image", "data"),
            new EdgeDefinition("geometry-image", "other-image", "image", "geometry", "image", "data"),
            new EdgeDefinition("crop-measure", "crop", "image", "measure", "image", "data"),
            new EdgeDefinition("geometry-input", "geometry", geometryOutput, "measure", measurementInput, "data"),
            new EdgeDefinition("crop-transform", "crop", "transform", "measure", "transform", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("geometry is in frame 'other-image.image'", error.Message);
        Assert.Contains("transform expects source frame 'source-image.image'", error.Message);
    }

    [Theory]
    [InlineData("feature.line", "line", "measure.caliper", "searchLine")]
    [InlineData("feature.line", "line", "feature.edgeSubpixel", "searchLine")]
    [InlineData("feature.line", "line", "measure.caliperLine", "searchLine")]
    [InlineData("feature.circle", "circle", "measure.caliperCircle", "circle")]
    public void MeasurementRejectsGeometryFromADifferentImageFrameWithoutTransform(
        string geometryType,
        string geometryOutput,
        string measurementType,
        string measurementInput)
    {
        var workflow = new WorkflowDefinition("geometry-frame-mismatch", "Geometry from another image",
        [
            new NodeDefinition("geometry-image", "image.synthetic", null, null, null),
            new NodeDefinition("geometry-crop", "image.crop", null, null, null),
            new NodeDefinition("measurement-crop", "image.crop", null, null, null),
            new NodeDefinition("geometry", geometryType, null, null, null),
            new NodeDefinition("measure", measurementType, null, null, null)
        ],
        [
            new EdgeDefinition("geometry-crop-control", "geometry-image", "next", "geometry-crop", "exec", "control"),
            new EdgeDefinition("measurement-crop-control", "geometry-crop", "next", "measurement-crop", "exec", "control"),
            new EdgeDefinition("geometry-control", "measurement-crop", "next", "geometry", "exec", "control"),
            new EdgeDefinition("measure-control", "geometry", "next", "measure", "exec", "control"),
            new EdgeDefinition("geometry-crop-image", "geometry-image", "image", "geometry-crop", "image", "data"),
            new EdgeDefinition("measurement-crop-image", "geometry-image", "image", "measurement-crop", "image", "data"),
            new EdgeDefinition("geometry-image-input", "geometry-crop", "image", "geometry", "image", "data"),
            new EdgeDefinition("measurement-image-input", "measurement-crop", "image", "measure", "image", "data"),
            new EdgeDefinition("geometry-input", "geometry", geometryOutput, "measure", measurementInput, "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains($"Node 'measure' input '{measurementInput}' geometry is in frame 'geometry-crop.image (crop frame)'", error.Message);
        Assert.Contains("measurement image is in frame 'measurement-crop.image (crop frame)'", error.Message);
        Assert.Contains("Connect geometry and image from the same pixel frame", error.Message);
    }

    [Fact]
    public void MeasurementAcceptsGeometryFromTheSameImageFrameThroughImagePreservingNodes()
    {
        var workflow = new WorkflowDefinition("matching-geometry-frame", "Geometry from same image frame",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("gray", "image.grayscale", null, null, null),
            new NodeDefinition("geometry", "feature.line", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("gray-control", "scene", "next", "gray", "exec", "control"),
            new EdgeDefinition("geometry-control", "gray", "next", "geometry", "exec", "control"),
            new EdgeDefinition("measure-control", "geometry", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-gray", "scene", "image", "gray", "image", "data"),
            new EdgeDefinition("gray-geometry", "gray", "image", "geometry", "image", "data"),
            new EdgeDefinition("gray-measure", "gray", "image", "measure", "image", "data"),
            new EdgeDefinition("geometry-measure", "geometry", "line", "measure", "searchLine", "data")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.Contains("measure", compiled.OrderedNodeIds);
    }

    [Theory]
    [InlineData("measure.blob", "circle")]
    [InlineData("measure.blobs", "circle")]
    public void MeasurementRejectsBlobCircleFromAnotherImageFrame(string circleSourceType, string circlePort)
    {
        var workflow = new WorkflowDefinition("blob-circle-frame-mismatch", "Blob circle from another image",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("blob", circleSourceType, null, null, null),
            new NodeDefinition("undistort", "image.undistort", null, null, null),
            new NodeDefinition("measure", "measure.caliperCircle", null, null, null)
        ],
        [
            new EdgeDefinition("blob-control", "scene", "next", "blob", "exec", "control"),
            new EdgeDefinition("undistort-control", "blob", "next", "undistort", "exec", "control"),
            new EdgeDefinition("measure-control", "undistort", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-blob", "scene", "image", "blob", "image", "data"),
            new EdgeDefinition("scene-undistort", "scene", "image", "undistort", "image", "data"),
            new EdgeDefinition("undistorted-image", "undistort", "image", "measure", "image", "data"),
            new EdgeDefinition("blob-circle", "blob", circlePort, "measure", "circle", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("Node 'measure' input 'circle' geometry is in frame 'scene.image'", error.Message);
        Assert.Contains("measurement image is in frame 'undistort.image (undistorted frame)'", error.Message);
    }

    [Fact]
    public void MeasurementRejectsCircleExtractedFromDetectionsInAnotherImageFrame()
    {
        var workflow = new WorkflowDefinition("detection-circle-frame-mismatch", "Detection circle from another image",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("blobs", "measure.blobs", null, null, null),
            new NodeDefinition("filter", "collection.detections.filter", null, null, null),
            new NodeDefinition("sort", "collection.detections.sort", null, null, null),
            new NodeDefinition("detection", "collection.detections.at", null, null, null),
            new NodeDefinition("undistort", "image.undistort", null, null, null),
            new NodeDefinition("measure", "measure.caliperCircle", null, null, null)
        ],
        [
            new EdgeDefinition("blobs-control", "scene", "next", "blobs", "exec", "control"),
            new EdgeDefinition("filter-control", "blobs", "next", "filter", "exec", "control"),
            new EdgeDefinition("sort-control", "filter", "next", "sort", "exec", "control"),
            new EdgeDefinition("detection-control", "sort", "next", "detection", "exec", "control"),
            new EdgeDefinition("undistort-control", "detection", "next", "undistort", "exec", "control"),
            new EdgeDefinition("measure-control", "undistort", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-blobs", "scene", "image", "blobs", "image", "data"),
            new EdgeDefinition("blobs-filter", "blobs", "detections", "filter", "detections", "data"),
            new EdgeDefinition("filter-sort", "filter", "detections", "sort", "detections", "data"),
            new EdgeDefinition("sort-detection", "sort", "detections", "detection", "detections", "data"),
            new EdgeDefinition("scene-undistort", "scene", "image", "undistort", "image", "data"),
            new EdgeDefinition("undistorted-image", "undistort", "image", "measure", "image", "data"),
            new EdgeDefinition("detection-circle", "detection", "circle", "measure", "circle", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("Node 'measure' input 'circle' geometry is in frame 'scene.image'", error.Message);
        Assert.Contains("measurement image is in frame 'undistort.image (undistorted frame)'", error.Message);
    }

    [Fact]
    public void RegionBooleanRejectsRegionsFromDifferentImageFramesBeforeCaliperUse()
    {
        var workflow = new WorkflowDefinition("region-boolean-frame-mismatch", "Region boolean across image frames",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("undistort", "image.undistort", null, null, null),
            new NodeDefinition("raw-region", "region.fromThreshold", null, null, null),
            new NodeDefinition("corrected-region", "region.fromThreshold", null, null, null),
            new NodeDefinition("combine", "region.boolean", null, null, null),
            new NodeDefinition("detections", "region.toDetections", null, null, null),
            new NodeDefinition("detection", "collection.detections.at", null, null, null),
            new NodeDefinition("measure", "measure.caliperCircle", null, null, null)
        ],
        [
            new EdgeDefinition("scene-undistort-control", "scene", "next", "undistort", "exec", "control"),
            new EdgeDefinition("undistort-raw-region-control", "undistort", "next", "raw-region", "exec", "control"),
            new EdgeDefinition("raw-corrected-region-control", "raw-region", "next", "corrected-region", "exec", "control"),
            new EdgeDefinition("corrected-combine-control", "corrected-region", "next", "combine", "exec", "control"),
            new EdgeDefinition("combine-detections-control", "combine", "next", "detections", "exec", "control"),
            new EdgeDefinition("detections-at-control", "detections", "next", "detection", "exec", "control"),
            new EdgeDefinition("detection-measure-control", "detection", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-undistort", "scene", "image", "undistort", "image", "data"),
            new EdgeDefinition("raw-region-image", "scene", "image", "raw-region", "image", "data"),
            new EdgeDefinition("corrected-region-image", "undistort", "image", "corrected-region", "image", "data"),
            new EdgeDefinition("raw-region-input", "raw-region", "region", "combine", "a", "data"),
            new EdgeDefinition("corrected-region-input", "corrected-region", "region", "combine", "b", "data"),
            new EdgeDefinition("combine-detections", "combine", "region", "detections", "region", "data"),
            new EdgeDefinition("detections-index", "detections", "detections", "detection", "detections", "data"),
            new EdgeDefinition("measurement-image", "undistort", "image", "measure", "image", "data"),
            new EdgeDefinition("detection-circle", "detection", "circle", "measure", "circle", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("Node 'combine' cannot combine regions from different image frames", error.Message);
        Assert.Contains("scene.image", error.Message);
        Assert.Contains("undistort.image (undistorted frame)", error.Message);
    }

    [Fact]
    public void MeasurementRejectsRoiAuthoredBeforeImageUndistortion()
    {
        var workflow = new WorkflowDefinition("raw-roi-on-undistorted-image", "Raw ROI on undistorted image",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("undistort", "image.undistort", null, null, null),
            new NodeDefinition("roi", "roi.transform", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("scene-undistort-control", "scene", "next", "undistort", "exec", "control"),
            new EdgeDefinition("undistort-roi-control", "undistort", "next", "roi", "exec", "control"),
            new EdgeDefinition("roi-measure-control", "roi", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-undistort", "scene", "image", "undistort", "image", "data"),
            new EdgeDefinition("scene-roi", "scene", "image", "roi", "debugImage", "data"),
            new EdgeDefinition("undistorted-measurement-image", "undistort", "image", "measure", "image", "data"),
            new EdgeDefinition("raw-roi", "roi", "roi", "measure", "roi", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("Node 'measure' ROI input 'roi'", error.Message);
        Assert.Contains("scene.image", error.Message);
        Assert.Contains("undistort.image (undistorted frame)", error.Message);
    }

    [Fact]
    public void MeasurementRejectsSearchGeometryAuthoredBeforeImageUndistortion()
    {
        var workflow = new WorkflowDefinition("raw-line-on-undistorted-image", "Raw line on undistorted image",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("line", "feature.line", null, null, null),
            new NodeDefinition("undistort", "image.undistort", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("scene-line-control", "scene", "next", "line", "exec", "control"),
            new EdgeDefinition("line-undistort-control", "line", "next", "undistort", "exec", "control"),
            new EdgeDefinition("undistort-measure-control", "undistort", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-line", "scene", "image", "line", "image", "data"),
            new EdgeDefinition("scene-undistort", "scene", "image", "undistort", "image", "data"),
            new EdgeDefinition("undistorted-measurement-image", "undistort", "image", "measure", "image", "data"),
            new EdgeDefinition("raw-line", "line", "line", "measure", "searchLine", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("Node 'measure' input 'searchLine' geometry is in frame 'scene.image'", error.Message);
        Assert.Contains("measurement image is in frame 'undistort.image (undistorted frame)'", error.Message);
    }

    [Fact]
    public void MeasurementAcceptsRoiAndSearchGeometryAuthoredAfterImageUndistortion()
    {
        var workflow = new WorkflowDefinition("undistorted-roi-and-line", "ROI and line in undistorted frame",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("undistort", "image.undistort", null, null, null),
            new NodeDefinition("roi", "roi.transform", null, null, null),
            new NodeDefinition("line", "feature.line", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("scene-undistort-control", "scene", "next", "undistort", "exec", "control"),
            new EdgeDefinition("undistort-roi-control", "undistort", "next", "roi", "exec", "control"),
            new EdgeDefinition("roi-line-control", "roi", "next", "line", "exec", "control"),
            new EdgeDefinition("line-measure-control", "line", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-undistort", "scene", "image", "undistort", "image", "data"),
            new EdgeDefinition("undistorted-roi", "undistort", "image", "roi", "debugImage", "data"),
            new EdgeDefinition("undistorted-line", "undistort", "image", "line", "image", "data"),
            new EdgeDefinition("undistorted-measurement-image", "undistort", "image", "measure", "image", "data"),
            new EdgeDefinition("roi-measure", "roi", "roi", "measure", "roi", "data"),
            new EdgeDefinition("line-measure", "line", "line", "measure", "searchLine", "data")
        ]);

        var compiled = CreateCompiler().Compile(workflow);

        Assert.Contains("measure", compiled.OrderedNodeIds);
    }

    [Fact]
    public void MeasurementRejectsRoiFromADifferentCropImageFrame()
    {
        var workflow = new WorkflowDefinition("mismatched-crop-roi", "Mismatched crop ROI",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("scene-crop-control", "scene", "next", "crop", "exec", "control"),
            new EdgeDefinition("crop-measure-control", "crop", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-crop", "scene", "image", "crop", "image", "data"),
            new EdgeDefinition("measurement-image", "scene", "image", "measure", "image", "data"),
            new EdgeDefinition("wrong-roi", "crop", "croppedRoi", "measure", "roi", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("Node 'measure' ROI input 'roi'", error.Message);
        Assert.Contains("crop.image (crop frame)", error.Message);
        Assert.Contains("scene.image", error.Message);
    }

    [Fact]
    public void CropRejectsCorrectedRoiFromADifferentImageFrame()
    {
        var workflow = new WorkflowDefinition("mismatched-crop-input-roi", "Mismatched crop input ROI",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("other", "image.synthetic", null, null, null),
            new NodeDefinition("roi", "roi.transform", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null)
        ],
        [
            new EdgeDefinition("scene-roi-control", "scene", "next", "roi", "exec", "control"),
            new EdgeDefinition("roi-crop-control", "roi", "next", "crop", "exec", "control"),
            new EdgeDefinition("scene-crop-image", "scene", "IMAGE", "crop", "IMAGE", "data"),
            new EdgeDefinition("other-roi-image", "other", "IMAGE", "roi", "DEBUGIMAGE", "data"),
            new EdgeDefinition("corrected-roi", "roi", "ROI", "crop", "ROI", "data")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow));

        Assert.Contains("Node 'crop' ROI input 'roi'", error.Message);
        Assert.Contains("other.image", error.Message);
        Assert.Contains("scene.image", error.Message);
    }

    [Fact]
    public void MeasurementAcceptsCroppedRoiFromTheMatchingCropImageFrame()
    {
        var workflow = new WorkflowDefinition("matching-crop-roi", "Matching crop ROI",
        [
            new NodeDefinition("scene", "image.synthetic", null, null, null),
            new NodeDefinition("crop", "image.crop", null, null, null),
            new NodeDefinition("measure", "measure.caliperLine", null, null, null)
        ],
        [
            new EdgeDefinition("scene-crop-control", "scene", "next", "crop", "exec", "control"),
            new EdgeDefinition("crop-measure-control", "crop", "next", "measure", "exec", "control"),
            new EdgeDefinition("scene-crop", "scene", "image", "crop", "image", "data"),
            new EdgeDefinition("crop-image", "crop", "image", "measure", "image", "data"),
            new EdgeDefinition("cropped-roi", "crop", "croppedRoi", "measure", "roi", "data")
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

    [Theory]
    [InlineData("logic.boolean", "and", true)]
    [InlineData("logic.boolean", "or", true)]
    [InlineData("logic.boolean", "not", false)]
    [InlineData("value.arithmetic", "add", true)]
    [InlineData("value.arithmetic", "pow", true)]
    [InlineData("value.arithmetic", "abs", false)]
    [InlineData("value.arithmetic", "sqrt", false)]
    [InlineData("value.arithmetic", "clamp", false)]
    public void BinaryOperandRequirements_AreValidatedBeforeExecution(string type, string op, bool requiresB)
    {
        var target = CreateWorkflow(type, JsonSerializer.Serialize(new { op })).Nodes[0];
        var source = new NodeDefinition("source", "value.number", null, null, null);
        var workflow = new WorkflowDefinition("binary-input", "Binary input", [source, target],
            [new EdgeDefinition("a", source.Id, type == "logic.boolean" ? "isInteger" : "value", target.Id, "a", "data")]);
        if (requiresB)
            Assert.Contains("Required input 'n1.b'", Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow)).Message);
        else
            Assert.Contains(target.Id, CreateCompiler().Compile(workflow).OrderedNodeIds);
    }

    [Theory]
    [InlineData("text.match")]
    [InlineData("text.validate")]
    public void ConnectedTextInputs_OverrideInvalidDormantFallbacks(string type)
    {
        var target = CreateWorkflow(type, """{"text":123,"expected":false}""").Nodes[0];
        var source = new NodeDefinition("source", "text.match", null, null, null);
        var workflow = new WorkflowDefinition("connected-text", "Connected text", [source, target],
        [
            new EdgeDefinition("control", source.Id, "next", target.Id, "exec", "control"),
            new EdgeDefinition("text", source.Id, "text", target.Id, "text", "data"),
            new EdgeDefinition("expected", source.Id, "text", target.Id, "expected", "data")
        ]);
        Assert.Contains(target.Id, CreateCompiler().Compile(workflow).OrderedNodeIds);
    }

    [Fact]
    public void InactiveArithmeticBounds_DoNotBlockUnaryOperations()
    {
        var target = CreateWorkflow("value.arithmetic", """{"op":"abs","clampMin":null,"clampMax":"unused"}""").Nodes[0];
        var source = new NodeDefinition("source", "value.number", null, null, null);
        var workflow = new WorkflowDefinition("inactive-bounds", "Inactive bounds", [source, target],
            [new EdgeDefinition("a", source.Id, "value", target.Id, "a", "data")]);
        Assert.Contains(target.Id, CreateCompiler().Compile(workflow).OrderedNodeIds);
    }

    [Theory]
    [InlineData("image.resize", """{"width":null,"height":null}""")]
    [InlineData("image.resize", """{"width":100,"height":null}""")]
    [InlineData("jlvision.measure1d", """{"row":null,"column":null}""")]
    public void ContextDependentNumericDefaults_AllowExplicitNull(string type, string parametersJson)
    {
        var target = CreateWorkflow(type, parametersJson).Nodes[0];
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var workflow = new WorkflowDefinition("automatic-geometry", "Automatic geometry", [source, target],
            [new EdgeDefinition("image", source.Id, "image", target.Id, "image", "data")]);

        Assert.Contains(target.Id, CreateCompiler().Compile(workflow).OrderedNodeIds);
    }

    [Theory]
    [InlineData("image.resize", """{"width":0}""", "below the minimum")]
    [InlineData("image.resize", """{"width":"320"}""", "expects a number")]
    [InlineData("jlvision.measure1d", """{"row":"16"}""", "expects a number")]
    public void ContextDependentNumericDefaults_StillRejectInvalidExplicitValues(string type, string parametersJson, string reason)
    {
        var target = CreateWorkflow(type, parametersJson).Nodes[0];
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var workflow = new WorkflowDefinition("invalid-geometry", "Invalid geometry", [source, target],
            [new EdgeDefinition("image", source.Id, "image", target.Id, "image", "data")]);

        Assert.Contains(reason, Assert.Throws<InvalidOperationException>(() => CreateCompiler().Compile(workflow)).Message);
    }

    [Theory]
    [InlineData("zoomImageFactor", 0.5)]
    [InlineData("ZOOMIMAGEFACTOR", 0.5)]
    public void OperatorVariantRanges_AreCaseInsensitive(string operation, double factor)
    {
        var parameters = JsonSerializer.Serialize(new { @operator = operation, mult = factor, offset = factor });
        var target = CreateWorkflow("jlvision.imageOp", parameters).Nodes[0];
        var source = new NodeDefinition("source", "image.synthetic", null, null, null);
        var workflow = new WorkflowDefinition("variant-geometry", "Variant geometry", [source, target],
            [new EdgeDefinition("image", source.Id, "image", target.Id, "image", "data")]);

        Assert.Contains(target.Id, CreateCompiler().Compile(workflow).OrderedNodeIds);
    }

    [Fact]
    public void NumericOverflowWithoutCatalogBounds_IsRejectedBeforeExecution()
    {
        var catalog = new NodeCatalogItem("test.numeric", "Numeric", "Test", [], [],
            [new ParameterDescriptor("value", "Value", "number", 0)]);
        using var registry = new VisionNodeRegistry();
        registry.Register(catalog, new NoopExecutor(catalog.Type), "test");
        var error = Assert.Throws<InvalidOperationException>(() => new VisionWorkflowCompiler(registry)
            .Compile(CreateWorkflow(catalog.Type, """{"value":1e400}""")));

        Assert.Contains("parameter 'value'", error.Message);
        Assert.Contains("cannot be represented", error.Message);
    }

    private static VisionWorkflowCompiler CreateCompiler(params NodeCatalogItem[] additionalCatalog)
    {
        var registry = new VisionNodeRegistry();
        foreach (var catalog in BuiltInNodeCatalog.Items.Concat(additionalCatalog))
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
