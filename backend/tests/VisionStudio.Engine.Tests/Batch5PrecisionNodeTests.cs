using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 第五批（2026-10-14 精密定位与测量）验收 · 节点级测试（5A 轮廓 / 5B 亚像素 / 5C 形状与 ROI）。
/// 每个节点覆盖：算法精度（已知真值的合成素材）、"未找到目标"（found=false 正常结果）、
/// 参数/数据错误（InvalidOperationException）与取消行为。
/// </summary>
public sealed class Batch5PrecisionNodeTests
{
    // ================= 5A 轮廓 =================

    [Fact]
    public async Task ContourExtract_Hierarchy_ExternalAndHolesAreDistinguished()
    {
        using var image = BuildRingImage(out var externalArea, out var holeArea);
        var all = await Run(new ContourExtractNode(), Image(image), """{"mode":"all","binarize":"auto"}""");
        var contours = all.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        var summary = all.Summary;

        Assert.Equal(2, contours.Count);
        Assert.Equal(2, Convert.ToInt32(summary["count"]));
        Assert.Equal(1, Convert.ToInt32(summary["holes"]));
        Assert.True(Convert.ToBoolean(summary["found"]));

        var external = contours.Single(x => !x.IsHole);
        var hole = contours.Single(x => x.IsHole);
        Assert.Equal(-1, external.ParentIndex);
        Assert.Equal(external.Index, hole.ParentIndex);
        Assert.InRange(external.Area, externalArea - 250, externalArea + 250);
        Assert.InRange(hole.Area, holeArea - 120, holeArea + 120);
        Assert.True(external.BoundingBox.Width > 100);

        var externalOnly = await Run(new ContourExtractNode(), Image(image), """{"mode":"external"}""");
        Assert.Equal(1, Convert.ToInt32(externalOnly.Summary["count"]));
        Assert.Single(externalOnly.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours"));

        var holesOnly = await Run(new ContourExtractNode(), Image(image), """{"mode":"holes"}""");
        var holes = holesOnly.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Single(holes);
        Assert.True(holes[0].IsHole);
    }

    [Fact]
    public async Task ContourExtract_BlankImage_IsFoundFalseNotFailure()
    {
        using var blank = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        var result = await Run(new ContourExtractNode(), Image(blank), """{"mode":"external"}""");
        Assert.False(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal(0, Convert.ToInt32(result.Summary["count"]));
        Assert.Empty(result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours"));
    }

    [Fact]
    public async Task ContourExtract_BinarizeModes_AndInvertHandleDarkObjects()
    {
        // 亮背景（200）+ 暗目标（0）：需要 invert 才把暗目标当前景。
        using var darkObject = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(200));
        Cv2.Rectangle(darkObject, new Rect(60, 60, 60, 60), Scalar.All(0), -1);

        var inverted = await Run(new ContourExtractNode(), Image(darkObject), """{"mode":"external","binarize":"auto","invert":true}""");
        var contours = inverted.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        Assert.Single(contours);
        Assert.InRange(contours[0].Area, 3300, 3700);
        Assert.Equal("otsu", inverted.Summary["binarize"]);

        // direct：输入端已是二值图，非零像素即前景。
        using var binary = new Mat(160, 160, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(binary, new Rect(20, 20, 40, 40), Scalar.All(255), -1);
        Cv2.Rectangle(binary, new Rect(100, 100, 30, 30), Scalar.All(255), -1);
        var direct = await Run(new ContourExtractNode(), Image(binary), """{"mode":"external","binarize":"direct","minArea":100}""");
        Assert.Equal(2, Convert.ToInt32(direct.Summary["count"]));

        // fixed：显式阈值 + 最小面积过滤（小目标被丢弃）。
        var fixedResult = await Run(new ContourExtractNode(), Image(binary), """{"mode":"external","binarize":"fixed","threshold":128,"minArea":1200}""");
        Assert.Equal("fixed", fixedResult.Summary["binarize"]);
        Assert.Equal(1, Convert.ToInt32(fixedResult.Summary["count"]));
    }

    [Fact]
    public async Task ContourFilter_AreaCircularityPositionAndHierarchy()
    {
        using var image = BuildRingImage(out _, out _);
        var extracted = await Run(new ContourExtractNode(), Image(image), """{"mode":"all"}""");
        var contours = extracted.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");

        // 面积：孔洞（约 1600）与外部（约 14400）分开。
        var bigOnly = await Run(new ContourFilterNode(), ContourInputs(contours), """{"minArea":8000}""");
        Assert.Equal(1, Convert.ToInt32(bigOnly.Summary["count"]));

        // 层级：仅孔洞。
        var holesOnly = await Run(new ContourFilterNode(), ContourInputs(contours), """{"hierarchy":"holes"}""");
        Assert.Equal("holes", holesOnly.Summary["hierarchy"]);
        Assert.Equal(1, Convert.ToInt32(holesOnly.Summary["count"]));

        // 圆度：外部轮廓是方形环，圆度约 0.78–0.99；把上限定 0.5 ⇒ 全部丢弃。
        var strict = await Run(new ContourFilterNode(), ContourInputs(contours), """{"maxCircularity":0.5,"hierarchy":"external"}""");
        Assert.Equal(0, Convert.ToInt32(strict.Summary["count"]));

        // 位置窗口：孔洞中心约 (80,80)、外部中心约 (100,100)——窗口只覆盖外部中心。
        var window = await Run(new ContourFilterNode(), ContourInputs(contours),
            """{"windowX":85,"windowY":85,"windowWidth":30,"windowHeight":30}""");
        Assert.Equal(1, Convert.ToInt32(window.Summary["count"]));

        // maxCount 截断计入 dropped。
        var capped = await Run(new ContourFilterNode(), ContourInputs(contours), """{"maxCount":1}""");
        Assert.Equal(1, Convert.ToInt32(capped.Summary["count"]));
        Assert.Equal(1, Convert.ToInt32(capped.Summary["dropped"]));
    }

    [Fact]
    public async Task ContourProperties_ComputesGeometryAndSelection()
    {
        using var image = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(image, new Rect(50, 50, 100, 100), Scalar.All(255), -1);
        var extracted = await Run(new ContourExtractNode(), Image(image), """{"mode":"external"}""");
        var contours = extracted.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");

        var result = await Run(new ContourPropertiesNode(), ContourInputs(contours), """{"selectBy":"largest"}""");
        var properties = result.Outputs["properties"].Require<IReadOnlyList<VisionContourProperties>>("properties");
        var selected = properties[0];

        Assert.InRange(selected.Area, 9500, 10300);
        Assert.InRange(selected.Circularity, 0.74, 0.80);            // 方形 ≈ π/4
        Assert.InRange(selected.Centroid.X, 98.5, 100.5);            // 方形中心 ≈ (99.5,99.5)
        Assert.InRange(selected.Centroid.Y, 98.5, 100.5);
        Assert.InRange(selected.Solidity, 0.95, 1.0);
        Assert.InRange(selected.Extent, 0.95, 1.0);
        Assert.InRange(selected.AspectRatio, 0.95, 1.05);
        Assert.InRange(selected.EquivalentDiameter, 108, 116);       // sqrt(4A/π)
        Assert.InRange(selected.MinAreaRect.Width, 96, 103);
        Assert.InRange(selected.MinAreaRect.Height, 96, 103);

        // DetectionList 复用输出 + 快捷几何输出。
        var detections = result.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Single(detections);
        Assert.Equal(0, Convert.ToInt32(result.Outputs["selected"].Require<long>("selected")));
        var center = result.Outputs["center"].Require<VisionPoint2D>("center");
        Assert.InRange(center.X, 98.5, 100.5);

        // 非对称形状：plus 轮廓的 solidity 明显小于 1。
        using var plus = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(plus, new Rect(80, 40, 40, 120), Scalar.All(255), -1);
        Cv2.Rectangle(plus, new Rect(40, 80, 120, 40), Scalar.All(255), -1);
        var plusExtract = await Run(new ContourExtractNode(), Image(plus), """{"mode":"external"}""");
        var plusProperties = await Run(new ContourPropertiesNode(),
            ContourInputs(plusExtract.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours")), """{}""");
        var plusSelected = plusExtract.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours")[0];
        Assert.True(plusSelected.Area > 0);
        var plusProps = plusProperties.Outputs["properties"].Require<IReadOnlyList<VisionContourProperties>>("properties")[0];
        Assert.InRange(plusProps.Solidity, 0.6, 0.95);               // plus 的凸包比自身大
        Assert.InRange(plusProps.Extent, 0.4, 0.75);                 // 面积/外接框 < 1

        // 空列表 ⇒ 明确报错（不是 found=false）。
        var empty = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new ContourPropertiesNode(),
                new Dictionary<string, VisionValue> { ["contours"] = VisionValue.Contours([]) }, """{}"""));
        Assert.Contains("at least one contour", empty.Message);
    }

    // ================= 5B 亚像素 =================

    [Fact]
    public async Task EdgeSubpixel_BlurredStepEdge_LocatesAtHalfPixelGroundTruth()
    {
        // 列 0–99 = 30、列 100–255 = 220：连续阶梯边缘在 x=99.5（像素中心语义）。
        using var image = BuildStepEdge(out var trueEdgeX);
        var result = await Run(new EdgeSubpixelNode(), Image(image),
            """{"startX":60,"startY":128,"endX":140,"endY":128,"sampleWidth":5,"minContrast":15,"polarity":"Any","direction":"strongest","refine":"parabolic"}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var point = result.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, trueEdgeX - 0.35, trueEdgeX + 0.35);
        Assert.InRange(point.Y, 127.5, 128.5);
        Assert.True(Convert.ToDouble(result.Outputs["strength"].Require<double>("strength")) > 15);
        Assert.True(Convert.ToInt32(result.Outputs["sampleCount"].Require<long>("sampleCount")) > 60);
        var fitError = result.Outputs["fitError"].Require<double>("fitError");
        Assert.True(fitError >= 0 && fitError < 6, $"fitError={fitError}");

        // refine=none：位置退化为采样中点（仍应在 0.6px 内）。
        var integer = await Run(new EdgeSubpixelNode(), Image(image),
            """{"startX":60,"startY":128,"endX":140,"endY":128,"sampleWidth":5,"minContrast":15,"refine":"none"}""");
        var integerPoint = integer.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(integerPoint.X, trueEdgeX - 0.6, trueEdgeX + 0.6);
    }

    [Fact]
    public async Task EdgeSubpixel_NoEdgeLowContrastAndPolarity_ReturnFoundFalse()
    {
        // 平坦图像：无边缘。
        using var flat = new Mat(256, 256, MatType.CV_8UC1, Scalar.All(128));
        var none = await Run(new EdgeSubpixelNode(), Image(flat), """{"startX":60,"startY":128,"endX":200,"endY":128}""");
        Assert.False(Convert.ToBoolean(none.Summary["found"]));
        Assert.Equal("no-edge-above-threshold", none.Summary["reason"]);
        Assert.False(none.Outputs.ContainsKey("point"));

        // 低对比度（6 级）低于 minContrast=10 ⇒ 未找到。
        using var lowContrast = new Mat(256, 256, MatType.CV_8UC1, Scalar.All(100));
        Cv2.Rectangle(lowContrast, new Rect(128, 0, 128, 256), Scalar.All(106), -1);
        Cv2.GaussianBlur(lowContrast, lowContrast, new Size(0, 0), 1.5);
        var low = await Run(new EdgeSubpixelNode(), Image(lowContrast), """{"startX":60,"startY":128,"endX":200,"endY":128,"minContrast":10}""");
        Assert.False(Convert.ToBoolean(low.Summary["found"]));

        // 极性：亮→暗沿搜索方向为下降沿。rising ⇒ 未找到；falling ⇒ 找到。
        using var darkRight = new Mat(256, 256, MatType.CV_8UC1, Scalar.All(220));
        Cv2.Rectangle(darkRight, new Rect(128, 0, 128, 256), Scalar.All(30), -1);
        Cv2.GaussianBlur(darkRight, darkRight, new Size(0, 0), 1.5);
        var rising = await Run(new EdgeSubpixelNode(), Image(darkRight), """{"startX":60,"startY":128,"endX":200,"endY":128,"polarity":"Rising","minContrast":15}""");
        var falling = await Run(new EdgeSubpixelNode(), Image(darkRight), """{"startX":60,"startY":128,"endX":200,"endY":128,"polarity":"Falling","minContrast":15}""");
        Assert.False(Convert.ToBoolean(rising.Summary["found"]));
        Assert.True(Convert.ToBoolean(falling.Summary["found"]));
        var fallingPoint = falling.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(fallingPoint.X, 127.0, 128.2);
    }

    [Fact]
    public async Task EdgeSubpixel_RoiMasksTheSearchRegion_AndShortLineFails()
    {
        using var image = BuildStepEdge(out _);
        // ROI 只覆盖 x<95：边缘 (x≈99.5) 被掩膜排除 ⇒ 未找到。
        var masked = await Run(new EdgeSubpixelNode(), Image(image),
            """{"startX":60,"startY":128,"endX":140,"endY":128,"minContrast":15,"roi":{"type":"Rectangle","x":0,"y":0,"width":95,"height":256}}""");
        Assert.False(Convert.ToBoolean(masked.Summary["found"]));
        Assert.Equal("Rectangle", masked.Summary["roi"]);

        // 零长度搜索线 ⇒ 参数错误。
        var shortLine = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new EdgeSubpixelNode(), Image(image), """{"startX":100,"startY":100,"endX":100,"endY":100}"""));
        Assert.Contains("too short", shortLine.Message);
    }

    [Fact]
    public async Task EdgeSubpixel_Cancellation_Throws()
    {
        using var image = BuildStepEdge(out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new EdgeSubpixelNode().ExecuteAsync(
                new NodeExecutionContext(new Dictionary<string, VisionValue> { ["image"] = Image(image) }),
                NodeOf("feature.edgeSubpixel", """{"startX":60,"startY":128,"endX":140,"endY":128}"""),
                cts.Token));
    }

    [Fact]
    public async Task CaliperLine_EdgeLineIsPerpendicularToSearchDirection()
    {
        using var image = BuildStepEdge(out var trueEdgeX);
        var result = await Run(new CaliperLineNode(), Image(image),
            """{"startX":60,"startY":128,"endX":140,"endY":128,"sampleWidth":5,"minContrast":15}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        var point = result.Outputs["point"].Require<VisionPoint2D>("point");
        Assert.InRange(point.X, trueEdgeX - 0.35, trueEdgeX + 0.35);

        var line = result.Outputs["line"].Require<VisionLine2D>("line");
        // 搜索方向水平 ⇒ 边缘线垂直：两端 x 相同、y 跨度为搜索线长度。
        Assert.InRange(Math.Abs(line.Start.X - line.End.X), 0, 0.2);
        Assert.InRange(Math.Abs(line.End.Y - line.Start.Y), 75, 85);
        Assert.InRange(line.Start.X, trueEdgeX - 0.35, trueEdgeX + 0.35);

        var searchLine = result.Outputs["searchLine"].Require<VisionLine2D>("searchLine");
        Assert.Equal(60, searchLine.Start.X);

        // 未找到 ⇒ found=false 且没有 line 输出。
        using var flat = new Mat(256, 256, MatType.CV_8UC1, Scalar.All(100));
        var none = await Run(new CaliperLineNode(), Image(flat), """{"startX":60,"startY":128,"endX":200,"endY":128}""");
        Assert.False(Convert.ToBoolean(none.Summary["found"]));
        Assert.False(none.Outputs.ContainsKey("line"));
    }

    [Fact]
    public async Task CaliperLine_RejectsIsolatedWrongEdgeAndReportsIgnoredPoint()
    {
        using var image = BuildStepEdge(out var trueEdgeX);
        // One caliper crosses a small dark patch and, searching from the end, finds its edge.
        Cv2.Rectangle(image, new Rect(120, 126, 20, 5), Scalar.All(30), -1);
        var result = await Run(new CaliperLineNode(), Image(image),
            """{"startX":60,"startY":128,"endX":140,"endY":128,"sampleWidth":5,"edgeLength":40,"caliperCount":9,"direction":"firstfromend","maxFitOutliers":2,"fitOutlierThreshold":2,"minContrast":15}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        Assert.True(Convert.ToInt32(result.Summary["ignoredByFit"]) >= 1);
        Assert.Equal(Convert.ToInt32(result.Summary["ignoredByFit"]), Convert.ToInt32(result.Outputs["ignoredCount"].Require<long>("ignoredCount")));
        var line = result.Outputs["line"].Require<VisionLine2D>("line");
        Assert.InRange(line.Start.X, trueEdgeX - 0.5, trueEdgeX + 0.5);
        Assert.InRange(Convert.ToDouble(result.Summary["fitError"]), 0, 0.5);
        Assert.Contains(result.Overlays ?? [], overlay => overlay.Id.StartsWith("caliper-line-ignored-point", StringComparison.Ordinal));

        var minimumAfterRejection = Convert.ToInt32(result.Summary["fitPoints"])
            + Convert.ToInt32(result.Summary["ignoredByFit"]);
        var strict = await Run(new CaliperLineNode(), Image(image), JsonSerializer.Serialize(new
        {
            startX = 60, startY = 128, endX = 140, endY = 128, sampleWidth = 5, edgeLength = 40,
            caliperCount = 9, direction = "firstfromend", maxFitOutliers = 2, fitOutlierThreshold = 2,
            minContrast = 15, minValidCalipers = minimumAfterRejection
        }));
        Assert.False(Convert.ToBoolean(strict.Summary["found"]));
        Assert.Equal("insufficient-valid-calipers", strict.Summary["reason"]);
        Assert.True(Convert.ToInt32(strict.Summary["fitPoints"]) < minimumAfterRejection);
        Assert.False(strict.Outputs.ContainsKey("line"));
    }

    [Fact]
    public async Task CaliperCircle_ConcentricCircle_RecoversRadiusAndRoundness()
    {
        using var image = BuildCircleImage(axes: null, out var truth);
        var result = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":12,"angleStepDeg":5,"minContrast":15,"polarity":"Any"}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal(72, Convert.ToInt32(result.Summary["spokes"]));
        Assert.Equal(5, Convert.ToDouble(result.Summary["angleStepDeg"]), 6);
        var radius = result.Outputs["radius"].Require<double>("radius");
        Assert.InRange(radius, truth.r - 0.8, truth.r + 0.8);
        var circle = result.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.InRange(circle.X, 199, 201);
        Assert.InRange(circle.Y, 199, 201);
        Assert.True(Convert.ToInt32(result.Outputs["sampleCount"].Require<long>("sampleCount")) >= 68);

        var fitError = result.Outputs["fitError"].Require<double>("fitError");
        Assert.True(fitError <= 1.0, $"fitError={fitError}");
        var spread = Convert.ToDouble(result.Outputs["radiusMax"].Require<double>("radiusMax"))
            - Convert.ToDouble(result.Outputs["radiusMin"].Require<double>("radiusMin"));
        Assert.InRange(spread, 0, 2.5);

        var denseLegacyStep = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":12,"samplingMode":"step","angleStepDeg":0.5,"minContrast":15}""");
        Assert.True(Convert.ToBoolean(denseLegacyStep.Summary["found"]));
        Assert.Equal(360, Convert.ToInt32(denseLegacyStep.Summary["spokes"]));
        Assert.Equal(1, Convert.ToDouble(denseLegacyStep.Summary["angleStepDeg"]), 6);

        // 部分弧（±45°）仍能拟合半径。
        var arc = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":12,"angleStepDeg":5,"angleStartDeg":-45,"angleEndDeg":45,"minContrast":15}""");
        Assert.True(Convert.ToBoolean(arc.Summary["found"]));
        Assert.InRange(arc.Outputs["radius"].Require<double>("radius"), truth.r - 1.5, truth.r + 1.5);
    }

    [Theory]
    [InlineData("angleStartDeg")]
    [InlineData("angleEndDeg")]
    [InlineData("angleStepDeg")]
    [InlineData("caliperCount")]
    [InlineData("radialBand")]
    [InlineData("minContrast")]
    [InlineData("sampleWidth")]
    [InlineData("maxRadiusDeviation")]
    [InlineData("maxFitOutliers")]
    [InlineData("fitOutlierThreshold")]
    [InlineData("fitRadiusTolerance")]
    public async Task CaliperCircle_RejectsNonFiniteNumericParameters(string parameterName)
    {
        using var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        var parameters = new Dictionary<string, object>
        {
            ["centerX"] = 32,
            ["centerY"] = 32,
            ["radius"] = 12,
            [parameterName] = "NaN"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(new CaliperCircleNode(), Image(image), JsonSerializer.Serialize(parameters)));
        Assert.Contains(parameterName, error.Message);
    }

    [Theory]
    [InlineData("measure.caliperLine", 4.5, 5)]
    [InlineData("measure.caliperLine", 5.5, 6)]
    [InlineData("measure.caliperCircle", 4.5, 5)]
    [InlineData("measure.caliperCircle", 5.5, 6)]
    public async Task CaliperCountsUseTheSameHalfUpRoundingAsThePreview(string nodeType, double requestedCount, int expectedCount)
    {
        using var image = new Mat(64, 64, MatType.CV_8UC1, Scalar.All(0));
        var isCircle = nodeType == "measure.caliperCircle";
        var parameters = isCircle
            ? JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["centerX"] = 32, ["centerY"] = 32, ["radius"] = 12,
                ["samplingMode"] = "count", ["caliperCount"] = requestedCount
            })
            : JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["startX"] = 10, ["startY"] = 32, ["endX"] = 50, ["endY"] = 32,
                ["edgeLength"] = 20, ["caliperCount"] = requestedCount
            });
        var result = await Run(isCircle ? new CaliperCircleNode() : new CaliperLineNode(), Image(image), parameters);
        var actualCount = Convert.ToInt32(result.Summary[isCircle ? "spokes" : "caliperCount"]);
        Assert.Equal(expectedCount, actualCount);
    }

    [Fact]
    public async Task CaliperCircle_CountModeWrapsArcsAndRejectsFitOutliers()
    {
        using var image = BuildCircleImage(axes: null, out var truth);

        var counted = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":12,"samplingMode":"count","caliperCount":36,"minContrast":15}""");
        Assert.True(Convert.ToBoolean(counted.Summary["found"]));
        Assert.Equal(36, Convert.ToInt32(counted.Summary["spokes"]));
        Assert.Equal(36, Convert.ToInt32(counted.Outputs["sampleCount"].Require<long>("sampleCount")));
        Assert.Equal(10, Convert.ToDouble(counted.Summary["angleStepDeg"]), 6);

        using var offsetCircle = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Ellipse(offsetCircle, new Point(203, 202), new Size(100, 100), 0, 0, 360, Scalar.All(220), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(offsetCircle, offsetCircle, new Size(0, 0), 1.5);
        var offset = await Run(new CaliperCircleNode(), Image(offsetCircle),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":12,"samplingMode":"count","caliperCount":72,"minContrast":15}""");
        var offsetFit = offset.Outputs["circle"].Require<VisionCircle>("circle");
        Assert.InRange(offsetFit.X, 202, 204);
        Assert.InRange(offsetFit.Y, 201, 203);
        var offsetSpread = offset.Outputs["radiusMax"].Require<double>("radiusMax") - offset.Outputs["radiusMin"].Require<double>("radiusMin");
        Assert.InRange(offsetSpread, 0, 2.5);

        // End before start means a clockwise arc crossing 0 degrees.
        var wrapped = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":12,"samplingMode":"count","caliperCount":25,"angleStartDeg":135,"angleEndDeg":-135,"minContrast":15}""");
        Assert.True(Convert.ToBoolean(wrapped.Summary["found"]));
        Assert.Equal(25, Convert.ToInt32(wrapped.Summary["spokes"]));
        Assert.InRange(wrapped.Outputs["radius"].Require<double>("radius"), truth.r - 2, truth.r + 2);

        // A small bright patch creates a competing outer edge on one spoke. First-from-end
        // deliberately selects it; bounded fit rejection should keep the main circle stable.
        Cv2.Rectangle(image, new Rect(305, 198, 7, 5), Scalar.All(220), -1);
        var robust = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":15,"samplingMode":"count","caliperCount":72,"searchDirection":"firstfromend","maxFitOutliers":2,"fitOutlierThreshold":2,"minContrast":15}""");
        Assert.True(Convert.ToBoolean(robust.Summary["found"]));
        Assert.True(Convert.ToInt32(robust.Outputs["ignoredCount"].Require<long>("ignoredCount")) >= 1);
        Assert.InRange(robust.Outputs["radius"].Require<double>("radius"), truth.r - 1, truth.r + 1);
        Assert.True(Convert.ToDouble(robust.Outputs["fitError"].Require<double>("fitError")) < 1);

        var minimumAfterRejection = Convert.ToInt32(robust.Summary["edgePoints"]);
        var strict = await Run(new CaliperCircleNode(), Image(image), JsonSerializer.Serialize(new
        {
            centerX = 200, centerY = 200, radius = 100, radialBand = 15, samplingMode = "count",
            caliperCount = 72, searchDirection = "firstfromend", maxFitOutliers = 2,
            fitOutlierThreshold = 2, minContrast = 15, minValidCalipers = minimumAfterRejection
        }));
        Assert.False(Convert.ToBoolean(strict.Summary["found"]));
        Assert.Equal("insufficient-valid-calipers", strict.Summary["reason"]);
        Assert.True(Convert.ToInt32(strict.Summary["fitPoints"]) < minimumAfterRejection);
        Assert.Equal(Convert.ToInt32(strict.Summary["fitPoints"]), Convert.ToInt32(strict.Summary["sampleCount"]));
        Assert.False(strict.Outputs.ContainsKey("circle"));

        using var oversized = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Ellipse(oversized, new Point(200, 200), new Size(104, 104), 0, 0, 360, Scalar.All(220), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(oversized, oversized, new Size(0, 0), 1.5);
        var constrained = await Run(new CaliperCircleNode(), Image(oversized),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":10,"samplingMode":"count","caliperCount":72,"fitRadiusTolerance":2,"minContrast":15}""");
        Assert.False(Convert.ToBoolean(constrained.Summary["found"]));
        Assert.Equal("fitted-radius-outside-tolerance", constrained.Summary["reason"]);
        Assert.False(constrained.Outputs.ContainsKey("circle"));
        Assert.InRange(Convert.ToDouble(constrained.Summary["fittedRadius"]), 103, 105);
    }

    [Fact]
    public async Task CaliperCircle_CirclePortOverrideAndFailureModes()
    {
        using var image = BuildCircleImage(axes: null, out var truth);

        // circle 端口覆盖参数：把标称圆心移到远离真实圆的背景区 ⇒ 找不到边缘。
        var portOverride = await Run(new CaliperCircleNode(),
            new Dictionary<string, VisionValue>
            {
                ["image"] = Image(image),
                ["circle"] = VisionValue.Circle(new VisionCircle(60, 60, 30))
            },
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":8,"angleStepDeg":5,"minContrast":15}""");
        Assert.False(Convert.ToBoolean(portOverride.Summary["found"]));
        Assert.Equal("no-edge-above-threshold", portOverride.Summary["reason"]);

        // 有效边缘点 < 3 ⇒ 退化拟合，found=false。
        var single = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":200,"centerY":200,"radius":100,"radialBand":12,"angleStartDeg":0,"angleEndDeg":0,"minContrast":15}""");
        Assert.False(Convert.ToBoolean(single.Summary["found"]));
        Assert.Equal("too-few-edge-points", single.Summary["reason"]);

        // 半径偏离闸门：标称 92、带宽 ±15（覆盖真实边缘 100），deviation=3 时全部被拒。
        var rejected = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":200,"centerY":200,"radius":92,"radialBand":15,"angleStepDeg":5,"minContrast":15,"maxRadiusDeviation":3}""");
        Assert.False(Convert.ToBoolean(rejected.Summary["found"]));
        Assert.Equal("all-spokes-exceed-max-radius-deviation", rejected.Summary["reason"]);
        Assert.True(Convert.ToInt32(rejected.Summary["rejectedByDeviation"]) >= 60);

        // 非正半径 ⇒ 参数错误。
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new CaliperCircleNode(), Image(image), """{"centerX":200,"centerY":200,"radius":0}"""));
        Assert.Contains("positive radius", error.Message);
    }

    [Fact]
    public async Task CaliperCircle_RadialSearchStopsAtTheCenterForSmallNominalRadius()
    {
        using var image = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Circle(image, new Point(64, 64), 6, Scalar.All(220), -1, LineTypes.AntiAlias);
        Cv2.Circle(image, new Point(64, 64), 4, Scalar.All(30), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(image, image, new Size(0, 0), 0.8);

        var result = await Run(new CaliperCircleNode(), Image(image),
            """{"centerX":64,"centerY":64,"radius":1,"radialBand":8,"samplingMode":"count","caliperCount":25,"angleStartDeg":0,"angleEndDeg":60,"searchDirection":"firstfromstart","polarity":"rising","sampleWidth":1,"minContrast":15}""");

        Assert.True(Convert.ToBoolean(result.Summary["found"]));
        Assert.Equal(0, Convert.ToDouble(result.Summary["radialSearchMinRadius"]));
        Assert.InRange(result.Outputs["radius"].Require<double>("radius"), 4.2, 5.3);
        var overlays = result.Overlays ?? [];
        var points = overlays.Where(overlay => overlay.Id.StartsWith("caliper-circle-point-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(25, overlays.Count(overlay => overlay.Id.StartsWith("caliper-circle-search-", StringComparison.Ordinal)));
        Assert.NotEmpty(points);
        Assert.True(points.Average(point => point.X) > 64);
        Assert.True(points.Average(point => point.Y) > 64);
    }

    [Fact]
    public async Task FitEllipse_RecoversAxesAndAngle()
    {
        using var image = new Mat(300, 400, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Ellipse(image, new Point(200, 150), new Size(120, 60), 30, 0, 360, Scalar.All(255), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(image, image, new Size(0, 0), 1.5);

        var extracted = await Run(new ContourExtractNode(), Image(image), """{"mode":"external","binarize":"auto"}""");
        var contours = extracted.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours");
        var result = await Run(new FitEllipseNode(), ContourInputs(contours), """{"selectBy":"largest"}""");

        var width = result.Outputs["width"].Require<double>("width");
        var height = result.Outputs["height"].Require<double>("height");
        var angle = result.Outputs["angle"].Require<double>("angle");
        var center = result.Outputs["center"].Require<VisionPoint2D>("center");

        // Cv2.Ellipse 的 Size 是半轴：(120,60) ⇒ 全长 240×120。
        Assert.InRange(width, 236, 244);
        Assert.InRange(height, 116, 124);
        Assert.InRange(NormalizeAxisAngleDiff(angle, 30), 0, 3);
        Assert.InRange(center.X, 198, 202);
        Assert.InRange(center.Y, 148, 152);

        // 轮廓透传 + pose 输出。
        Assert.Equal(contours.Count, result.Outputs["contours"].Require<IReadOnlyList<VisionContour>>("contours").Count);

        // 退化（点数 < 5）与空列表 ⇒ 参数/数据错误。
        var sparse = new VisionContour(0, [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], -1, false, 1, 4, 0.7,
            new VisionRectangle2D(0, 0, 1, 1), new VisionCircle(0.5, 0.5, 0.7));
        var degenerate = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new FitEllipseNode(), ContourInputs([sparse]), """{}"""));
        Assert.Contains("at least 5", degenerate.Message);

        var empty = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await Run(new FitEllipseNode(), new Dictionary<string, VisionValue> { ["contours"] = VisionValue.Contours([]) }, """{}"""));
        Assert.Contains("at least one contour", empty.Message);
    }

    // ================= 测试素材与辅助 =================

    /// <summary>环形素材：白色方环 120×120（开口 40×40）+ 已知面积。</summary>
    private static Mat BuildRingImage(out double externalArea, out double holeArea)
    {
        var image = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(image, new Rect(40, 40, 120, 120), Scalar.All(255), -1);
        Cv2.Rectangle(image, new Rect(60, 60, 40, 40), Scalar.All(0), -1);
        externalArea = 120 * 120;   // 外轮廓多边形面积 ≈ 14400
        holeArea = 40 * 40;         // 孔洞多边形面积 ≈ 1600
        return image;
    }

    /// <summary>模糊阶梯边缘：左 30 / 右 220，连续边缘在 x=99.5。</summary>
    private static Mat BuildStepEdge(out double trueEdgeX)
    {
        var image = new Mat(256, 256, MatType.CV_8UC1, Scalar.All(30));
        Cv2.Rectangle(image, new Rect(0, 0, 100, 256), Scalar.All(30), -1);
        Cv2.Rectangle(image, new Rect(100, 0, 156, 256), Scalar.All(220), -1);
        Cv2.GaussianBlur(image, image, new Size(0, 0), 1.5);
        trueEdgeX = 99.5;
        return image;
    }

    /// <summary>抗锯齿圆（或椭圆）素材。axes=null 时画半径 100 的圆。</summary>
    private static Mat BuildCircleImage(Size? axes, out (double x, double y, double r) truth)
    {
        var image = new Mat(400, 400, MatType.CV_8UC1, Scalar.All(30));
        var size = axes ?? new Size(100, 100);
        Cv2.Ellipse(image, new Point(200, 200), size, 0, 0, 360, Scalar.All(220), -1, LineTypes.AntiAlias);
        Cv2.GaussianBlur(image, image, new Size(0, 0), 1.5);
        truth = (200, 200, size.Width);
        return image;
    }

    private static double NormalizeAxisAngleDiff(double a, double b)
    {
        var diff = Math.Abs(a - b) % 180;
        return diff > 90 ? 180 - diff : diff;
    }

    private static Dictionary<string, VisionValue> ContourInputs(IReadOnlyList<VisionContour> contours) =>
        new() { ["contours"] = VisionValue.Contours(contours) };

    private static Dictionary<string, JsonElement>? Params(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

    private static NodeDefinition NodeOf(string type, string json) => new("n1", type, null, null, Params(json));

    private static VisionValue Image(Mat mat) => VisionValue.Image(VisionImage.Own(mat.Clone()));

    private static Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, VisionValue image, string parametersJson)
        => Run(executor, new Dictionary<string, VisionValue> { ["image"] = image }, parametersJson);

    private static async Task<NodeExecutorResult> Run(IVisionNodeExecutor executor, Dictionary<string, VisionValue> inputs, string parametersJson)
    {
        var result = await executor.ExecuteAsync(new NodeExecutionContext(inputs), NodeOf(executor.Type, parametersJson), CancellationToken.None);
        foreach (var value in inputs.Values)
            (value.Value as VisionImage)?.Dispose();
        return result;
    }
}
