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
    public async Task ShapeMatch_AnisotropicScale_RecoversIndependentHorizontalAndVerticalScale()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        const double scaleX = 1.12;
        const double scaleY = 0.90;
        const double angleDeg = 10;
        const double targetX = 220;
        const double targetY = 210;
        var angle = angleDeg * Math.PI / 180;
        var a = scaleX * Math.Cos(angle);
        var b = scaleY * Math.Sin(angle);
        var c = -scaleX * Math.Sin(angle);
        var d = scaleY * Math.Cos(angle);
        using (var matrix = new Mat(2, 3, MatType.CV_64FC1))
        {
            matrix.Set(0, 0, a); matrix.Set(0, 1, b);
            matrix.Set(1, 0, c); matrix.Set(1, 1, d);
            matrix.Set(0, 2, targetX - a * 60 - b * 60);
            matrix.Set(1, 2, targetY - c * 60 - d * 60);
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
        }

        var result = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"minScore":0.45,"angleRangeDeg":30,"angleStepDeg":2,"scaleToleranceXPercent":15,"scaleToleranceYPercent":15,"scaleStepPercent":2,"minArea":100}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]), SummaryText(result));
        Assert.InRange(result.Outputs["scaleX"].Require<double>("scaleX"), 1.08, 1.16);
        Assert.InRange(result.Outputs["scaleY"].Require<double>("scaleY"), 0.86, 0.94);
        Assert.InRange(result.Outputs["angle"].Require<double>("angle"), 8, 12);
        using var expectedTransform = MatrixForAnisotropicPose(scaleX, scaleY, angleDeg, targetX, targetY);
        var centroid = TemplateCentroidThrough(expectedTransform);
        Assert.InRange(result.Outputs["x"].Require<double>("x"), centroid.X - 4, centroid.X + 4);
        Assert.InRange(result.Outputs["y"].Require<double>("y"), centroid.Y - 4, centroid.Y + 4);
    }

    [Fact]
    public async Task ShapeMatch_ClutterRegion_ReturnsAndFiltersNativeClutterScore()
    {
        using var template = new Mat(160, 180, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(template, new Rect(20, 20, 30, 90), Scalar.All(255), -1);
        Cv2.Rectangle(template, new Rect(20, 80, 70, 30), Scalar.All(255), -1);
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using var matrix = PasteMatrix(200, 200, 0);
        Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        Point Map(double x, double y) => new(
            (int)Math.Round(matrix.At<double>(0, 0) * x + matrix.At<double>(0, 1) * y + matrix.At<double>(0, 2)),
            (int)Math.Round(matrix.At<double>(1, 0) * x + matrix.At<double>(1, 1) * y + matrix.At<double>(1, 2)));
        for (var x = 148; x <= 168; x += 5)
            Cv2.Line(scene, Map(x, 22), Map(x, 138), Scalar.All(255), 2);
        for (var y = 28; y <= 136; y += 8)
            Cv2.Line(scene, Map(146, y), Map(169, y), Scalar.All(255), 2);

        var parameters = JsonSerializer.Serialize(new
        {
            minScore = 0.4, angleRangeDeg = 15, angleStepDeg = 1, maxDeformationPx = 2,
            clutterContrast = 5, maxClutterScore = 1,
            clutterRoi = new { type = "Rectangle", x = 145, y = 20, width = 25, height = 120 },
            minArea = 100
        });
        var result = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) }, parameters);

        Assert.True(Convert.ToBoolean(result.Summary["found"]), SummaryText(result));
        Assert.Equal(2, Convert.ToInt32(result.Summary["maxDeformationPx"]));
        Assert.True(result.Outputs.TryGetValue("clutterScore", out var clutterValue));
        var clutterScore = clutterValue!.Require<double>("clutterScore");
        Assert.True(clutterScore > 0, SummaryText(result));
        Assert.Equal(clutterScore, Convert.ToDouble(result.Summary["matchedClutterScore"]), 6);

        var weightedParameters = parameters.Replace("\"clutterContrast\":5", "\"clutterContrast\":5,\"scoreUsingClutter\":true", StringComparison.Ordinal)
            .Replace("\"minScore\":0.4", "\"minScore\":0.05", StringComparison.Ordinal)
            .Replace("\"angleRangeDeg\":15", "\"angleRangeDeg\":0", StringComparison.Ordinal);
        var weighted = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) }, weightedParameters);
        Assert.True(Convert.ToBoolean(weighted.Summary["found"]), SummaryText(weighted));
        var nativeScore = Convert.ToDouble(weighted.Summary["matchedNativeScore"]);
        var adjustedScore = weighted.Outputs["score"].Require<double>("score");
        var matchedClutter = Convert.ToDouble(weighted.Summary["matchedClutterScore"]);
        Assert.Equal(nativeScore * (1 - Math.Clamp(matchedClutter, 0, 1)), adjustedScore, 6);
        Assert.Equal(nativeScore, weighted.Outputs["nativeScore"].Require<double>("nativeScore"), 6);

        var rejected = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            parameters.Replace("\"maxClutterScore\":1", "\"maxClutterScore\":0", StringComparison.Ordinal));
        Assert.False(Convert.ToBoolean(rejected.Summary["found"]), SummaryText(rejected));
    }

    [Fact]
    public async Task ShapeMatch_RuntimeContrastChecks_ReportStrengthAndRejectDimMatches()
    {
        using var template = BuildTemplate();
        using var dimmedTemplate = new Mat();
        template.ConvertTo(dimmedTemplate, MatType.CV_8UC1, .35);
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = PasteMatrix(210, 200, 6))
            Cv2.WarpAffine(dimmedTemplate, scene, matrix, scene.Size(), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        Dictionary<string, VisionValue> Inputs() => new() { ["image"] = Image(scene), ["template"] = Image(template) };
        const string baseParameters = "\"minScore\":0.4,\"angleRangeDeg\":15,\"angleStepDeg\":2,\"minArea\":100";

        var accepted = await Run(new ShapeMatchNode(), Inputs(),
            $"{{{baseParameters},\"runtimeContrastThreshold\":20,\"contrastFractionThreshold\":0.25}}");
        Assert.True(Convert.ToBoolean(accepted.Summary["found"]), SummaryText(accepted));
        Assert.True(accepted.Outputs["contrast"].Require<double>("contrast") > 20, SummaryText(accepted));
        var contrastFraction = accepted.Outputs["contrastFraction"].Require<double>("contrastFraction");
        Assert.InRange(contrastFraction, .25, .55);
        Assert.Equal(contrastFraction, Convert.ToDouble(accepted.Summary["matchedContrastFraction"]), 6);

        var rejectedByAbsolute = await Run(new ShapeMatchNode(), Inputs(),
            $"{{{baseParameters},\"runtimeContrastThreshold\":120}}");
        Assert.False(Convert.ToBoolean(rejectedByAbsolute.Summary["found"]), SummaryText(rejectedByAbsolute));

        var rejectedByFraction = await Run(new ShapeMatchNode(), Inputs(),
            $"{{{baseParameters},\"contrastFractionThreshold\":0.7}}");
        Assert.False(Convert.ToBoolean(rejectedByFraction.Summary["found"]), SummaryText(rejectedByFraction));
    }

    [Fact]
    public async Task ShapeMatch_MaximumLocalDeformation_UsesNativeFixedAndScaledSearches()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = PasteMatrix(215, 205, 8))
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        foreach (var scaleParameters in new[]
        {
            "",
            ",\"scaleTolerancePercent\":10,\"scaleStepPercent\":2",
            ",\"scaleToleranceXPercent\":10,\"scaleToleranceYPercent\":10,\"scaleStepPercent\":2"
        })
        {
            var result = await Run(new ShapeMatchNode(),
                new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
                $"{{\"minScore\":0.4,\"angleRangeDeg\":20,\"angleStepDeg\":2,\"maxDeformationPx\":2{scaleParameters},\"minArea\":100}}");

            Assert.True(Convert.ToBoolean(result.Summary["found"]), SummaryText(result));
            Assert.Equal(2, Convert.ToInt32(result.Summary["maxDeformationPx"]));
            Assert.True(result.Outputs["score"].Require<double>("score") > 0.4);
        }
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
        Cv2.ImEncode(".png", template, out var sourceImageBytes);

        var runtimeParameters = JsonSerializer.Serialize(new
        {
            modelMode = "trained", trainedModelData = asset,
            trainedModelConfig = trained.Summary["trainingConfig"],
            trainedWidth = trained.Summary["trainingWidth"], trainedHeight = trained.Summary["trainingHeight"],
            trainedOriginX = trained.Summary["trainingOriginX"], trainedOriginY = trained.Summary["trainingOriginY"],
            trainedOriginImageX = trained.Summary["trainingOriginImageX"], trainedOriginImageY = trained.Summary["trainingOriginImageY"],
            trainedSourceImage = Convert.ToBase64String(sourceImageBytes), contrastFractionThreshold = 0.8,
            angleRangeDeg = 180, angleStepDeg = 2, trainingOriginMode = "centroid", trainingOriginX = 0, trainingOriginY = 0,
            minScore = 0.5, minArea = 100, maxDeformationPx = 2
        });
        var serializedNode = JsonSerializer.Serialize(new NodeDefinition("n1", "match.shape", "Shape", null, Params(runtimeParameters)));
        var restoredNode = JsonSerializer.Deserialize<NodeDefinition>(serializedNode, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var restoredParameters = JsonSerializer.Serialize(restoredNode.Parameters);
        typeof(ShapeMatchNode).Assembly.GetType("VisionStudio.Engine.Algorithms.JLVisionModelCache")!
            .GetMethod("Clear", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.Invoke(null, null);
        var matched = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene) }, restoredParameters);
        Assert.True(Convert.ToBoolean(matched.Summary["found"]), SummaryText(matched));
        Assert.Equal(2, Convert.ToInt32(matched.Summary["maxDeformationPx"]));
        Assert.InRange(matched.Outputs["contrastFraction"].Require<double>("contrastFraction"), .8, 1);
        Assert.Equal("workflow-asset", matched.Summary["modelSource"]);
        using var expectedTransform = PasteMatrix(165, 145, 20);
        var expectedPose = TemplateCentroidThrough(expectedTransform);
        Assert.InRange(matched.Outputs["x"].Require<double>("x"), expectedPose.X - 3, expectedPose.X + 3);
        Assert.InRange(matched.Outputs["y"].Require<double>("y"), expectedPose.Y - 3, expectedPose.Y + 3);

        // A historical v2 fingerprint used angleRangeDeg and omitted pyramid depth and composite-vote settings.
        var legacyV2Config = "shape-v2||auto|128|False|180|2|none|centroid|0|0|0|0|False|5";
        var oldVersionParameters = restoredParameters.Replace(
            $"\"trainedModelConfig\":\"{trained.Summary["trainingConfig"]}\"",
            $"\"trainedModelConfig\":\"{legacyV2Config}\"", StringComparison.Ordinal);
        var oldVersionMatch = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) }, oldVersionParameters);
        Assert.True(Convert.ToBoolean(oldVersionMatch.Summary["found"]), SummaryText(oldVersionMatch));

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
    public async Task ShapeMatch_SearchAngleWindowCanNarrowWithoutRetraining()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = PasteMatrix(165, 145, 25, 1.1))
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        var trained = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"modelMode":"trained","trainingMode":true,"trainingAngleRangeDeg":30,"angleStepDeg":2,"pyramidLevels":3,"scaleTolerancePercent":20,"scaleStepPercent":5,"minArea":100}""");
        Assert.True(Convert.ToBoolean(trained.Summary["trainingSucceeded"]));
        Assert.Equal(3, Convert.ToInt32(trained.Summary["trainingPyramidLevels"]));
        var modelData = Assert.IsType<string>(trained.Summary["trainingModelData"]);
        var parameters = JsonSerializer.Serialize(new
        {
            modelMode = "trained", trainedModelData = modelData, trainedModelConfig = trained.Summary["trainingConfig"],
            trainedWidth = trained.Summary["trainingWidth"], trainedHeight = trained.Summary["trainingHeight"],
            trainedOriginX = trained.Summary["trainingOriginX"], trainedOriginY = trained.Summary["trainingOriginY"],
            trainingAngleRangeDeg = 30, angleStepDeg = 2, pyramidLevels = 3, scaleTolerancePercent = 20, scaleStepPercent = 5,
            trainingOriginMode = "centroid", trainingOriginX = 0, trainingOriginY = 0, minScore = 0.5, minArea = 100,
            searchAngleStartDeg = -10, searchAngleEndDeg = 10, searchScaleMinPercent = 80, searchScaleMaxPercent = 120
        });

        var narrowSearch = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) }, parameters);
        Assert.False(Convert.ToBoolean(narrowSearch.Summary["found"]), SummaryText(narrowSearch));
        Assert.Equal(-10d, Convert.ToDouble(narrowSearch.Summary["searchAngleStartDeg"]), 5);
        Assert.Equal(10d, Convert.ToDouble(narrowSearch.Summary["searchAngleEndDeg"]), 5);

        var narrowScaleSearch = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
            parameters.Replace("\"searchAngleStartDeg\":-10", "\"searchAngleStartDeg\":-90", StringComparison.Ordinal)
                .Replace("\"searchAngleEndDeg\":10", "\"searchAngleEndDeg\":90", StringComparison.Ordinal)
                .Replace("\"searchScaleMinPercent\":80", "\"searchScaleMinPercent\":95", StringComparison.Ordinal)
                .Replace("\"searchScaleMaxPercent\":120", "\"searchScaleMaxPercent\":105", StringComparison.Ordinal));
        Assert.False(Convert.ToBoolean(narrowScaleSearch.Summary["found"]), SummaryText(narrowScaleSearch));

        var wideSearch = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
            parameters.Replace("\"searchAngleStartDeg\":-10", "\"searchAngleStartDeg\":-90", StringComparison.Ordinal)
                .Replace("\"searchAngleEndDeg\":10", "\"searchAngleEndDeg\":90", StringComparison.Ordinal));
        Assert.True(Convert.ToBoolean(wideSearch.Summary["found"]), SummaryText(wideSearch));
        Assert.Equal(30d, Convert.ToDouble(wideSearch.Summary["trainingAngleCoverageDeg"]), 5);
        Assert.Equal(80d, Convert.ToDouble(wideSearch.Summary["searchScaleMinPercent"]), 5);
        Assert.Equal(120d, Convert.ToDouble(wideSearch.Summary["searchScaleMaxPercent"]), 5);

        var staleTrainingCoverage = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) },
                parameters.Replace("\"trainingAngleRangeDeg\":30", "\"trainingAngleRangeDeg\":31", StringComparison.Ordinal)));
        Assert.Contains("Train the model again", staleTrainingCoverage.Message);
    }

    [Fact]
    public async Task ShapeMatch_CombinedTrainingModelSet_MergesAndDeduplicatesCandidates()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = PasteMatrix(165, 145, 20))
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        var trained = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"modelMode":"trained","trainingMode":true,"trainingAngleRangeDeg":30,"angleStepDeg":2,"minArea":100}""");
        var modelData = Assert.IsType<string>(trained.Summary["trainingModelData"]);
        var variant = new
        {
            data = modelData,
            width = Convert.ToInt32(trained.Summary["trainingWidth"]),
            height = Convert.ToInt32(trained.Summary["trainingHeight"]),
            originX = Convert.ToDouble(trained.Summary["trainingOriginX"]),
            originY = Convert.ToDouble(trained.Summary["trainingOriginY"])
        };
        var secondTrained = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            JsonSerializer.Serialize(new { modelMode = "trained", trainingMode = true, trainingAngleRangeDeg = 30, angleStepDeg = 2, minArea = 100,
                trainingOriginOverrideX = variant.originX, trainingOriginOverrideY = variant.originY }));
        Assert.Equal(variant.originX, Convert.ToDouble(secondTrained.Summary["trainingOriginX"]));
        Assert.Equal(variant.originY, Convert.ToDouble(secondTrained.Summary["trainingOriginY"]));
        Assert.Equal(trained.Summary["trainingConfig"], secondTrained.Summary["trainingConfig"]);
        var secondVariant = new
        {
            data = Assert.IsType<string>(secondTrained.Summary["trainingModelData"]), variant.width, variant.height,
            originX = Convert.ToDouble(secondTrained.Summary["trainingOriginX"]), originY = Convert.ToDouble(secondTrained.Summary["trainingOriginY"])
        };
        var modelSet = "shape-model-set-v1:" + JsonSerializer.Serialize(new { version = 1, models = new[] { variant, secondVariant } });
        var parameters = JsonSerializer.Serialize(new
        {
            modelMode = "trained", trainedModelData = modelSet, trainedModelConfig = trained.Summary["trainingConfig"],
            trainedWidth = variant.width, trainedHeight = variant.height, trainedOriginX = variant.originX, trainedOriginY = variant.originY,
            trainingAngleRangeDeg = 30, angleStepDeg = 2, trainingOriginMode = "centroid", trainingOriginX = 0, trainingOriginY = 0,
            minScore = 0.5, minArea = 100, maxTargets = 5
        });

        var matched = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(scene) }, parameters);

        Assert.True(Convert.ToBoolean(matched.Summary["found"]), SummaryText(matched));
        Assert.Equal(2, Convert.ToInt32(matched.Summary["modelVariantCount"]));
        Assert.Equal("workflow-asset-set", matched.Summary["modelSource"]);
        Assert.Equal(1, Convert.ToInt32(matched.Outputs["count"].Require<long>("count")));
        using var expectedTransform = PasteMatrix(165, 145, 20);
        var expectedPose = TemplateCentroidThrough(expectedTransform);
        Assert.InRange(matched.Outputs["x"].Require<double>("x"), expectedPose.X - 3, expectedPose.X + 3);
        Assert.InRange(matched.Outputs["y"].Require<double>("y"), expectedPose.Y - 3, expectedPose.Y + 3);
    }

    [Fact]
    public async Task ShapeMatch_CompositeTraining_FusesFrequentContoursAndAlignsSamplePoses()
    {
        using var template = BuildTemplate();
        using var trainingInput = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        var samples = new List<Mat>();
        try
        {
            foreach (var (centerX, centerY, angle) in new[] { (150d, 150d, 0d), (175d, 125d, 20d), (130d, 170d, -15d) })
            {
                var sample = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
                using var matrix = PasteMatrix(centerX, centerY, angle);
                Cv2.WarpAffine(template, sample, matrix, sample.Size(), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
                samples.Add(sample);
            }
            // A feature found in only one sample should be filtered by the default 75% vote threshold.
            Cv2.Circle(samples[1], new Point(270, 260), 9, Scalar.All(255), -1);

            var sampleModels = new List<object>();
            foreach (var sample in samples)
            {
                Cv2.ImEncode(".png", sample, out var encoded);
                var trained = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(trainingInput) },
                    JsonSerializer.Serialize(new
                    {
                        modelMode = "trained", trainingMode = true, trainingImageData = Convert.ToBase64String(encoded),
                        trainingAngleRangeDeg = 45, angleStepDeg = 2, trainingMinSampleFraction = .75,
                        trainingFeatureTolerancePx = 2, trainingOriginMode = "centroid", minContrast = 5
                    }));
                Assert.True(Convert.ToBoolean(trained.Summary["trainingSucceeded"]), SummaryText(trained));
                sampleModels.Add(new
                {
                    data = Assert.IsType<string>(trained.Summary["trainingModelData"]),
                    width = Convert.ToInt32(trained.Summary["trainingWidth"]), height = Convert.ToInt32(trained.Summary["trainingHeight"]),
                    originX = Convert.ToDouble(trained.Summary["trainingOriginX"]), originY = Convert.ToDouble(trained.Summary["trainingOriginY"]),
                    sourceOriginX = Convert.ToDouble(trained.Summary["trainingOriginImageX"]), sourceOriginY = Convert.ToDouble(trained.Summary["trainingOriginImageY"])
                });
            }

            var modelSet = "shape-model-set-v1:" + JsonSerializer.Serialize(new { version = 1, models = sampleModels });
            var sourceImages = samples.Select(sample =>
            {
                Cv2.ImEncode(".png", sample, out var encoded);
                return Convert.ToBase64String(encoded);
            }).ToArray();
            var composite = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(trainingInput) },
                JsonSerializer.Serialize(new
                {
                    modelMode = "trained", trainingMode = true, trainingCombineModelData = modelSet,
                    trainingCombineSourceImages = JsonSerializer.Serialize(sourceImages),
                    trainingAngleRangeDeg = 45, angleStepDeg = 2, trainingMinSampleFraction = .75,
                    trainingFeatureTolerancePx = 2, trainingOriginMode = "centroid", minContrast = 5
                }));

            Assert.True(Convert.ToBoolean(composite.Summary["trainingSucceeded"]), SummaryText(composite));
            Assert.Equal("composite-shape-model", composite.Summary["trainingSource"]);
            Assert.Equal(3, Convert.ToInt32(composite.Summary["trainingSampleCount"]));
            Assert.Equal(3, Convert.ToInt32(composite.Summary["trainingMinimumSampleVotes"]));
            Assert.Equal(3, Assert.IsType<double[]>(composite.Summary["trainingSampleAlignmentScores"]).Length);
            var compositeData = Assert.IsType<string>(composite.Summary["trainingModelData"]);
            Assert.False(compositeData.StartsWith("shape-model-set-v1:", StringComparison.Ordinal));
            var contours = Assert.IsType<VisionOverlayPoint[][]>(composite.Summary["trainingContours"]);
            Assert.NotEmpty(contours);
            Assert.DoesNotContain(contours.SelectMany(contour => contour), point => point.X > 240 && point.Y > 230);

            using var runtimeScene = (Mat)samples[2].Clone();
            var matched = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(runtimeScene) },
                JsonSerializer.Serialize(new
                {
                    modelMode = "trained", trainedModelData = compositeData, trainedModelConfig = composite.Summary["trainingConfig"],
                    trainedWidth = composite.Summary["trainingWidth"], trainedHeight = composite.Summary["trainingHeight"],
                    trainedOriginX = composite.Summary["trainingOriginX"], trainedOriginY = composite.Summary["trainingOriginY"],
                    trainedPoseOffsetX = composite.Summary["trainingPoseOffsetX"], trainedPoseOffsetY = composite.Summary["trainingPoseOffsetY"],
                    trainingAngleRangeDeg = 45, angleStepDeg = 2, trainingMinSampleFraction = .75, trainingFeatureTolerancePx = 2,
                    trainingOriginMode = "centroid", minContrast = 5, minScore = .4, minArea = 100,
                    searchAngleStartDeg = -45, searchAngleEndDeg = 45
                }));
            Assert.True(Convert.ToBoolean(matched.Summary["found"]), SummaryText(matched));
            Assert.Equal("workflow-asset", matched.Summary["modelSource"]);
            using var expectedTransform = PasteMatrix(130, 170, -15);
            var expectedPose = TemplateCentroidThrough(expectedTransform);
            Assert.InRange(matched.Outputs["x"].Require<double>("x"), expectedPose.X - 4, expectedPose.X + 4);
            Assert.InRange(matched.Outputs["y"].Require<double>("y"), expectedPose.Y - 4, expectedPose.Y + 4);
        }
        finally
        {
            foreach (var sample in samples) sample.Dispose();
        }
    }

    [Fact]
    public async Task ShapeMatch_MultiSampleTraining_TransformsModelRegionWithReferencePose()
    {
        using var template = BuildTemplate();
        using var firstScene = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        using var secondScene = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        using (var firstTransform = PasteMatrix(115, 115, 0))
            Cv2.WarpAffine(template, firstScene, firstTransform, firstScene.Size(), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        using (var secondTransform = PasteMatrix(180, 145, 25))
            Cv2.WarpAffine(template, secondScene, secondTransform, secondScene.Size(), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));
        Cv2.ImEncode(".png", firstScene, out var firstImageData);
        Cv2.ImEncode(".png", secondScene, out var secondImageData);
        var region = new { type = "Rectangle", x = 65, y = 65, width = 100, height = 110 };
        var baseParameters = new
        {
            modelMode = "trained", trainingMode = true, roi = region,
            trainingAngleRangeDeg = 45, angleStepDeg = 2, trainingOriginMode = "centroid", minContrast = 5
        };
        var first = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(firstScene) },
            JsonSerializer.Serialize(new { baseParameters.modelMode, baseParameters.trainingMode, trainingImageData = Convert.ToBase64String(firstImageData),
                baseParameters.roi, baseParameters.trainingAngleRangeDeg, baseParameters.angleStepDeg, baseParameters.trainingOriginMode, baseParameters.minContrast }));
        Assert.True(Convert.ToBoolean(first.Summary["trainingSucceeded"]), SummaryText(first));

        var second = await Run(new ShapeMatchNode(), new Dictionary<string, VisionValue> { ["image"] = Image(secondScene) },
            JsonSerializer.Serialize(new
            {
                baseParameters.modelMode, baseParameters.trainingMode, trainingImageData = Convert.ToBase64String(secondImageData),
                baseParameters.roi, baseParameters.trainingAngleRangeDeg, baseParameters.angleStepDeg, baseParameters.trainingOriginMode, baseParameters.minContrast,
                trainingReferenceModelData = first.Summary["trainingModelData"],
                trainingReferenceOriginImageX = first.Summary["trainingOriginImageX"],
                trainingReferenceOriginImageY = first.Summary["trainingOriginImageY"]
            }));

        Assert.True(Convert.ToBoolean(second.Summary["trainingSucceeded"]), SummaryText(second));
        Assert.True(Convert.ToDouble(second.Summary["trainingRegionAlignmentScore"]) > .8, SummaryText(second));
        using var expectedTransform = PasteMatrix(180, 145, 25);
        var expectedOrigin = TemplateCentroidThrough(expectedTransform);
        Assert.InRange(Convert.ToDouble(second.Summary["trainingOriginImageX"]), expectedOrigin.X - 3, expectedOrigin.X + 3);
        Assert.InRange(Convert.ToDouble(second.Summary["trainingOriginImageY"]), expectedOrigin.Y - 3, expectedOrigin.Y + 3);
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
    public async Task ShapeMatchRoiTransformCropAndCaliperCircle_KeepReferenceGeometryAligned()
    {
        using var template = BuildTemplate();
        Cv2.Circle(template, new Point(80, 50), 12, Scalar.All(240), 2);
        using var scene = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(0));
        using (var matrix = PasteMatrix(230, 205, 28))
            Cv2.WarpAffine(template, scene, matrix, scene.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));

        const string matchParameters = """{"minScore":0.5,"angleRangeDeg":180,"angleStepDeg":1,"minArea":300,"signatureSamples":360}""";
        var referenceMatch = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(template), ["template"] = Image(template) }, matchParameters);
        var currentMatch = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) }, matchParameters);
        Assert.True(Convert.ToBoolean(referenceMatch.Summary["found"]), SummaryText(referenceMatch));
        Assert.True(Convert.ToBoolean(currentMatch.Summary["found"]), SummaryText(currentMatch));
        var basePose = referenceMatch.Outputs["pose"].Require<VisionPose2D>("pose");
        var currentPose = currentMatch.Outputs["pose"].Require<VisionPose2D>("pose");

        var corrected = await Run(new RoiTransformNode(),
            new Dictionary<string, VisionValue> { ["currentPose"] = VisionValue.Pose(currentPose) },
            JsonSerializer.Serialize(new
            {
                roi = new { type = "Circle", x = 80, y = 50, radius = 18 },
                baseX = basePose.X, baseY = basePose.Y, baseThetaDeg = basePose.ThetaDeg
            }));
        using var sceneImage = VisionImage.Own(scene.Clone());
        var crop = await new ImageCropNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(sceneImage),
                ["roi"] = corrected.Outputs["roi"]
            }),
            new NodeDefinition("crop", "image.crop", null, null, Params("{}")), CancellationToken.None);
        using var croppedImage = (VisionImage)crop.Outputs["image"].Require<IVisionImage>("image");

        var composed = await new ComposeTransformNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["transformA"] = corrected.Outputs["transform"],
                ["transformB"] = crop.Outputs["transform"]
            }),
            new NodeDefinition("compose", "coordinate.transformCompose", null, null, Params("{}")), CancellationToken.None);
        var composedTransform = composed.Outputs["transform"].Require<VisionTransform2D>("transform");
        Assert.Equal("ReferenceImage", composedTransform.SourceFrame);
        Assert.Equal("CropImage", composedTransform.TargetFrame);
        var expectedCircleCenter = composedTransform.Apply(new VisionPoint2D(80, 50));
        var measurement = await new CaliperCircleNode().ExecuteAsync(
            new NodeExecutionContext(new Dictionary<string, VisionValue>
            {
                ["image"] = VisionValue.Image(croppedImage),
                ["circle"] = VisionValue.Circle(new VisionCircle(80, 50, 12)),
                ["transform"] = composed.Outputs["transform"],
                ["roi"] = crop.Outputs["croppedRoi"]
            }),
            new NodeDefinition("caliper", "measure.caliperCircle", null, null,
                Params("""{"angleStartDeg":-180,"angleEndDeg":180,"samplingMode":"count","caliperCount":72,"radialBand":5,"sampleWidth":1,"minContrast":20,"polarity":"rising","searchDirection":"firstfromstart"}""")),
            CancellationToken.None);

        Assert.True(Convert.ToBoolean(measurement.Summary["found"]), SummaryText(measurement));
        var fitted = measurement.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.InRange(Math.Abs(fitted.X - expectedCircleCenter.X), 0, 1.5);
        Assert.InRange(Math.Abs(fitted.Y - expectedCircleCenter.Y), 0, 1.5);
        // The rising/first-from-start setup selects the ring's inner edge, not its centerline.
        Assert.InRange(fitted.Radius, 10, 11);

        (referenceMatch.Outputs["image"].Value as IDisposable)?.Dispose();
        (currentMatch.Outputs["image"].Value as IDisposable)?.Dispose();
        (crop.Outputs["roiMask"].Value as IDisposable)?.Dispose();
        (measurement.Outputs["image"].Value as IDisposable)?.Dispose();
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
    public async Task ShapeMatch_TrainingKeepsGrayModelWhenOriginThresholdHasNoContour()
    {
        using var template = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(50));
        Cv2.Rectangle(template, new Rect(20, 20, 30, 90), Scalar.All(100), -1);
        Cv2.Rectangle(template, new Rect(20, 80, 70, 30), Scalar.All(100), -1);
        using var scene = new Mat(240, 240, MatType.CV_8UC1, Scalar.All(0));
        var trained = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"modelMode":"trained","trainingMode":true,"binarize":"fixed","threshold":100,"angleStepDeg":2}""");

        Assert.True(Convert.ToBoolean(trained.Summary["trainingSucceeded"]), SummaryText(trained));
        Assert.Equal(59.5, Convert.ToDouble(trained.Summary["trainingOriginX"]));
        Assert.Equal(59.5, Convert.ToDouble(trained.Summary["trainingOriginY"]));
        Assert.True(Convert.ToInt32(trained.Summary["trainingFeaturePointCount"]) > 0);
        Assert.DoesNotContain(trained.Overlays!, overlay => overlay.Type == VisionOverlayType.Polygon && overlay.Id.StartsWith("shape-training-contour-", StringComparison.Ordinal));
        Assert.Contains(trained.Overlays!, overlay => overlay.Type == VisionOverlayType.Polyline && overlay.Id.StartsWith("shape-training-contour-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShapeMatch_TrainingWarnsWhenNativeFeaturesAreRotationallyAmbiguous()
    {
        using var template = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(template, new Point(40, 40), 20, Scalar.All(255), -1);
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        var trained = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"modelMode":"trained","trainingMode":true,"angleStepDeg":2}""");

        Assert.True(Convert.ToBoolean(trained.Summary["trainingSucceeded"]), SummaryText(trained));
        Assert.Equal(90, Convert.ToInt32(trained.Summary["trainingSymmetryPeriodDeg"]));
        var warnings = Assert.IsType<string[]>(trained.Summary["trainingWarnings"]);
        Assert.Contains(warnings, warning => warning.Contains("周期性歧义", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ShapeMatch_TrainingUsesCapturedImage_WhenUpstreamTemplateChanges()
    {
        using var template = BuildTemplate();
        using var scene = new Mat(300, 300, MatType.CV_8UC1, Scalar.All(0));
        var acquired = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(template) },
            """{"modelMode":"trained","trainingMode":true,"trainingPreviewOnly":true}""");
        var captured = Assert.IsType<string>(acquired.Summary["trainingImageData"]);
        using var changedTemplate = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        var trained = await Run(new ShapeMatchNode(),
            new Dictionary<string, VisionValue> { ["image"] = Image(scene), ["template"] = Image(changedTemplate) },
            JsonSerializer.Serialize(new { modelMode = "trained", trainingMode = true, trainingImageData = captured, angleStepDeg = 2 }));
        Assert.True(Convert.ToBoolean(trained.Summary["trainingSucceeded"]));
        var shown = trained.Outputs["image"].Require<VisionImage>("image");
        Assert.Equal(0d, Cv2.Norm(template, shown.Mat, NormTypes.INF));
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
    public async Task RoiTransform_HalfTurnSymmetry_UsesEquivalentOrientation()
    {
        var result = await Run(new RoiTransformNode(), new Dictionary<string, VisionValue>(),
            """{"roi":{"type":"Circle","x":320,"y":240,"radius":80},"baseX":128,"baseY":100.5,"baseThetaDeg":-180,"anglePeriodDeg":180,"currentX":173.56,"currentY":99.71,"currentThetaDeg":-5.77}""");

        var roi = result.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(VisionRoiType.Circle, roi.Type);
        Assert.InRange(roi.X!.Value, 350, 351);
        Assert.InRange(roi.Y!.Value, 256, 258);
        Assert.Equal(-5.77, Convert.ToDouble(result.Summary["thetaDeg"]), 2);
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
        var rectangle = result.Outputs["rectangle"].Require<VisionRectangle2D>("rectangle");
        Assert.Equal(60, rectangle.X);
        Assert.Equal(80, rectangle.Y);
        Assert.Equal(40, rectangle.Width);
        Assert.Equal(40, rectangle.Height);
        Assert.Equal(0, rectangle.AngleDeg);
        var boundsCircle = result.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.Equal(new VisionCircle(60, 80, 20), boundsCircle);

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
    public async Task RoiTransform_BrushBoundsFollowTheTransformedStrokeInsteadOfItsOldAxisAlignedBox()
    {
        var result = await Run(new RoiTransformNode(), new Dictionary<string, VisionValue>(),
            """{"roi":{"type":"Brush","strokes":[{"mode":"Add","width":10,"points":[{"x":20,"y":20},{"x":80,"y":80}]}]},"baseX":50,"baseY":50,"currentX":100,"currentY":100,"currentThetaDeg":45}""");

        var roi = result.Outputs["roi"].Require<VisionRoi>("roi");
        Assert.Equal(VisionRoiType.Brush, roi.Type);
        var rectangle = result.Outputs["rectangle"].Require<VisionRectangle2D>("rectangle");
        Assert.InRange(rectangle.Width, 94, 96);
        Assert.InRange(rectangle.Height, 9, 11);
        Assert.InRange(Math.Abs(rectangle.AngleDeg), 0, 0.1);
        var boundsCircle = result.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.Equal(100, boundsCircle.X, 3);
        Assert.Equal(100, boundsCircle.Y, 3);
        Assert.InRange(boundsCircle.Radius, 47.3, 47.6);
    }

    [Fact]
    public async Task RoiTransform_SinglePointBrushReportsItsBrushDiameter()
    {
        var result = await Run(new RoiTransformNode(), new Dictionary<string, VisionValue>(),
            """{"roi":{"type":"Brush","strokes":[{"mode":"Add","width":8,"points":[{"x":20,"y":30},{"x":20,"y":30}]}]}}""");

        var rectangle = result.Outputs["rectangle"].Require<VisionRectangle2D>("rectangle");
        Assert.Equal(20, rectangle.X);
        Assert.Equal(30, rectangle.Y);
        Assert.Equal(8, rectangle.Width);
        Assert.Equal(8, rectangle.Height);
        Assert.Equal(new VisionCircle(20, 30, 4), result.Outputs["circle"].Require<VisionCircle>("circle"));
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

        var singleCaliper = BuiltInNodeCatalog.Require("measure.caliper");
        Assert.Contains(singleCaliper.Inputs, x => x.Name == "roi" && x.DataType == VisionDataType.Roi && !x.Required);
        Assert.True(singleCaliper.Capabilities?.SupportsRoi);

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
    private static Mat PasteMatrix(double cx, double cy, double angleDeg, double scale = 1.0)
    {
        var matrix = Cv2.GetRotationMatrix2D(new Point2f(60, 60), angleDeg, scale);
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

    private static Mat MatrixForAnisotropicPose(double scaleX, double scaleY, double angleDeg, double targetX, double targetY)
    {
        var angle = angleDeg * Math.PI / 180;
        var a = scaleX * Math.Cos(angle);
        var b = scaleY * Math.Sin(angle);
        var c = -scaleX * Math.Sin(angle);
        var d = scaleY * Math.Cos(angle);
        var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, a); matrix.Set(0, 1, b);
        matrix.Set(1, 0, c); matrix.Set(1, 1, d);
        matrix.Set(0, 2, targetX - a * 60 - b * 60);
        matrix.Set(1, 2, targetY - c * 60 - d * 60);
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
