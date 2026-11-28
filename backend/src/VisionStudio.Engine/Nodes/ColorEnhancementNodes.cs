using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

internal sealed class ColorChannels(JlImage first, JlImage second, JlImage third, string names) : IDisposable
{
    public JlImage First { get; } = first;
    public JlImage Second { get; } = second;
    public JlImage Third { get; } = third;
    public string Names { get; } = names;
    public void Dispose() { First.Dispose(); Second.Dispose(); Third.Dispose(); }

    public static void Validate(VisionImage source)
    {
        if (source.Mat.Empty() || source.Mat.Depth() != MatType.CV_8U || source.Mat.Channels() is not (1 or 3 or 4))
            throw new InvalidOperationException("Color and enhancement nodes require a nonempty 8-bit grayscale or BGR(A) image.");
    }

    public static ColorChannels Create(VisionImage source, string space, string channelOrder = "bgr")
    {
        Validate(source);
        space = space.Trim().ToLowerInvariant();
        if (space is not ("rgb" or "hsv" or "lab")) throw new InvalidOperationException("Color space must be rgb, hsv or lab.");
        var order = channelOrder.Trim().ToLowerInvariant();
        if (order is not ("bgr" or "rgb")) throw new InvalidOperationException("Channel order must be bgr or rgb.");
        using var native = JLVisionImages.FromMat(source.Mat);
        using var count = native.CountChannels();
        using var native1 = count.I == 1 ? native.CopyImage() : native.AccessChannel(1);
        using var native2 = count.I == 1 ? native.CopyImage() : native.AccessChannel(2);
        using var native3 = count.I == 1 ? native.CopyImage() : native.AccessChannel(3);
        var redByte = order == "rgb" ? native3 : native1;
        var greenByte = native2;
        var blueByte = order == "rgb" ? native1 : native3;
        using var r = Normalize(redByte); using var g = Normalize(greenByte); using var b = Normalize(blueByte);
        if (space == "rgb") return new(r.CopyImage(), g.CopyImage(), b.CopyImage(), "R(0..1),G(0..1),B(0..1)");
        if (space == "lab")
        {
            // Native CIELAB conversion expects linear RGB. Camera byte buffers are
            // interpreted as sRGB, so decode its piecewise transfer function first.
            using var linearR = DecodeSrgb(r, source.Width, source.Height);
            using var linearG = DecodeSrgb(g, source.Width, source.Height);
            using var linearB = DecodeSrgb(b, source.Width, source.Height);
            var l = linearR.TransFromRgb(linearG, linearB, out var a, out var labB, "cielab");
            return new(l, a, labB, "L*(0..100,D65),a*,b*");
        }
        using var first = r.TransFromRgb(g, b, out var second, out var third, "hsv");
        try
        {
            return new(first.ScaleImage(180 / Math.PI, 0d), second, third, "H(0..360deg),S(0..1),V(0..1)");
        }
        catch { second.Dispose(); third.Dispose(); throw; }
    }

    private static JlImage Normalize(JlImage input)
    {
        using var real = input.ConvertImageType("real");
        return real.ScaleImage(1d / 255, 0d);
    }

    private static JlImage DecodeSrgb(JlImage channel, int width, int height)
    {
        using var low = channel.ScaleImage(1d / 12.92, 0d);
        using var shifted = channel.ScaleImage(1d / 1.055, .055 / 1.055); using var high = shifted.PowImage(2.4);
        using var lowRegion = channel.Threshold(0d, .04045); using var byteMask = lowRegion.RegionToBin(1, 0, width, height);
        using var mask = byteMask.ConvertImageType("real"); using var inverse = mask.ScaleImage(-1d, 1d);
        using var lowPart = low.MultImage(mask, 1d, 0d); using var highPart = high.MultImage(inverse, 1d, 0d);
        return lowPart.AddImage(highPart, 1d, 0d);
    }

    public static JlImage Gray(VisionImage source)
    {
        Validate(source);
        using var native = JLVisionImages.FromMat(source.Mat); using var channels = native.CountChannels();
        return channels.I == 1 ? native.CopyImage() : native.Rgb1ToGray();
    }

    public static int Window(NodeDefinition node, int fallback)
    {
        var size = node.GetInt("windowSize", fallback);
        if (size is < 3 or > 255 || size % 2 == 0) throw new InvalidOperationException("Window size must be an odd integer in 3..255.");
        return size;
    }
}

public sealed class ImageColorConvertNode : IVisionNodeExecutor
{
    public string Type => "image.colorConvert";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var space = node.GetString("space", "hsv").Trim().ToLowerInvariant();
        var channelOrder = node.GetString("channelOrder", "bgr").Trim().ToLowerInvariant();
        var inputColorSpace = node.GetString("inputColorSpace", "srgb").Trim().ToLowerInvariant();
        if (inputColorSpace is not ("srgb" or "linear"))
            throw new InvalidOperationException("image.colorConvert inputColorSpace must be srgb or linear.");
        if (inputColorSpace == "linear" && space is "hsv" or "lab")
            throw new InvalidOperationException("image.colorConvert HSV/Lab conversion requires sRGB input; select rgb for normalized linear values or encode to sRGB first.");
        var source = context.RequireImage("image");
        using var channels = ColorChannels.Create(source, space, channelOrder);
        var outputs = new Dictionary<string, VisionValue>();
        try
        {
            foreach (var (key, value) in new[] { ("channel1", channels.First), ("channel2", channels.Second), ("channel3", channels.Third) })
            {
                cancellationToken.ThrowIfCancellationRequested();
                outputs[key] = VisionValue.Image(VisionImage.Own(JLVisionImages.ToMat(value)));
            }
            outputs["channelNames"] = VisionValue.String(channels.Names);
            var diag = CoordinateValidation.Diagnostics(Type, ("space", space), ("channelOrder", channelOrder), ("channelNames", channels.Names),
                ("inputColorSpace", inputColorSpace), ("assumedSrgb", inputColorSpace == "srgb"),
                ("inputDepth", source.Mat.Depth().ToString()), ("inputChannels", source.Mat.Channels()),
                ("alphaIgnored", source.Mat.Channels() == 4), ("channelOrderIgnoredForGray", source.Mat.Channels() == 1),
                ("transferFunction", space == "lab" && inputColorSpace == "srgb" ? "sRGB EOTF then D65 Lab" : "none"));
            outputs["diagnostics"] = VisionValue.String(diag);
            return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?> { ["space"] = space, ["channelOrder"] = channelOrder, ["channelNames"] = channels.Names, ["algorithmBackend"] = "jlvision", ["diagnostics"] = diag }));
        }
        catch { foreach (var output in outputs.Values) (output.Value as IDisposable)?.Dispose(); throw; }
    }
}

internal static class RegionFromColorRangeCore
{
    public static NodeExecutorResult Execute(NodeExecutionContext context, NodeDefinition node, string nodeType, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(nodeType + " requires a node definition.");
        var emptyPolicy = CoordinateValidation.RequireNonEmpty(node.GetString("emptyPolicy", "empty"), "emptyPolicy").ToLowerInvariant();
        if (emptyPolicy is not ("empty" or "fail")) throw new InvalidOperationException("region.fromColorRange emptyPolicy must be empty or fail");
        var (source, scoped) = RegionNodeHelpers.Source(context);
        var space = node.GetString("space", "hsv").Trim().ToLowerInvariant();
        var min = new[] { node.GetDouble("min1", 0), node.GetDouble("min2", 0), node.GetDouble("min3", .5) };
        var max = new[] { node.GetDouble("max1", 360), node.GetDouble("max2", 1), node.GetDouble("max3", 1) };
        var lower = space == "lab" ? new[] { 0d, -200, -200 } : new[] { 0d, 0, 0 };
        var upper = space switch { "lab" => new[] { 100d, 200, 200 }, "rgb" => new[] { 1d, 1, 1 }, _ => new[] { 360d, 1, 1 } };
        for (var i = 0; i < 3; i++)
            if (!double.IsFinite(min[i]) || !double.IsFinite(max[i]) || min[i] < lower[i] || max[i] < lower[i] || min[i] > upper[i] || max[i] > upper[i] || min[i] > max[i] && (space != "hsv" || i != 0))
                throw new InvalidOperationException("Color bounds must fit the selected color space and be ordered (HSV hue may wrap).");
        using var channels = ColorChannels.Create(source, space);
        using var domain = DefectNodeHelpers.Domain(channels.First, source.Width, source.Height, scoped, context);
        using var first = channels.First.ReduceDomain(domain);
        using var range1 = min[0] <= max[0] ? first.Threshold(min[0], max[0]) : WrappedHue(first, min[0], max[0]);
        using var range2 = channels.Second.Threshold(min[1], max[1]); using var range3 = channels.Third.Threshold(min[2], max[2]);
        using var both = range1.Intersection(range2); using var selected = both.Intersection(range3);
        var result = RegionNodeHelpers.Result(JLVisionRegions.FromNative(selected, source.Width, source.Height, cancellationToken), cancellationToken);
        var found = result.Outputs.TryGetValue("found", out var foundValue) && foundValue.Value is true;
        if (!found && emptyPolicy == "fail")
        {
            foreach (var output in result.Outputs.Values) (output.Value as IDisposable)?.Dispose();
            throw new InvalidOperationException("region.fromColorRange produced no pixels and emptyPolicy=fail.");
        }
        return (result with { Summary = new Dictionary<string, object?>(result.Summary) { ["space"] = space, ["emptyPolicy"] = emptyPolicy, ["algorithmBackend"] = "jlvision" } });
    }
    private static JlRegion WrappedHue(JlImage image, double min, double max)
    {
        using var high = image.Threshold(min, 360d); using var low = image.Threshold(0d, max);
        return high.Union2(low);
    }
}

public sealed class ColorDifferenceNode : IVisionNodeExecutor
{
    public string Type => "measure.colorDifference";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (source, scoped) = RegionNodeHelpers.Source(context);
        using var current = ColorChannels.Create(source, "lab");
        var hasReference = context.TryGetValue("reference", out var referenceValue);
        VisionImage? constant = null; ColorChannels? reference = null;
        try
        {
            if (hasReference)
            {
                var image = referenceValue.Require<VisionImage>("reference"); DefectNodeHelpers.SameSize(source, image);
                var registered = node.GetBool("referenceRegistered", false);
                var residual = node.GetDouble("registrationResidualPx", 0);
                var maxResidual = node.GetDouble("maxRegistrationResidualPx", .5);
                if (!double.IsFinite(residual) || residual < 0 || !double.IsFinite(maxResidual) || maxResidual < 0)
                    throw new InvalidOperationException("Color difference registration residual must be finite and non-negative.");
                if (!registered || residual > maxResidual)
                {
                    var reason = !registered ? "reference-not-registered" : "registration-residual-exceeds-tolerance";
                    var failureDiag = CoordinateValidation.Diagnostics(Type, ("found", false), ("reason", reason),
                        ("referenceFrame", image.CoordinateFrame), ("currentFrame", source.CoordinateFrame),
                        ("registrationResidualPx", residual), ("maxRegistrationResidualPx", maxResidual),
                        ("alignmentValid", false), ("metric", "CIE76"));
                    return ValueTask.FromResult(new NodeExecutorResult(new Dictionary<string, VisionValue>
                    {
                        ["found"] = VisionValue.Boolean(false), ["alignmentValid"] = VisionValue.Boolean(false),
                        ["image"] = VisionValue.Image(source.CloneOwned()), ["diagnostics"] = VisionValue.String(failureDiag)
                    }, new Dictionary<string, object?> { ["found"] = false, ["reason"] = reason, ["alignmentValid"] = false,
                        ["referenceFrame"] = image.CoordinateFrame, ["currentFrame"] = source.CoordinateFrame,
                        ["registrationResidualPx"] = residual, ["diagnostics"] = failureDiag }));
                }
                reference = ColorChannels.Create(image, "lab");
            }
            else
            {
                var red = node.GetDouble("referenceRed", 255); var green = node.GetDouble("referenceGreen", 255); var blue = node.GetDouble("referenceBlue", 255);
                if (new[] { red, green, blue }.Any(v => !double.IsFinite(v) || v is < 0 or > 255 || v != Math.Truncate(v)))
                    throw new InvalidOperationException("Reference sRGB channels must be integers in 0..255.");
                constant = VisionImage.Own(new Mat(1, 1, MatType.CV_8UC3, new Scalar(blue, green, red)));
                reference = ColorChannels.Create(constant, "lab");
            }
            using var d1 = Difference(current.First, reference.First, hasReference); using var d2 = Difference(current.Second, reference.Second, hasReference); using var d3 = Difference(current.Third, reference.Third, hasReference);
            using var s1 = d1.MultImage(d1, 1d, 0d); using var s2 = d2.MultImage(d2, 1d, 0d); using var s3 = d3.MultImage(d3, 1d, 0d);
            using var sum12 = s1.AddImage(s2, 1d, 0d); using var sum = sum12.AddImage(s3, 1d, 0d); using var distance = sum.SqrtImage();
            using var domain = DefectNodeHelpers.Domain(distance, source.Width, source.Height, scoped, context);
            var region = JLVisionRegions.FromNative(domain, source.Width, source.Height, cancellationToken); var area = JLVisionRegions.PixelArea(region); var found = area > 0;
            var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(found), ["area"] = VisionValue.Double(area),
                ["alignmentValid"] = VisionValue.Boolean(true), ["region"] = VisionValue.Region(region) };
            var summary = new Dictionary<string, object?> { ["found"] = found, ["area"] = area, ["metric"] = "CIE76", ["referenceSource"] = hasReference ? "image" : "srgb-constant", ["algorithmBackend"] = "jlvision" };
            if (found)
            {
                var stats = DefectNodeHelpers.Statistics(distance, domain);
                outputs["deltaE"] = VisionValue.Double(stats.Mean); outputs["maxDeltaE"] = VisionValue.Double(stats.Max); outputs["stdDev"] = VisionValue.Double(stats.Deviation);
                summary["deltaE"] = stats.Mean; summary["maxDeltaE"] = stats.Max;
            }
            var colorDiag = CoordinateValidation.Diagnostics(Type, ("found", found), ("area", area), ("metric", "CIE76"),
                ("referenceSource", hasReference ? "image" : "srgb-constant"), ("alignmentValid", true),
                ("referenceFrame", hasReference ? referenceValue!.Require<VisionImage>("reference").CoordinateFrame : "constant"),
                ("currentFrame", source.CoordinateFrame),
                ("registrationResidualPx", hasReference ? node.GetDouble("registrationResidualPx", 0) : 0), ("width", source.Width), ("height", source.Height),
                ("deltaE", summary.GetValueOrDefault("deltaE")), ("maxDeltaE", summary.GetValueOrDefault("maxDeltaE")));
            outputs["diagnostics"] = VisionValue.String(colorDiag); summary["diagnostics"] = colorDiag;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var image = VisionImage.Own(JLVisionImages.ToMat(distance)); outputs["image"] = VisionValue.Image(image);
                outputs["domainImage"] = VisionValue.DomainImage(new VisionDomainImage(image.CloneOwned(), region));
                return ValueTask.FromResult(new NodeExecutorResult(outputs, summary));
            }
            catch { foreach (var value in outputs.Values) (value.Value as IDisposable)?.Dispose(); throw; }
        }
        finally { reference?.Dispose(); constant?.Dispose(); }
    }
    private static JlImage Difference(JlImage current, JlImage reference, bool image)
    {
        if (image) return current.SubImage(reference, 1d, 0d);
        using var pixel = reference.GetGrayval(0, 0);
        return current.ScaleImage(1d, -pixel.D);
    }
}

public sealed class ContrastNormalizeNode : IVisionNodeExecutor
{
    public string Type => "image.contrastNormalize";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
var tail = node.GetDouble("tailPercent", 0); var low = node.GetDouble("outputMin", 0); var high = node.GetDouble("outputMax", 255);
        if (!double.IsFinite(tail) || tail is < 0 or >= 50 || !double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high > 255 || low > high || low != Math.Truncate(low) || high != Math.Truncate(high))
            throw new InvalidOperationException("Tail percent must satisfy 0 <= tailPercent < 50 and output bounds must be ordered integers within 0..255.");
        var emptyPolicy = CoordinateValidation.RequireNonEmpty(node.GetString("emptyPolicy", "passthrough"), "emptyPolicy").ToLowerInvariant();
        if (emptyPolicy is not ("passthrough" or "fail"))
            throw new InvalidOperationException("image.contrastNormalize emptyPolicy must be passthrough or fail.");
        using var gray = ColorChannels.Gray(context.RequireImage("image")); using var domain = gray.GetDomain();
        gray.MinMaxGray(domain, tail, out double min, out double max, out double _);
        var rangeCollapsed = max <= min;
        if (rangeCollapsed && emptyPolicy == "fail")
            throw new InvalidOperationException("image.contrastNormalize found a uniform image (zero contrast) and emptyPolicy=fail.");
        var scaleUsed = rangeCollapsed ? 0d : (high - low) / (max - min);
        var offsetUsed = rangeCollapsed ? low : low - min * scaleUsed;
        using var real = gray.ConvertImageType("real");
        using var stretched = rangeCollapsed ? real.CopyImage() : real.ScaleImage(scaleUsed, offsetUsed);
        using var lo = real.ScaleImage(0d, low); using var hi = real.ScaleImage(0d, high);
        using var lower = rangeCollapsed ? (low == high ? lo.CopyImage() : stretched.CopyImage()) : stretched.MaxImage(lo);
        using var clamped = rangeCollapsed ? (low == high ? hi.CopyImage() : lower.CopyImage()) : lower.MinImage(hi);
        using var output = clamped.ConvertImageType("byte"); cancellationToken.ThrowIfCancellationRequested();
        using var outputMat = JLVisionImages.ToMat(output);
        var saturation = PreprocessP1Diagnostics.ByteSaturation(outputMat, 255);
        var diag = CoordinateValidation.Diagnostics(Type, ("inputMin", min), ("inputMax", max), ("rangeCollapsed", rangeCollapsed),
            ("tailPercent", tail), ("outputMin", low), ("outputMax", high), ("emptyPolicy", emptyPolicy),
            ("scaleUsed", scaleUsed), ("offsetUsed", offsetUsed),
            ("zeroContrastStrategy", rangeCollapsed ? (low == high ? "constant-outputMin" : "passthrough") : "linear-stretch"),
            ("saturatedLowPixels", saturation.Low), ("saturatedHighPixels", saturation.High), ("saturatedRatio", saturation.Ratio));
        var result = DefectNodeHelpers.ImageResult(output, new() { ["inputMin"] = min, ["inputMax"] = max, ["rangeCollapsed"] = max <= min, ["tailPercent"] = tail, ["emptyPolicy"] = emptyPolicy, ["diagnostics"] = diag });
        var outputs = new Dictionary<string, VisionValue>(result.Outputs)
        {
            ["inputMin"] = VisionValue.Double(min),
            ["inputMax"] = VisionValue.Double(max),
            ["scaleUsed"] = VisionValue.Double(scaleUsed),
            ["offsetUsed"] = VisionValue.Double(offsetUsed),
            ["saturatedPixelCount"] = VisionValue.Integer(saturation.Low + saturation.High),
            ["diagnostics"] = VisionValue.String(diag)
        };
        var owned = outputs["image"].Require<VisionImage>("image");
        var meanIntensity = JLVisionOperators.Mean(owned.Mat);
        outputs["meanIntensity"] = VisionValue.Double(meanIntensity);
        var summary = new Dictionary<string, object?>(result.Summary) { ["meanIntensity"] = meanIntensity };
        return ValueTask.FromResult(result with { Outputs = outputs, Summary = summary });
    }
}

public sealed class IlluminationNormalizeNode : IVisionNodeExecutor
{
    public string Type => "image.illuminationNormalize";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var window = ColorChannels.Window(node, 51); var target = node.GetDouble("targetMean", 128); var minimum = node.GetDouble("minBackground", 1);
        if (!double.IsFinite(target) || target is < 0 or > 255 || !double.IsFinite(minimum) || minimum is <= 0 or > 255)
            throw new InvalidOperationException("Target gray must be 0..255 and minimum background must be positive and <=255.");
        var source = context.RequireImage("image");
        if (source.Mat.Empty()) throw new InvalidOperationException("image.illuminationNormalize requires a non-empty image.");
        if (source.Mat.Width < 1 || source.Mat.Height < 1)
            throw new InvalidOperationException("image.illuminationNormalize image dimensions must be positive.");
        var windowPolicy = node.GetString("windowPolicy", "clamp").Trim().ToLowerInvariant();
        if (windowPolicy is not ("clamp" or "error"))
            throw new InvalidOperationException("image.illuminationNormalize windowPolicy must be clamp or error.");
        var borderName = node.GetString("borderMode", "reflect").Trim().ToLowerInvariant();
        var border = borderName switch
        {
            "reflect" => BorderTypes.Reflect,
            "replicate" => BorderTypes.Replicate,
            "constant" => BorderTypes.Constant,
            _ => throw new InvalidOperationException("image.illuminationNormalize borderMode must be reflect, replicate or constant.")
        };
        var requestedWindow = window;
        if (window > source.Mat.Width || window > source.Mat.Height)
        {
            if (windowPolicy == "error")
                throw new InvalidOperationException($"image.illuminationNormalize windowSize={window} exceeds image {source.Mat.Width}x{source.Mat.Height}; reduce it or select clamp.");
            window = Math.Min(requestedWindow, Math.Min(source.Mat.Width, source.Mat.Height) | 1);
            if (window > Math.Min(source.Mat.Width, source.Mat.Height)) window -= 2;
            window = Math.Max(1, window);
        }
        using var grayNative = ColorChannels.Gray(source);
        using var gray = JLVisionImages.ToMat(grayNative);
        using var gray32 = new Mat(); gray.ConvertTo(gray32, MatType.CV_32F);
        using var mean = new Mat();
        Cv2.BoxFilter(gray32, mean, MatType.CV_32F, new Size(window, window), new Point(-1, -1), normalize: true, borderType: border);
        using var floor = new Mat(); Cv2.Max(mean, Scalar.All(minimum), floor);
        using var floorHits = new Mat(); Cv2.Compare(mean, Scalar.All(minimum), floorHits, CmpTypes.LE);
        var floorHitCount = Cv2.CountNonZero(floorHits);
        using var normalized = new Mat(); Cv2.Divide(gray32, floor, normalized, target, 0);
        using var output = new Mat(); normalized.ConvertTo(output, MatType.CV_8U);
        cancellationToken.ThrowIfCancellationRequested();
        using var outputNative = JLVisionImages.FromMat(output);
        var result = DefectNodeHelpers.ImageResult(outputNative, new() { ["windowSize"] = window, ["requestedWindowSize"] = requestedWindow, ["targetMean"] = target, ["minBackground"] = minimum, ["borderMode"] = borderName, ["windowPolicy"] = windowPolicy });
        var outputs = new Dictionary<string, VisionValue>(result.Outputs);
        var summary = new Dictionary<string, object?>(result.Summary);
        var owned = outputs["image"].Require<VisionImage>("image");
        var saturation = PreprocessP1Diagnostics.ByteSaturation(owned.Mat, 255);
        DefectNodeHelpers.AttachByteStats(outputs, summary, owned.Mat, Type, ("windowSize", window), ("requestedWindowSize", requestedWindow), ("targetMean", target), ("minBackground", minimum), ("borderMode", borderName), ("windowPolicy", windowPolicy), ("floorHitCount", floorHitCount), ("floorHitRatio", floorHitCount / (double)gray.Total()), ("clippedLowPixels", saturation.Low), ("clippedHighPixels", saturation.High), ("clippedRatio", saturation.Ratio), ("targetMeanZeroSemantics", target == 0 ? "all output pixels are zero" : "division target"));
        if (!outputs.ContainsKey("diagnostics"))
            throw new InvalidOperationException("image.illuminationNormalize diagnostics missing.");
        return ValueTask.FromResult(result with { Outputs = outputs, Summary = summary });
    }
}

public sealed class ImageSharpenNode : IVisionNodeExecutor
{
    public string Type => "image.sharpen";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));
        var window = ColorChannels.Window(node, 5); var amount = node.GetDouble("amount", 1);
        if (!double.IsFinite(amount) || amount is < 0 or > 10) throw new InvalidOperationException("Sharpen amount must be finite in 0..10.");
        var maxOvershoot = node.GetDouble("maxOvershoot", 0);
        if (!double.IsFinite(maxOvershoot) || maxOvershoot < 0 || maxOvershoot > 255)
            throw new InvalidOperationException("Sharpen maxOvershoot must be finite in 0..255 (0 disables the cap).");
        var source = context.RequireImage("image");
        if (source.Mat.Empty()) throw new InvalidOperationException("image.sharpen requires a non-empty image.");
        if (source.Mat.Width < 1 || source.Mat.Height < 1)
            throw new InvalidOperationException("image.sharpen image dimensions must be positive.");
        using var grayNative = ColorChannels.Gray(source);
        using var gray = JLVisionImages.ToMat(grayNative);
        using var gray32 = new Mat(); gray.ConvertTo(gray32, MatType.CV_32F);
        using var mean = new Mat();
        Cv2.BoxFilter(gray32, mean, MatType.CV_32F, new Size(window, window), new Point(-1, -1), normalize: true, borderType: BorderTypes.Reflect);
        using var detail = new Mat(); Cv2.Subtract(gray32, mean, detail);
        using var absDetail = new Mat(); Cv2.Absdiff(detail, Scalar.All(0), absDetail);
        Cv2.MinMaxLoc(absDetail, out double _, out var maxDetail);
        var meanDetail = Cv2.Mean(absDetail).Val0;
        var amountUsed = maxOvershoot > 0 && maxDetail > 1e-12 ? Math.Min(amount, maxOvershoot / maxDetail) : amount;
        using var weighted = new Mat(); detail.ConvertTo(weighted, MatType.CV_32F, amountUsed, 0);
        using var sharpened = new Mat(); Cv2.Add(gray32, weighted, sharpened);
        using var output = new Mat(); sharpened.ConvertTo(output, MatType.CV_8U);
        cancellationToken.ThrowIfCancellationRequested();
        using var outputNative = JLVisionImages.FromMat(output);
        var result = DefectNodeHelpers.ImageResult(outputNative, new() { ["windowSize"] = window, ["amount"] = amount, ["amountUsed"] = amountUsed, ["maxOvershoot"] = maxOvershoot });
        var outputs = new Dictionary<string, VisionValue>(result.Outputs);
        var summary = new Dictionary<string, object?>(result.Summary);
        var owned = outputs["image"].Require<VisionImage>("image");
        var saturation = PreprocessP1Diagnostics.ByteSaturation(owned.Mat, 255);
        var colorConverted = source.Mat.Channels() != 1;
        DefectNodeHelpers.AttachByteStats(outputs, summary, owned.Mat, Type, ("windowSize", window), ("amount", amount), ("amountUsed", amountUsed), ("maxOvershoot", maxOvershoot), ("maxDetail", maxDetail), ("meanDetail", meanDetail), ("clippedLowPixels", saturation.Low), ("clippedHighPixels", saturation.High), ("clippedRatio", saturation.Ratio), ("colorConverted", colorConverted), ("inputDepth", source.Mat.Depth().ToString()), ("inputChannels", source.Mat.Channels()), ("noiseAmplificationWarning", amountUsed > 1 || saturation.Ratio > .01));
        outputs["colorConverted"] = VisionValue.Boolean(colorConverted);
        outputs["clippedPixelCount"] = VisionValue.Integer(saturation.Low + saturation.High);
        summary["colorConverted"] = colorConverted;
        if (!outputs.ContainsKey("diagnostics"))
            throw new InvalidOperationException("image.sharpen diagnostics missing.");
        return ValueTask.FromResult(result with { Outputs = outputs, Summary = summary });
    }
}
