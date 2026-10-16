using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 第五批（2026-10-14）验收 · 5C 形状定位与动态 ROI：
/// match.shape 的平移/旋转定位精度、多目标抑制、无目标与尺度限制（文档化假设的负向验证）；
/// roi.transform 的刚体变换正确性与输入校验。
/// </summary>
public sealed class Batch5ShapeMatchTests
{
    [Fact]
    public async Task ShapeMatch_TranslationAndRotation_RecoversPoseWithinTolerance()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        var matrix = PasteMatrix(250, 180, 25);
        using (matrix) Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        var result = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"minScore":0.5,"angleRangeDeg":180,"angleStepDeg":1,"minArea":300,"signatureSamples":360}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]), SummaryText(result));
        var score = result.Outputs["score"].Require<double>("score");
        Assert.True(score > 0.8, $"score={score}");

        using var expectedMatrix = PasteMatrix(250, 180, 25);
        var expectedCenter = TemplateCentroidThrough(expectedMatrix);
        var x = result.Outputs["x"].Require<double>("x");
        var y = result.Outputs["y"].Require<double>("y");
        Assert.InRange(x, expectedCenter.X - 3, expectedCenter.X + 3);
        Assert.InRange(y, expectedCenter.Y - 3, expectedCenter.Y + 3);

        var angle = result.Outputs["angle"].Require<double>("angle");
        Assert.InRange(angle, 23, 27); // 真值 +25°，步长 1°

        // pose 与 x/y/angle 一致，供 roi.transform 直接使用。
        var pose = result.Outputs["pose"].Require<VisionPose2D>("pose");
        Assert.Equal(x, pose.X);
        Assert.Equal(angle, pose.ThetaDeg);

        // 单目标 detections 与 count。
        Assert.Equal(1, Convert.ToInt32(result.Outputs["count"].Require<long>("count")));
        var detections = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Single(detections);
        Assert.NotNull(detections[0].Confidence);
    }

    [Fact]
    public async Task ShapeMatch_NinetyDegrees_IsRecovered()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = PasteMatrix(200, 220, 90))
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        var result = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"minScore":0.5,"angleStepDeg":1,"minArea":300,"signatureSamples":360}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var angle = result.Outputs["angle"].Require<double>("angle");
        Assert.InRange(angle, 88, 92);
    }

    [Fact]
    public async Task ShapeMatch_RotatedRectangle_UsesCompleteContourSignature()
    {
        using var template = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(template, new Rect(20, 40, 80, 40), Scalar.All(255), -1);
        using var scene = new Mat(240, 240, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = Cv2.GetRotationMatrix2D(new Point2f(60, 60), 90, 1))
        {
            matrix.Set(0, 2, matrix.At<double>(0, 2) + 120 - 60);
            matrix.Set(1, 2, matrix.At<double>(1, 2) + 120 - 60);
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        }
        var result = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"minScore":0.8,"angleRangeDeg":180,"angleStepDeg":1,"minArea":300,"signatureSamples":360}""");
        Assert.True(Convert.ToBoolean(result.Summary["found"]), SummaryText(result));
        Assert.InRange(result.Outputs["angle"].Require<double>("angle"), 88, 92);
        AssertImageOutput(result);
        var outputImage = result.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0, Cv2.Norm(scene, outputImage.Mat, NormTypes.L2));
        Assert.True(result.Overlays?.Count > 0);
    }

    [Fact]
    public async Task ShapeMatch_MultipleTargets_AreSuppressedByCenterDistance()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var m1 = PasteMatrix(120, 120, 0))
        using (var layer = new Mat(scene.Size(), scene.Type(), Scalar.All(0)))
        {
            Cv2.WarpAffine(template, layer, m1, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            Cv2.BitwiseOr(scene, layer, scene);
        }
        using (var m2 = PasteMatrix(310, 290, 40))
        using (var layer = new Mat(scene.Size(), scene.Type(), Scalar.All(0)))
        {
            Cv2.WarpAffine(template, layer, m2, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            Cv2.BitwiseOr(scene, layer, scene);
        }

        var result = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"minScore":0.5,"angleStepDeg":2,"minArea":300,"maxTargets":5,"minCenterDistance":60,"signatureSamples":360}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]), SummaryText(result));
        Assert.Equal(2, Convert.ToInt32(result.Outputs["count"].Require<long>("count")));
        var detections = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");

        using var e1 = PasteMatrix(120, 120, 0);
        using var e2 = PasteMatrix(310, 290, 40);
        foreach (var expected in new[] { TemplateCentroidThrough(e1), TemplateCentroidThrough(e2) })
        {
            Assert.Contains(detections, d =>
                Math.Abs(d.Circle.X - expected.X) <= 4 && Math.Abs(d.Circle.Y - expected.Y) <= 4);
        }
        Assert.All(detections, d => Assert.True(d.Confidence > 0.5));
    }

    [Fact]
    public async Task ShapeMatch_NoTargetAndScaleChange_BehaveAsDocumented()
    {
        using var template = BuildTemplate();
        using var blank = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        var none = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(blank), ["template"] = Image(template) },
            """{"minScore":0.5,"angleStepDeg":2}""");
        Assert.False(Convert.ToBoolean(none.Summary["found"]));
        Assert.Equal(0d, none.Outputs["score"].Require<double>("score"));
        AssertImageOutput(none);

        // 尺度改变（0.7×）：首版不搜索尺度 ⇒ 分数显著下降（文档化限制的负向验证）。
        using var scaled = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = ScaleMatrix(200, 200, 0.7))
            Cv2.WarpAffine(template, scaled, matrix, scaled.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        var scaledResult = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scaled), ["template"] = Image(template) },
            """{"minScore":0.5,"angleStepDeg":2,"minArea":100,"signatureSamples":360}""");
        Assert.True(scaledResult.Outputs["score"].Require<double>("score") < 0.9);

        // 无模板来源 ⇒ 参数错误。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new ShapeMatchNode(),
                new Dictionary<string, VisionValue> { ["image"] = Image(blank) }, """{}"""));
        Assert.Contains("template", error.Message);
    }

    [Fact]
    public async Task ShapeMatch_WrongShapeAndEmptyTemplate_ReturnImageOutputs()
    {
        using var template = BuildTemplate();
        using var circleScene = new Mat(240, 240, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(circleScene, new Point(120, 120), 45, Scalar.All(255), -1);
        var wrong = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(circleScene), ["template"] = Image(template) },
            """{"minScore":0.75,"minArea":100}""");
        Assert.False(Convert.ToBoolean(wrong.Summary["found"]));
        AssertImageOutput(wrong);

        using var emptyTemplate = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0));
        var empty = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(circleScene), ["template"] = Image(emptyTemplate) },
            """{}""");
        Assert.False(Convert.ToBoolean(empty.Summary["found"]));
        AssertImageOutput(empty);
    }

    [Fact]
    public async Task ShapeMatch_CancellationDoesNotDisposeInputImages()
    {
        using var scene = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(0));
        using var template = BuildTemplate();
        var sceneValue = Image(scene);
        var templateValue = Image(template);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new ShapeMatchNode().ExecuteAsync(
                new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = sceneValue, ["template"] = templateValue }),
                new NodeDefinition("n1", "match.shape", null, null, null), cancellation.Token));
        Assert.False(sceneValue.Require<VisionImage>("image").Mat.Empty());
        Assert.False(templateValue.Require<VisionImage>("template").Mat.Empty());
        (sceneValue.Value as VisionImage)?.Dispose();
        (templateValue.Value as VisionImage)?.Dispose();
    }

    [Fact]
    public async Task ShapeMatch_ExplicitTrainingModel_IsReusableWithoutTemplateInput_AndRejectsStaleSettings()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = PasteMatrix(165, 145, 20))
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        var trained = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"modelMode":"trained","trainingMode":true,"angleRangeDeg":180,"angleStepDeg":2,"minArea":100}""");
        Assert.True(Convert.ToBoolean(trained.Summary["trainingSucceeded"]));
        Assert.Contains(trained.Overlays!, overlay => overlay.Id.StartsWith("shape-training-contour-", StringComparison.Ordinal));
        Assert.Contains(trained.Overlays!, overlay => overlay.Id == "shape-training-origin");
        var trainedContours = Assert.IsType<VisionOverlayPoint[][]>(trained.Summary["trainingContours"]);
        Assert.NotEmpty(trainedContours);
        var modelContourPoints = trainedContours.SelectMany(points => points).ToArray();
        Assert.InRange(modelContourPoints.Min(point => point.X), 17, 23);
        Assert.InRange(modelContourPoints.Max(point => point.X), 86, 93);
        Assert.InRange(modelContourPoints.Min(point => point.Y), 17, 23);
        Assert.InRange(modelContourPoints.Max(point => point.Y), 106, 113);
        var asset = Assert.IsType<string>(trained.Summary["trainingModelData"]);
        Assert.NotEmpty(asset);

        var runtimeParameters = JsonSerializer.Serialize(new
        {
            modelMode = "trained", trainedModelData = asset,
            trainedModelConfig = trained.Summary["trainingConfig"],
            trainedWidth = trained.Summary["trainingWidth"], trainedHeight = trained.Summary["trainingHeight"],
            trainedOriginX = trained.Summary["trainingOriginX"], trainedOriginY = trained.Summary["trainingOriginY"],
            angleRangeDeg = 180, angleStepDeg = 2, trainingOriginMode = "centroid", trainingOriginX = 0, trainingOriginY = 0,
            minScore = 0.5, minArea = 100
        });
        var serializedNode = JsonSerializer.Serialize(new NodeDefinition("n1", "match.shape", "Shape", null, Params(runtimeParameters)));
        var restoredNode = JsonSerializer.Deserialize<NodeDefinition>(serializedNode, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var restoredParameters = JsonSerializer.Serialize(restoredNode.Parameters);
        typeof(ShapeMatchNode).Assembly.GetType("VisionStudio.Engine.Algorithms.JLVisionModelCache")!
            .GetMethod("Clear", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.Invoke(null, null);
        var matched = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene) }, restoredParameters);
        Assert.True(Convert.ToBoolean(matched.Summary["found"]), SummaryText(matched));
        Assert.Equal("workflow-asset", matched.Summary["modelSource"]);
        using var expectedTransform = PasteMatrix(165, 145, 20);
        var expectedPose = TemplateCentroidThrough(expectedTransform);
        Assert.InRange(matched.Outputs["x"].Require<double>("x"), expectedPose.X - 3, expectedPose.X + 3);
        Assert.InRange(matched.Outputs["y"].Require<double>("y"), expectedPose.Y - 3, expectedPose.Y + 3);

        // Run-only acceptance thresholds may change without invalidating the trained model.
        var changedScore = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
            restoredParameters.Replace("\"minScore\":0.5", "\"minScore\":0.51", StringComparison.Ordinal));
        Assert.True(Convert.ToBoolean(changedScore.Summary["found"]), SummaryText(changedScore));

        var stale = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new ShapeMatchNode(),
                new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
                restoredParameters.Replace("\"trainingOriginX\":0", "\"trainingOriginX\":1", StringComparison.Ordinal)));
        Assert.Contains("Train the model again", stale.Message);
        var staleRoi = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
                restoredParameters.Replace("\"trainingOriginX\":0", "\"trainingOriginX\":0,\"roi\":{\"type\":\"Rectangle\",\"x\":1,\"y\":1,\"width\":100,\"height\":100}", StringComparison.Ordinal)));
        Assert.Contains("Train the model again", staleRoi.Message);
    }

    [Fact]
    public async Task ShapeMatch_NewTrainedMode_RequiresExplicitTraining()
    {
        using var scene = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(0));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) }, """{"modelMode":"trained"}"""));
        Assert.Contains("requires a trained model", error.Message);
    }

    [Fact]
    public async Task ShapeMatch_ExplicitLegacyMode_UsesTemplateInputEvenWhenTrainedAssetExists()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = PasteMatrix(165, 145, 0))
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        var trained = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"modelMode":"trained","trainingMode":true,"angleRangeDeg":180,"angleStepDeg":2,"minArea":100}""");
        var asset = Assert.IsType<string>(trained.Summary["trainingModelData"]);
        var legacy = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            JsonSerializer.Serialize(new { modelMode = "legacy", trainedModelData = asset, trainedModelConfig = "deliberately-stale",
                angleRangeDeg = 180, angleStepDeg = 2, minArea = 100, minScore = 0.5 }));

        Assert.True(Convert.ToBoolean(legacy.Summary["found"]), SummaryText(legacy));
        Assert.Equal("input-port", legacy.Summary["templateSource"]);
        Assert.False(legacy.Summary.ContainsKey("modelSource"));
    }

    [Fact]
    public async Task ShapeMatch_TrainingPreviewAndTrain_SupportTemplatePathAndColorImages()
    {
        using var template = BuildTemplate();
        using var colorTemplate = new Mat();
        Cv2.CvtColor(template, colorTemplate, ColorConversionCodes.GRAY2BGR);
        var path = Path.Combine(Path.GetTempPath(), $"shape-template-{Guid.NewGuid():N}.png");
        Cv2.ImWrite(path, colorTemplate);
        try
        {
            using var scene = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
            var preview = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
                JsonSerializer.Serialize(new { modelMode = "trained", trainingMode = true, trainingPreviewOnly = true, templatePath = path }));
            Assert.True(Convert.ToBoolean(preview.Summary["trainingPreviewReady"]));
            Assert.Equal(120, preview.Outputs["image"].Require<VisionImage>("image").Width);

            var trained = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
                JsonSerializer.Serialize(new { modelMode = "trained", trainingMode = true, templatePath = path, angleRangeDeg = 180, angleStepDeg = 2 }));
            Assert.True(Convert.ToBoolean(trained.Summary["trainingSucceeded"]));
            Assert.Equal("template-path", trained.Summary["trainingSource"]);
            Assert.NotEmpty((string)trained.Summary["trainingPreviewImage"]!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RoiTransform_Translation_RectangleStaysRectangle()
    {
        var result = await Run(new RoiTransformNode(), new Dictionary<string, VisionValue>(),
            """{"roi":{"type":"Rectangle","x":100,"y":100,"width":50,"height":30},"baseX":0,"baseY":0,"baseThetaDeg":0,"currentX":200,"currentY":150,"currentThetaDeg":0}""");

        var roi = result.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(VisionRoiType.Rectangle, roi.Type);
        Assert.Equal(300, roi.X);
        Assert.Equal(250, roi.Y);
        Assert.Equal(50, roi.Width);
        Assert.Equal(30, roi.Height);

        var center = result.Outputs["center"].Require<VisionPoint2D>("center");
        Assert.InRange(center.X, 324.5, 325.5);
        Assert.InRange(center.Y, 264.5, 265.5);

        // transform 输出可直接复算：base(0,0) 点经变换到 current 平移。
        var transform = result.Outputs["transform"].Require<VisionTransform2D>("transform");
        var moved = transform.Apply(new VisionPoint2D(100, 100));
        Assert.InRange(moved.X, 299.5, 300.5);
        Assert.InRange(moved.Y, 249.5, 250.5);
    }

    [Fact]
    public async Task RoiTransform_Rotation_TurnsRectangleIntoPolygonWithTrueCorners()
    {
        var result = await Run(new RoiTransformNode(), new Dictionary<string, VisionValue>(),
            """{"roi":{"type":"Rectangle","x":100,"y":100,"width":50,"height":30},"baseThetaDeg":0,"currentThetaDeg":90}""");

        var roi = result.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(VisionRoiType.Polygon, roi.Type);
        Assert.NotNull(roi.Points);
        // R(90°) 逆时针（图像坐标）：(x,y) → (y,-x)。
        var expected = new[] { (100d, -100d), (100d, -150d), (130d, -150d), (130d, -100d) };
        foreach (var (x, y) in expected)
            Assert.Contains(roi.Points!, p => Math.Abs(p.X - x) < 0.01 && Math.Abs(p.Y - y) < 0.01);

        var summary = result.Summary;
        Assert.Equal(90d, Convert.ToDouble(summary["thetaDeg"]));
        Assert.Equal("Polygon", summary["roiType"]);
    }

    [Fact]
    public async Task RoiTransform_CircleTranslates_AndPortOverridesParameter()
    {
        var result = await Run(new RoiTransformNode(), new Dictionary<string, VisionValue>(),
            """{"roi":{"type":"Circle","x":50,"y":60,"radius":20},"currentX":10,"currentY":20}""");
        var roi = result.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(VisionRoiType.Circle, roi.Type);
        Assert.Equal(60, roi.X);
        Assert.Equal(80, roi.Y);
        Assert.Equal(20, roi.Radius);

        // 端口优先：端口传圆 ROI，参数里放一个不相干的矩形 ⇒ 输出以端口为准。
        var portResult = await Run(new RoiTransformNode(),
            new Dictionary<string, VisionValue>
            {
                ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Circle, X: 100, Y: 100, Radius: 30))
            },
            """{"roi":{"type":"Rectangle","x":0,"y":0,"width":1,"height":1},"currentX":5,"currentY":5}""");
        var portRoi = portResult.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(VisionRoiType.Circle, portRoi.Type);
        Assert.Equal(105, portRoi.X);
        Assert.Equal(105, portRoi.Y);
        Assert.Equal(30, portRoi.Radius);
    }

    [Fact]
    public async Task RoiTransform_RejectsMissingRoiAndNonFinitePose()
    {
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new RoiTransformNode(), new Dictionary<string, VisionValue>(), """{"currentX":1}"""));
        Assert.Contains("ROI", missing.Message);
        Assert.Contains("roi", missing.Message);

        var nonFinite = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new RoiTransformNode(),
                new Dictionary<string, VisionValue>
                {
                    ["roi"] = VisionValue.Roi(new VisionRoi(VisionRoiType.Rectangle, X: 0, Y: 0, Width: 10, Height: 10)),
                    ["currentPose"] = VisionValue.Pose(new VisionPose2D(double.NaN, 0, 0))
                },
                """{}"""));
        Assert.Contains("non-finite", nonFinite.Message);
    }

    [Fact]
    public void NewBatchFiveTypes_ArePresentInTheBuiltInCatalogWithContracts()
    {
        string[] expected =
        [
            "contour.extract", "contour.filter", "contour.properties",
            "feature.edgeSubpixel", "measure.caliperLine", "measure.caliperCircle", "measure.fitEllipse",
            "match.shape", "roi.transform"
        ];
        foreach (var type in expected)
            Assert.NotNull(BuiltInNodeCatalog.Items.FirstOrDefault(x => x.Type == type));

        var extract = BuiltInNodeCatalog.Require("contour.extract");
        Assert.Contains(extract.Inputs, x => x.Name == "roi" && x.DataType == VisionDataType.Roi && !x.Required);
        Assert.True(extract.Capabilities?.SupportsRoi);

        var caliperCircle = BuiltInNodeCatalog.Require("measure.caliperCircle");
        Assert.Contains(caliperCircle.Inputs, x => x.Name == "circle" && x.DataType == VisionDataType.Circle);

        var roiTransform = BuiltInNodeCatalog.Require("roi.transform");
        Assert.Contains(roiTransform.Inputs, x => x.Name == "roi" && x.DataType == VisionDataType.Roi);
        Assert.Contains(roiTransform.Outputs, x => x.Name == "roi" && x.DataType == VisionDataType.Roi);
        Assert.Contains(roiTransform.Outputs, x => x.Name == "circle" && x.DataType == VisionDataType.Circle);

        var shapeMatch = BuiltInNodeCatalog.Require("match.shape");
        Assert.Contains(shapeMatch.Inputs, x => x.Name == "template" && x.DataType == VisionDataType.Image && !x.Required);
        Assert.False(shapeMatch.Capabilities?.Deterministic ?? true);
    }

    // ================= 素材与辅助 =================

    /// <summary>不对称 L 形模板（120×120，亮 255）：竖臂 + 横臂相连，质心偏左下。</summary>
    private static Mat BuildTemplate()
    {
        var template = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(template, new Rect(20, 20, 30, 90), Scalar.All(255), -1);   // 竖臂 x20..49, y20..109
        Cv2.Rectangle(template, new Rect(20, 80, 70, 30), Scalar.All(255), -1);   // 横臂 x20..89, y80..109
        return template;
    }

    /// <summary>把 120×120 模板绕其中心旋转 angleDeg 后平移到 (cx,cy)。</summary>
    private static Mat PasteMatrix(double cx, double cy, double angleDeg)
    {
        var matrix = Cv2.GetRotationMatrix2D(new Point2f(60, 60), angleDeg, 1.0);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + cx - 60);
        matrix.Set(1, 2, matrix.At<double>(1, 2) + cy - 60);
        return matrix;
    }

    private static Mat ScaleMatrix(double cx, double cy, double scale)
    {
        var matrix = Cv2.GetRotationMatrix2D(new Point2f(60, 60), 0, scale);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + cx - 60);
        matrix.Set(1, 2, matrix.At<double>(1, 2) + cy - 60);
        return matrix;
    }

    /// <summary>模板质心经粘贴矩阵映射后的期望位置（与匹配器同源：Moments 质心）。</summary>
    private static VisionPoint2D TemplateCentroidThrough(Mat matrix)
    {
        using var template = BuildTemplate();
        Cv2.FindContours(template, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var contour = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        var moments = Cv2.Moments(contour, binaryImage: false);
        var cx = moments.M10 / moments.M00;
        var cy = moments.M01 / moments.M00;
        return new VisionPoint2D(
            matrix.At<double>(0, 0) * cx + matrix.At<double>(0, 1) * cy + matrix.At<double>(0, 2),
            matrix.At<double>(1, 0) * cx + matrix.At<double>(1, 1) * cy + matrix.At<double>(1, 2));
    }

    private static string SummaryText(NodeExecutorResult result) =>
        JsonSerializer.Serialize(result.Summary);

    private static Dictionary<string, JsonElement>? Params(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

    private static VisionValue Image(Mat mat) => VisionValue.Image(VisionImage.Own(mat.Clone()));

    private static void AssertImageOutput(NodeExecutorResult result)
    {
        var image = result.Outputs["image"].Require<VisionImage>("image");
        Assert.False(image.Mat.Empty());
        Assert.True(image.Width > 0 && image.Height > 0);
    }

    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, string parametersJson)
    {
        var result = await executor.ExecuteAsync(
            new NodeExecutionContext(inputs), new NodeDefinition("n1", executor.Type, null, null, Params(parametersJson)), CancellationToken.None);
        foreach (var value in inputs.Values)
            (value.Value as VisionImage)?.Dispose();
        return result;
    }
}
