using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;

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

    public static ColorChannels Create(VisionImage source, string space)
    {
        Validate(source);
        if (space is not ("rgb" or "hsv" or "lab")) throw new InvalidOperationException("Color space must be rgb, hsv or lab.");
        using var native = JLVisionImages.FromMat(source.Mat);
        using var count = native.CountChannels();
        using var redByte = count.I == 1 ? native.CopyImage() : native.AccessChannel(1);
        using var greenByte = count.I == 1 ? native.CopyImage() : native.AccessChannel(2);
        using var blueByte = count.I == 1 ? native.CopyImage() : native.AccessChannel(3);
        using var r = Normalize(redByte); using var g = Normalize(greenByte); using var b = Normalize(blueByte);
        if (space == "rgb") return new(r.CopyImage(), g.CopyImage(), b.CopyImage(), "R,G,B");
        if (space == "lab")
        {
            // Native CIELAB conversion expects linear RGB. Camera byte buffers are
            // interpreted as sRGB, so decode its piecewise transfer function first.
            using var linearR = DecodeSrgb(r, source.Width, source.Height);
            using var linearG = DecodeSrgb(g, source.Width, source.Height);
            using var linearB = DecodeSrgb(b, source.Width, source.Height);
            var l = linearR.TransFromRgb(linearG, linearB, out var a, out var labB, "cielab");
            return new(l, a, labB, "L*,a*,b*");
        }
        using var first = r.TransFromRgb(g, b, out var second, out var third, "hsv");
        try
        {
            return new(first.ScaleImage(180 / Math.PI, 0d), second, third, "H(deg),S,V");
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
        var space = node.GetString("space", "hsv");
        using var channels = ColorChannels.Create(context.RequireImage("image"), space);
        var outputs = new Dictionary<string, VisionValue>();
        try
        {
            foreach (var (key, value) in new[] { ("channel1", channels.First), ("channel2", channels.Second), ("channel3", channels.Third) })
            {
                cancellationToken.ThrowIfCancellationRequested();
                outputs[key] = VisionValue.Image(VisionImage.Own(JLVisionImages.ToMat(value)));
            }
            outputs["channelNames"] = VisionValue.String(channels.Names);
            return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?> { ["space"] = space, ["channelNames"] = channels.Names, ["algorithmBackend"] = "JLVision" }));
        }
        catch { foreach (var output in outputs.Values) (output.Value as IDisposable)?.Dispose(); throw; }
    }
}

public sealed class RegionFromColorRangeNode : IVisionNodeExecutor
{
    public string Type => "region.fromColorRange";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (source, scoped) = RegionNodeHelpers.Source(context); var space = node.GetString("space", "hsv");
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
        return ValueTask.FromResult(result with { Summary = new Dictionary<string, object?>(result.Summary) { ["space"] = space, ["algorithmBackend"] = "JLVision" } });
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
            var outputs = new Dictionary<string, VisionValue> { ["found"] = VisionValue.Boolean(found), ["area"] = VisionValue.Double(area), ["region"] = VisionValue.Region(region) };
            var summary = new Dictionary<string, object?> { ["found"] = found, ["area"] = area, ["metric"] = "CIE76", ["referenceSource"] = hasReference ? "image" : "srgb-constant", ["algorithmBackend"] = "JLVision" };
            if (found)
            {
                var stats = DefectNodeHelpers.Statistics(distance, domain);
                outputs["deltaE"] = VisionValue.Double(stats.Mean); outputs["maxDeltaE"] = VisionValue.Double(stats.Max); outputs["stdDev"] = VisionValue.Double(stats.Deviation);
                summary["deltaE"] = stats.Mean; summary["maxDeltaE"] = stats.Max;
            }
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
        var tail = node.GetDouble("tailPercent", 0); var low = node.GetDouble("outputMin", 0); var high = node.GetDouble("outputMax", 255);
        if (!double.IsFinite(tail) || tail is < 0 or > 20 || !double.IsFinite(low) || !double.IsFinite(high) || low < 0 || high > 255 || low >= high || low != Math.Truncate(low) || high != Math.Truncate(high))
            throw new InvalidOperationException("Tail percent must be 0..20 and output bounds must be ordered integers within 0..255.");
        using var gray = ColorChannels.Gray(context.RequireImage("image")); using var domain = gray.GetDomain();
        gray.MinMaxGray(domain, tail, out double min, out double max, out double _);
        using var real = gray.ConvertImageType("real"); using var stretched = max > min ? real.ScaleImage((high - low) / (max - min), low - min * (high - low) / (max - min)) : real.CopyImage();
        using var lo = real.ScaleImage(0d, low); using var hi = real.ScaleImage(0d, high);
        using var lower = max > min ? stretched.MaxImage(lo) : stretched.CopyImage(); using var clamped = max > min ? lower.MinImage(hi) : lower.CopyImage();
        using var output = clamped.ConvertImageType("byte"); cancellationToken.ThrowIfCancellationRequested();
        var result = DefectNodeHelpers.ImageResult(output, new() { ["inputMin"] = min, ["inputMax"] = max, ["rangeCollapsed"] = max <= min, ["tailPercent"] = tail });
        return ValueTask.FromResult(result with { Outputs = new Dictionary<string, VisionValue>(result.Outputs) { ["inputMin"] = VisionValue.Double(min), ["inputMax"] = VisionValue.Double(max) } });
    }
}

public sealed class IlluminationNormalizeNode : IVisionNodeExecutor
{
    public string Type => "image.illuminationNormalize";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = ColorChannels.Window(node, 51); var target = node.GetDouble("targetMean", 128); var minimum = node.GetDouble("minBackground", 1);
        if (!double.IsFinite(target) || target is < 0 or > 255 || !double.IsFinite(minimum) || minimum is <= 0 or > 255)
            throw new InvalidOperationException("Target gray must be 0..255 and minimum background must be positive and <=255.");
        using var gray = ColorChannels.Gray(context.RequireImage("image")); using var real = gray.ConvertImageType("real");
        using var mean = real.MeanImage(window, window); using var floor = real.ScaleImage(0d, minimum); using var background = mean.MaxImage(floor);
        using var normalized = real.DivImage(background, target, 0d); using var output = normalized.ConvertImageType("byte");
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DefectNodeHelpers.ImageResult(output, new() { ["windowSize"] = window, ["targetMean"] = target, ["minBackground"] = minimum }));
    }
}

public sealed class ImageSharpenNode : IVisionNodeExecutor
{
    public string Type => "image.sharpen";
    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = ColorChannels.Window(node, 5); var amount = node.GetDouble("amount", 1);
        if (!double.IsFinite(amount) || amount is < 0 or > 10) throw new InvalidOperationException("Sharpen amount must be finite in 0..10.");
        using var gray = ColorChannels.Gray(context.RequireImage("image")); using var real = gray.ConvertImageType("real");
        using var mean = real.MeanImage(window, window); using var detail = real.SubImage(mean, 1d, 0d); using var weighted = detail.ScaleImage(amount, 0d);
        using var sharpened = real.AddImage(weighted, 1d, 0d); using var output = sharpened.ConvertImageType("byte");
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(DefectNodeHelpers.ImageResult(output, new() { ["windowSize"] = window, ["amount"] = amount }));
    }
}
