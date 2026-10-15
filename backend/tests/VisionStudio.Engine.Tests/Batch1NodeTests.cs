using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Nodes;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 批次一（2026-10-13 节点补齐）验收：灰度/通道 → 滤波 → 形态学 → 自动阈值 → 多 Blob → 模板定位 → 数值/布尔判定。
/// 每个节点给出"能扩大现有系统可完成检测任务"的行为证据（含失败路径），
/// 另附目录参数校验（编译期拒绝非法参数，与既有 ParameterValidationTests 同风格）。
/// </summary>
public sealed class Batch1NodeTests
{
    // ---------- 灰度 / 通道 ----------

    [Fact]
    public async Task Grayscale_LuminanceAverageAndMax_ProduceExpectedValues()
    {
        using var color = new Mat(10, 10, MatType.CV_8UC3, new Scalar(10, 20, 30)); // BGR
        var luminance = await Run(new GrayscaleNode(), Image(color), """{"mode":"luminance"}""");
        var average = await Run(new GrayscaleNode(), Image(color), """{"mode":"average"}""");
        var max = await Run(new GrayscaleNode(), Image(color), """{"mode":"max"}""");

        using var luminanceMat = TakeImage(luminance);
        using var averageMat = TakeImage(average);
        using var maxMat = TakeImage(max);
        Assert.Equal(1, luminanceMat.Channels());
        // BT.601 for BGR: 0.114*10 + 0.587*20 + 0.299*30 ≈ 21.85
        Assert.InRange(luminanceMat.At<byte>(5, 5), 21, 23);
        Assert.InRange(averageMat.At<byte>(5, 5), 19, 21); // (10+20+30)/3 = 20
        Assert.Equal(30, maxMat.At<byte>(5, 5));
    }

    [Fact]
    public async Task Grayscale_SingleChannelInput_PassesThrough()
    {
        using var gray = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(77));
        var result = await Run(new GrayscaleNode(), Image(gray), """{"mode":"luminance"}""");
        using var output = TakeImage(result);
        Assert.Equal(77, output.At<byte>(4, 4));
        Assert.Equal("passthrough", result.Summary["mode"]);
    }

    [Fact]
    public async Task Channel_ExtractsColorAndHsvChannels()
    {
        using var color = new Mat(6, 6, MatType.CV_8UC3, new Scalar(10, 20, 30)); // B=10 G=20 R=30
        var blue = await Run(new ChannelExtractNode(), Image(color), """{"channel":"blue"}""");
        var red = await Run(new ChannelExtractNode(), Image(color), """{"channel":"red"}""");
        var hue = await Run(new ChannelExtractNode(), Image(color), """{"channel":"hue"}""");

        using var blueMat = TakeImage(blue);
        using var redMat = TakeImage(red);
        using var hueMat = TakeImage(hue);
        Assert.Equal(10, blueMat.At<byte>(3, 3));
        Assert.Equal(30, redMat.At<byte>(3, 3));
        Assert.True(hueMat.At<byte>(3, 3) > 0); // BGR(10,20,30) ⇒ 非零色相

        // 单通道输入取彩色通道 ⇒ 明确报错（不是静默错值）。
        using var gray = new Mat(4, 4, MatType.CV_8UC1, Scalar.All(1));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Run(new ChannelExtractNode(), Image(gray), """{"channel":"red"}"""));
        Assert.Contains("requires a color image", error.Message);
    }

    [Fact]
    public async Task Channel_UnifiesLuminanceAverageAndMaximumGrayModes()
    {
        using var color = new Mat(10, 10, MatType.CV_8UC3, new Scalar(10, 20, 30));
        var gray = await Run(new ChannelExtractNode(), Image(color), """{"channel":"gray"}""");
        var average = await Run(new ChannelExtractNode(), Image(color), """{"channel":"average"}""");
        var max = await Run(new ChannelExtractNode(), Image(color), """{"channel":"max"}""");
        using var grayMat = TakeImage(gray);
        using var averageMat = TakeImage(average);
        using var maxMat = TakeImage(max);
        Assert.InRange(grayMat.At<byte>(5, 5), 21, 23);
        Assert.InRange(averageMat.At<byte>(5, 5), 19, 21);
        Assert.Equal(30, maxMat.At<byte>(5, 5));
    }

    // ---------- 滤波 ----------

    [Fact]
    public async Task GaussianBlur_SmoothsEdges_AndCoercesEvenKernelToOdd()
    {
        using var image = new Mat(60, 60, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(image, new Rect(20, 20, 20, 20), Scalar.All(255), -1);
        var result = await Run(new GaussianBlurNode(), Image(image), """{"kernelSize":4}""");
        using var blurred = TakeImage(result);

        Assert.Equal(5, result.Summary["kernelSize"]);                 // 偶数核收敛为奇数
        Assert.Equal(255, blurred.At<byte>(30, 30));                   // 内部保持
        Assert.True(blurred.At<byte>(30, 18) > 0, "edge must bleed outward after blur");
        Assert.True(blurred.At<byte>(30, 5) < 200, "far background must stay dark");
    }

    [Fact]
    public async Task MedianBlur_RemovesSaltPepperNoise()
    {
        using var image = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(0));
        var random = new Random(7);
        for (var i = 0; i < 60; i++) image.At<byte>(random.Next(1, 79), random.Next(1, 79)) = 255;

        var result = await Run(new MedianBlurNode(), Image(image), """{"kernelSize":5}""");
        using var filtered = TakeImage(result);

        var white = 0;
        var rows = filtered.Rows;
        var cols = filtered.Cols;
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
            if (filtered.At<byte>(y, x) > 128) white++;
        Assert.Equal(0, white); // 孤立脉冲被完全移除
    }

    [Fact]
    public async Task UnifiedImageFilter_SelectsGaussianOrMedianBehavior()
    {
        using var image = new Mat(21, 21, MatType.CV_8UC1, Scalar.All(0));
        image.Set(10, 10, (byte)255);

        var gaussian = await Run(new ImageFilterNode(), Image(image), """{"method":"gaussian","kernelSize":5}""");
        using var gaussianOutput = TakeImage(gaussian);
        var median = await Run(new ImageFilterNode(), Image(image), """{"method":"median","kernelSize":5,"sigmaX":"inactive"}""");
        using var medianOutput = TakeImage(median);

        Assert.Equal("gaussian", gaussian.Summary["method"]);
        Assert.True(gaussianOutput.At<byte>(10, 10) > 0);
        Assert.Equal("median", median.Summary["method"]);
        Assert.Equal(0, medianOutput.At<byte>(10, 10));
    }

    // ---------- 形态学 ----------

    [Fact]
    public async Task Morphology_OpenRemovesBurrs_CloseFillsPinholes()
    {
        using var burred = new Mat(60, 60, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(burred, new Rect(15, 15, 30, 30), Scalar.All(255), -1);
        Cv2.Line(burred, new Point(44, 30), new Point(55, 30), Scalar.All(255), 1); // 1px 毛刺
        var opened = await Run(new MorphologyNode(), Image(burred), """{"operation":"open","kernelSize":3}""");
        using var openedMat = TakeImage(opened);
        Assert.Equal(0, openedMat.At<byte>(30, 52));                    // 毛刺被开运算去除
        Assert.Equal(255, openedMat.At<byte>(30, 30));                  // 本体保留

        using var holed = new Mat(60, 60, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Rectangle(holed, new Rect(15, 15, 30, 30), Scalar.All(255), -1);
        Cv2.Circle(holed, new Point(30, 30), 1, Scalar.All(0), -1);      // 1px 针孔
        var closed = await Run(new MorphologyNode(), Image(holed), """{"operation":"close","kernelSize":3}""");
        using var closedMat = TakeImage(closed);
        Assert.Equal(255, closedMat.At<byte>(30, 30));                  // 针孔被闭运算填充

        var eroded = await Run(new MorphologyNode(), Image(burred), """{"operation":"erode","kernelSize":5}""");
        using var erodedMat = TakeImage(eroded);
        Assert.Equal(0, erodedMat.At<byte>(30, 16));                    // 腐蚀收缩边界
    }

    [Theory]
    [InlineData("whiteTopHat")]
    [InlineData("blackBottomHat")]
    [InlineData("gradient")]
    public async Task UnifiedMorphology_ExposesGrayFeatureOperations(string operation)
    {
        using var image = new Mat(31, 31, MatType.CV_8UC1, Scalar.All(128));
        Cv2.Rectangle(image, new Rect(13, 13, 5, 5), Scalar.All(operation == "blackBottomHat" ? 0 : 255), -1);

        var result = await Run(new MorphologyNode(), Image(image), $$"""{"operation":"{{operation}}","kernelWidth":9,"kernelHeight":9,"kernelSize":"inactive"}""");
        using var output = TakeImage(result);

        Assert.Equal(operation is "whiteTopHat" ? "topHat" : operation is "blackBottomHat" ? "bottomHat" : "gradient", result.Summary["operation"]);
        Assert.True(Cv2.CountNonZero(output) > 0);
    }

    // ---------- 自动阈值 ----------

    [Fact]
    public async Task AdaptiveThreshold_HandlesUnevenIllumination_BothRegionsBinarize()
    {
        using var image = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(0));
        // 左暗右亮的背景渐变 + 两个比各自局部背景更暗的物体
        for (var y = 0; y < 120; y++)
        for (var x = 0; x < 120; x++)
            image.At<byte>(y, x) = (byte)(100 + x * 0.8); // 100..196 渐变
        Cv2.Rectangle(image, new Rect(20, 50, 20, 20), Scalar.All(60), -1);   // 暗背景上的暗块
        Cv2.Rectangle(image, new Rect(85, 50, 20, 20), Scalar.All(120), -1);  // 亮背景上的暗块

        var result = await Run(new AdaptiveThresholdNode(), Image(image), """{"blockSize":31,"c":10}""");
        using var binary = TakeImage(result);

        Assert.Equal(0, binary.At<byte>(60, 30));    // 暗侧物体 → 黑
        Assert.Equal(0, binary.At<byte>(60, 95));    // 亮侧物体 → 黑（固定阈值做不到）
        Assert.Equal(255, binary.At<byte>(10, 10));  // 暗侧背景 → 白
        Assert.Equal(255, binary.At<byte>(10, 110)); // 亮侧背景 → 白
    }

    [Fact]
    public async Task OtsuThreshold_ComputesBimodalThreshold_AndExposesIt()
    {
        using var image = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(200));
        Cv2.Rectangle(image, new Rect(0, 0, 40, 100), Scalar.All(50), -1);
        var result = await Run(new OtsuThresholdNode(), Image(image), """{"invert":false}""");
        using var binary = TakeImage(result);

        var threshold = (double)result.Summary["threshold"]!;
        Assert.InRange(threshold, 50, 199);            // 两峰之间（OpenCV 可选暗峰上界 50）
        Assert.Equal(0, binary.At<byte>(50, 20));      // 暗区 → 黑
        Assert.Equal(255, binary.At<byte>(50, 80));    // 亮区 → 白
    }

    [Fact]
    public async Task UnifiedThreshold_DispatchesManualAdaptiveAndOtsuModes()
    {
        using var image = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(200));
        Cv2.Rectangle(image, new Rect(0, 0, 40, 100), Scalar.All(50), -1);

        var manual = await Run(new ThresholdNode(), Image(image), """{"mode":"manual","threshold":100,"invert":true}""");
        using var manualImage = TakeImage(manual);
        Assert.Equal(100d, (double)manual.Outputs["threshold"].Value!);
        Assert.Equal(255, manualImage.At<byte>(50, 20));
        Assert.Equal(0, manualImage.At<byte>(50, 80));

        var otsu = await Run(new ThresholdNode(), Image(image), """{"mode":"otsu"}""");
        using var otsuImage = TakeImage(otsu);
        Assert.True(otsu.Outputs.ContainsKey("threshold"));
        Assert.Equal(0, otsuImage.At<byte>(50, 20));
        Assert.Equal(255, otsuImage.At<byte>(50, 80));

        var adaptive = await Run(new ThresholdNode(), Image(image), """{"mode":"adaptive","blockSize":15,"c":5,"method":"mean"}""");
        using var adaptiveImage = TakeImage(adaptive);
        Assert.False(adaptive.Outputs.ContainsKey("threshold"));
        Assert.Equal(100, adaptiveImage.Rows);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new ThresholdNode(), Image(image), """{"mode":"unknown"}"""));
    }

    // ---------- 多 Blob ----------

    [Fact]
    public async Task MultiBlob_FiltersSortsAndCounts_WithBestGeometry()
    {
        using var image = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(image, new Point(40, 40), 10, Scalar.All(255), -1);   // area ≈ 314
        Cv2.Circle(image, new Point(120, 60), 15, Scalar.All(255), -1);  // ≈ 707
        Cv2.Circle(image, new Point(80, 150), 20, Scalar.All(255), -1);  // ≈ 1257

        var all = await Run(new MultiBlobNode(), Image(image), """{"minArea":100}""");
        var detections = all.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal(3L, (long)all.Outputs["count"].Value!);
        Assert.Equal(3, detections.Count);
        Assert.Equal(0, detections[0].Index);                    // 排序后 0 基序号
        Assert.InRange(detections[0].Area, 1150, 1400);          // 面积降序 ⇒ 最大优先
        Assert.InRange((double)all.Outputs["area"].Value!, 1150, 1400);
        Assert.InRange((double)all.Outputs["radius"].Value!, 18, 22);

        var top2 = await Run(new MultiBlobNode(), Image(image), """{"minArea":100,"maxCount":2}""");
        Assert.Equal(2L, (long)top2.Outputs["count"].Value!);

        var bigOnly = await Run(new MultiBlobNode(), Image(image), """{"minArea":800}""");
        Assert.Equal(1L, (long)bigOnly.Outputs["count"].Value!);

        var largest = await Run(new MultiBlobNode(), Image(image),
            """{"mode":"largest","minArea":100,"maxArea":100,"minCircularity":1,"maxCount":20,"sortBy":"x","descending":false}""");
        var largestDetections = largest.Outputs["detections"].Require<IReadOnlyList<VisionDetection>>("detections");
        Assert.Equal(1L, (long)largest.Outputs["count"].Value!);
        Assert.Single(largestDetections);
        Assert.InRange(largestDetections[0].Area, 1150, 1400);
        Assert.IsType<VisionImage>(largest.Outputs["image"].Value);

        var leftFirst = await Run(new MultiBlobNode(), Image(image), """{"minArea":100,"sortBy":"x","descending":false}""");
        Assert.Equal(40d, (double)leftFirst.Outputs["x"].Value!, 1);   // 最左

        // 零目标是正常结果：count=0 且不产出首选几何（数量检查依赖这一语义）。
        using var empty = new Mat(50, 50, MatType.CV_8UC1, Scalar.All(0));
        var none = await Run(new MultiBlobNode(), Image(empty), """{"minArea":10}""");
        Assert.Equal(0L, (long)none.Outputs["count"].Value!);
        Assert.False(none.Outputs.ContainsKey("x"));
    }

    [Fact]
    public async Task Roi_MasksPreprocessingOutput_AndLimitsBlobCount()
    {
        using var uniform = new Mat(80, 80, MatType.CV_8UC1, Scalar.All(180));
        var blurred = await Run(new GaussianBlurNode(), Image(uniform),
            """{"kernelSize":3,"roi":{"type":"Rectangle","x":10,"y":10,"width":30,"height":30}}""");
        using var masked = TakeImage(blurred);
        Assert.Equal(0, masked.At<byte>(5, 5));
        Assert.Equal(180, masked.At<byte>(20, 20));

        using var blobs = new Mat(100, 160, MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(blobs, new Point(40, 50), 12, Scalar.All(255), -1);
        Cv2.Circle(blobs, new Point(120, 50), 12, Scalar.All(255), -1);
        var detected = await Run(new MultiBlobNode(), Image(blobs),
            """{"minArea":100,"roi":{"type":"Rectangle","x":20,"y":25,"width":40,"height":50}}""");
        Assert.Equal(1L, (long)detected.Outputs["count"].Value!);
    }

    // ---------- 模板匹配 / 旋转定位 ----------

    [Fact]
    public async Task TemplateMatch_LocatesUnrotatedTemplate_WithScoreAndGeometry()
    {
        using var template = BuildTemplate(out _);
        using var scene = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        using var templateArea = new Mat(scene, new Rect(60, 90, 32, 32));
        template.CopyTo(templateArea);

        var result = await Run(new TemplateMatchNode(), Image(scene), """{}""", template: template);
        Assert.True((bool)result.Outputs["found"].Value!);
        Assert.InRange((double)result.Outputs["score"].Value!, 0.95, 1.0);
        Assert.Equal(60d, (double)result.Outputs["x"].Value!, 1);
        Assert.Equal(90d, (double)result.Outputs["y"].Value!, 1);
        Assert.Equal(76d, ((VisionPoint2D)result.Outputs["center"].Value!).X, 1);
        Assert.Equal(32d, ((VisionRectangle2D)result.Outputs["rectangle"].Value!).Width, 1);
    }

    [Fact]
    public async Task TemplateMatch_DoesNotDisposeUpstreamImageInputs()
    {
        using var template = BuildTemplate(out _);
        using var scene = new Mat(100, 100, MatType.CV_8UC1, Scalar.All(0));
        using (var area = new Mat(scene, new Rect(30, 30, template.Width, template.Height)))
            template.CopyTo(area);
        var sceneInput = VisionImage.Own(scene.Clone());
        var templateInput = VisionImage.Own(template.Clone());
        var inputs = new Dictionary<string, VisionValue>
        {
            ["image"] = VisionValue.Image(sceneInput),
            ["template"] = VisionValue.Image(templateInput)
        };
        try
        {
            await new TemplateMatchNode().ExecuteAsync(
                new NodeExecutionContext(inputs), NodeOf("match.template", "{}"), CancellationToken.None);
            Assert.False(sceneInput.Mat.Empty());
            Assert.False(templateInput.Mat.Empty());
        }
        finally
        {
            sceneInput.Dispose();
            templateInput.Dispose();
        }
    }

    [Fact]
    public async Task TemplateMatch_RotationSearch_FindsKnownAngle()
    {
        using var template = BuildTemplate(out _);
        using var rotated = RotateExpanded(template, 15); // 场景中的工件 = 模板旋转 +15°
        using var scene = new Mat(200, 200, MatType.CV_8UC1, Scalar.All(0));
        var x = 100;
        var y = 60;
        using (var area = new Mat(scene, new Rect(x, y, rotated.Width, rotated.Height)))
            rotated.CopyTo(area);

        var result = await Run(new TemplateMatchNode(), Image(scene),
            """{"angleRangeDeg":20,"angleStepDeg":5,"minScore":0.5}""", template: template);

        Assert.True((bool)result.Outputs["found"].Value!);
        var angle = (double)result.Outputs["angle"].Value!;
        Assert.InRange(angle, 10, 20);                       // ≈ +15°（步长 5 的容差内）
        Assert.InRange((double)result.Outputs["score"].Value!, 0.5, 1.0);
        var rectangle = (VisionRectangle2D)result.Outputs["rectangle"].Value!;
        Assert.Equal(angle, rectangle.AngleDeg, 3);
    }

    [Fact]
    public async Task TemplateMatch_NoMatch_ReturnsFoundFalse_AndMissingPathFailsClearly()
    {
        using var template = BuildTemplate(out _);
        using var scene = new Mat(120, 120, MatType.CV_8UC1, Scalar.All(30));

        var result = await Run(new TemplateMatchNode(), Image(scene),
            """{"minScore":0.9}""", template: template);
        Assert.False((bool)result.Outputs["found"].Value!);
        Assert.False(result.Outputs.ContainsKey("x"));       // 未命中不产出几何
        var score = (double)result.Outputs["score"].Value!;
        Assert.True(score < 0.9, $"flat scene must not fake a match (score={score})");

        // 无模板端口 + 空路径 ⇒ 明确报错
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Run(new TemplateMatchNode(), Image(scene), """{}"""));
        Assert.Contains("template", error.Message, StringComparison.OrdinalIgnoreCase);

        // 有路径但文件不存在 ⇒ 明确指出路径
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Run(new TemplateMatchNode(), Image(scene),
                """{"templatePath":"Z:/definitely-missing/template.png"}"""));
        Assert.Contains("could not be loaded", missing.Message);
    }

    [Fact]
    public async Task TemplateMatch_LoadsTemplateFromFile()
    {
        using var template = BuildTemplate(out _);
        var file = Path.Combine(Path.GetTempPath(), "vs-batch1-template-" + Guid.NewGuid().ToString("N") + ".png");
        Cv2.ImWrite(file, template);
        try
        {
            using var scene = new Mat(150, 150, MatType.CV_8UC1, Scalar.All(0));
            using (var area = new Mat(scene, new Rect(40, 40, 32, 32)))
                template.CopyTo(area);

            var result = await Run(new TemplateMatchNode(), Image(scene),
                $$"""{"templatePath":{{JsonSerializer.Serialize(file)}},"minScore":0.8}""");
            Assert.True((bool)result.Outputs["found"].Value!);
            Assert.Equal("file", result.Summary["templateSource"]);
            Assert.Equal(40d, (double)result.Outputs["x"].Value!, 1);
        }
        finally
        {
            try { File.Delete(file); } catch { /* 尽力清理 */ }
        }
    }

    // ---------- 数值 / 布尔判定 ----------

    [Fact]
    public async Task ValueNumber_And_Arithmetic_CoverFourOperations_AndDivisionGuard()
    {
        var constant = await Run(new ValueNumberNode(), new Dictionary<string, VisionValue>(), """{"value":42.5}""");
        Assert.Equal(42.5, (double)constant.Outputs["value"].Value!, 3);
        Assert.Equal(43L, (long)constant.Outputs["integer"].Value!);   // 四舍五入

        var inputs = new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Double(10),
            ["b"] = VisionValue.Integer(4)                              // Integer → Double 端口兼容
        };
        Assert.Equal(14d, (double)(await Run(new ValueArithmeticNode(), inputs, """{"op":"add"}""")).Outputs["result"].Value!, 3);
        Assert.Equal(6d, (double)(await Run(new ValueArithmeticNode(), inputs, """{"op":"subtract"}""")).Outputs["result"].Value!, 3);
        Assert.Equal(40d, (double)(await Run(new ValueArithmeticNode(), inputs, """{"op":"multiply"}""")).Outputs["result"].Value!, 3);
        Assert.Equal(2.5d, (double)(await Run(new ValueArithmeticNode(), inputs, """{"op":"divide"}""")).Outputs["result"].Value!, 3);

        var zero = new Dictionary<string, VisionValue> { ["a"] = VisionValue.Double(1), ["b"] = VisionValue.Double(0) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Run(new ValueArithmeticNode(), zero, """{"op":"divide"}"""));
        Assert.Contains("divide by zero", error.Message);

        var nonFinite = new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Double(double.PositiveInfinity),
            ["b"] = VisionValue.Double(1)
        };
        var nonFiniteError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Run(new ValueArithmeticNode(), nonFinite, """{"op":"add"}"""));
        Assert.Contains("finite", nonFiniteError.Message);

        var overflow = new Dictionary<string, VisionValue>
        {
            ["a"] = VisionValue.Double(1e308),
            ["b"] = VisionValue.Double(1e-308)
        };
        var overflowError = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Run(new ValueArithmeticNode(), overflow, """{"op":"divide"}"""));
        Assert.Contains("not finite", overflowError.Message);
    }

    [Fact]
    public async Task ValueRange_ChecksInclusiveWindow_AndSwapsReversedBounds()
    {
        Assert.True((bool)(await Run(new ValueRangeNode(), Number(50), """{"min":0,"max":100}""")).Outputs["pass"].Value!);
        Assert.False((bool)(await Run(new ValueRangeNode(), Number(101), """{"min":0,"max":100}""")).Outputs["pass"].Value!);
        Assert.True((bool)(await Run(new ValueRangeNode(), Number(100), """{"min":0,"max":100}""")).Outputs["pass"].Value!);      // 闭区间
        Assert.True((bool)(await Run(new ValueRangeNode(), Number(50), """{"min":100,"max":0}""")).Outputs["pass"].Value!);       // 反向配置自动交换
        Assert.True((bool)(await Run(new ValueRangeNode(), Number(150), """{"min":0,"max":100,"invert":true}""")).Outputs["pass"].Value!);
    }

    [Fact]
    public async Task LogicBoolean_CombinesResults_AndNotIgnoresSecondInput()
    {
        static Dictionary<string, VisionValue> Pair(bool a, bool b) => new()
        {
            ["a"] = VisionValue.Boolean(a),
            ["b"] = VisionValue.Boolean(b)
        };
        Assert.True((bool)(await Run(new LogicBooleanNode(), Pair(true, true), """{"op":"and"}""")).Outputs["result"].Value!);
        Assert.False((bool)(await Run(new LogicBooleanNode(), Pair(true, false), """{"op":"and"}""")).Outputs["result"].Value!);
        Assert.True((bool)(await Run(new LogicBooleanNode(), Pair(false, true), """{"op":"or"}""")).Outputs["result"].Value!);
        Assert.True((bool)(await Run(new LogicBooleanNode(), Pair(true, false), """{"op":"xor"}""")).Outputs["result"].Value!);
        Assert.False((bool)(await Run(new LogicBooleanNode(), Pair(true, true), """{"op":"not"}""")).Outputs["result"].Value!);
        // NOT 只读 a：即使 b 未连接也必须成功（端口 Required=false）。
        var onlyA = new Dictionary<string, VisionValue> { ["a"] = VisionValue.Boolean(false) };
        Assert.True((bool)(await Run(new LogicBooleanNode(), onlyA, """{"op":"not"}""")).Outputs["result"].Value!);
    }

    // ---------- 目录/参数契约 ----------

    [Theory]
    [InlineData("image.blur.median", """{"kernelSize":40}""", "above the maximum 31")]
    [InlineData("image.filter", """{"method":"gaussian","sigmaX":21}""", "above the maximum 20")]
    [InlineData("image.morphology", """{"operation":"skeletonize"}""", "not an allowed option")]
    [InlineData("measure.blobs", """{"sortBy":"diagonal"}""", "not an allowed option")]
    [InlineData("logic.boolean", """{"op":"nand"}""", "not an allowed option")]
    [InlineData("image.threshold.adaptive", """{"blockSize":1}""", "below the minimum 3")]
    [InlineData("match.template", """{"minScore":2}""", "above the maximum 1")]
    public void NewNodes_RejectInvalidParametersAtCompileTime(string nodeType, string parametersJson, string reasonFragment)
    {
        var registry = new VisionNodeRegistry();
        foreach (var catalog in BuiltInNodeCatalog.Items)
            registry.Register(catalog, new NoopExecutor(catalog.Type), "test-catalog");
        var compiler = new VisionWorkflowCompiler(registry);
        var nodes = new List<NodeDefinition>
        {
            new("src-image", "image.synthetic", null, null, null),
            new("src-number", "value.number", null, null, null),
            new("src-range", "value.range", null, null, null),
            new("n1", nodeType, null, null, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson))
        };
        var edges = new List<EdgeDefinition>();
        if (BuiltInNodeCatalog.Items.Single(x => x.Type == nodeType).Inputs.Any(x => x.Name == "image" && x.Required))
            edges.Add(new EdgeDefinition("e-image", "src-image", "image", "n1", "image"));
        edges.Add(new EdgeDefinition("e-number", "src-number", "value", "src-range", "value"));
        if (nodeType == "logic.boolean")
            edges.Add(new EdgeDefinition("e-bool", "src-range", "pass", "n1", "a"));
        var workflow = new WorkflowDefinition("wf-batch1-params", "params", nodes, edges);

        var error = Assert.Throws<InvalidOperationException>(() => compiler.Compile(workflow));

        Assert.Contains(reasonFragment, error.Message);
    }

    [Fact]
    public void NewNodeTypes_ArePresentInTheBuiltInCatalog()
    {
        string[] expected =
        [
            "image.grayscale", "image.channel", "image.blur.gaussian", "image.blur.median", "image.morphology",
            "image.filter",
            "image.threshold.adaptive", "image.threshold.otsu", "measure.blobs", "match.template",
            "value.number", "value.arithmetic", "value.range", "logic.boolean"
        ];
        foreach (var type in expected)
            Assert.NotNull(BuiltInNodeCatalog.Items.FirstOrDefault(x => x.Type == type));
        Assert.Equal(14, expected.Distinct().Count());
    }

    // ---------- 测试辅助 ----------

    private static Dictionary<string, JsonElement>? Params(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

    private static NodeDefinition NodeOf(string type, string json) => new("n1", type, null, null, Params(json));

    private static VisionValue Image(Mat mat) => VisionValue.Image(VisionImage.Own(mat.Clone()));

    private static Dictionary<string, VisionValue> Number(double value) =>
        new() { ["value"] = VisionValue.Double(value) };

    /// <summary>单图像输入的便捷重载（键名 "image"）。</summary>
    private static Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor,
        VisionValue singleImageInput,
        string parametersJson,
        Mat? template = null)
        => Run(executor, new Dictionary<string, VisionValue> { ["image"] = singleImageInput }, parametersJson, template);

    private static async Task<NodeExecutorResult> Run(
        IVisionNodeExecutor executor,
        Dictionary<string, VisionValue> inputs,
        string parametersJson,
        Mat? template = null)
    {
        if (template is not null)
            inputs["template"] = Image(template);
        var result = await executor.ExecuteAsync(
            new NodeExecutionContext(inputs), NodeOf(executor.Type, parametersJson), CancellationToken.None);
        foreach (var value in inputs.Values)
            (value.Value as VisionImage)?.Dispose();
        return result;
    }

    private static Mat TakeImage(NodeExecutorResult result)
    {
        var image = result.Outputs["image"].Require<IVisionImage>("image");
        return ((VisionImage)image).Mat;
    }

    /// <summary>非对称模板（横条 + 偏心圆点）——旋转搜索可判定唯一角度。</summary>
    private static Mat BuildTemplate(out int size)
    {
        size = 32;
        var template = new Mat(size, size, MatType.CV_8UC1, Scalar.All(20));
        Cv2.Rectangle(template, new Rect(6, 8, 20, 8), Scalar.All(230), -1);
        Cv2.Circle(template, new Point(11, 24), 3, Scalar.All(200), -1);
        return template;
    }

    /// <summary>与 TemplateMatchNode 相同的旋转扩展（正角=逆时针，边界复制）。</summary>
    private static Mat RotateExpanded(Mat source, double angleDeg)
    {
        var center = new Point2f(source.Width / 2f, source.Height / 2f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
        var cos = Math.Abs(matrix.At<double>(0, 0));
        var sin = Math.Abs(matrix.At<double>(0, 1));
        var width = (int)Math.Round(source.Width * cos + source.Height * sin);
        var height = (int)Math.Round(source.Width * sin + source.Height * cos);
        matrix.Set(0, 2, matrix.At<double>(0, 2) + (width / 2d - center.X));
        matrix.Set(1, 2, matrix.At<double>(1, 2) + (height / 2d - center.Y));
        var rotated = new Mat();
        Cv2.WarpAffine(source, rotated, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Replicate);
        return rotated;
    }

    private sealed class NoopExecutor(string type) : IVisionNodeExecutor
    {
        public string Type { get; } = type;

        public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
            => ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>(), new Dictionary<string, object?>()));
    }
}
