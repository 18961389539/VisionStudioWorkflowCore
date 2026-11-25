using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Nodes;

public sealed class ClaheNode : IVisionNodeExecutor
{
    public string Type => "image.clahe";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var clipLimit = node.GetDouble("clipLimit", 2);
        if (!double.IsFinite(clipLimit) || clipLimit is < .1 or > 40)
            throw new InvalidOperationException("image.clahe clipLimit must be within 0.1..40.");
        var tilesX = node.GetInt("tilesX", 8);
        var tilesY = node.GetInt("tilesY", 8);
        if (tilesX is < 1 or > 64 || tilesY is < 1 or > 64)
            throw new InvalidOperationException("image.clahe tiles must be within 1..64.");
        var colorMode = node.GetString("colorMode", "luminance").Trim().ToLowerInvariant();
        if (colorMode is not ("luminance" or "gray"))
            throw new InvalidOperationException($"image.clahe colorMode '{colorMode}' is not supported (luminance | gray).");

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.clahe requires a non-empty image.");
        if (sourceMat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("image.clahe requires an 8-bit image; convert 16-bit sources with image.depthScale first.");
        if (sourceMat.Channels() is not (1 or 3))
            throw new InvalidOperationException($"image.clahe supports 1 or 3 channel images; received {sourceMat.Channels()}.");

        var roi = context.TryGetValue("roi", out var roiValue) ? roiValue.Require<VisionRoi>("roi") : node.GetRoi();
        using var mask = roi?.BuildMask(sourceMat.Size(), cancellationToken);

        Mat? output = null;
        try
        {
            output = PreprocessEnhancementAlgorithms.Clahe(sourceMat, mask, clipLimit, tilesX, tilesY, colorMode);
            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("clipLimit", clipLimit),
                    ("tilesX", tilesX), ("tilesY", tilesY), ("colorMode", colorMode),
                    ("roiApplied", roi is not null), ("width", image.Width), ("height", image.Height),
                    ("channels", image.Mat.Channels()));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["width"] = VisionValue.Integer(image.Width),
                        ["height"] = VisionValue.Integer(image.Height),
                        ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                        ["clipLimit"] = VisionValue.Double(clipLimit),
                        ["tilesX"] = VisionValue.Integer(tilesX),
                        ["tilesY"] = VisionValue.Integer(tilesY),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["clipLimit"] = clipLimit, ["tilesX"] = tilesX, ["tilesY"] = tilesY,
                        ["colorMode"] = colorMode, ["roiApplied"] = roi is not null,
                        ["width"] = image.Width, ["height"] = image.Height,
                        ["channels"] = image.Mat.Channels(), ["algorithmBackend"] = "opencv",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            output?.Dispose();
        }
    }
}

public sealed class PolarUnwrapNode : IVisionNodeExecutor
{
    public string Type => "image.polarUnwrap";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.polarUnwrap requires a non-empty image.");
        if (sourceMat.Channels() is < 1 or > 4)
            throw new InvalidOperationException("image.polarUnwrap supports 1..4 channel images.");

        var width = sourceMat.Width; var height = sourceMat.Height;
        var cx = node.GetDouble("centerX", 0); var cy = node.GetDouble("centerY", 0);
        var hasCircle = context.TryGetValue("circle", out var circleValue);
        var circle = hasCircle ? circleValue.Require<VisionCircle>("circle") : default;
        if (hasCircle)
        {
            if (!double.IsFinite(circle.X) || !double.IsFinite(circle.Y) || !double.IsFinite(circle.Radius) || circle.Radius <= 0)
                throw new InvalidOperationException("image.polarUnwrap circle must have finite center and a positive radius.");
            cx = circle.X; cy = circle.Y;
        }
        if (!double.IsFinite(cx) || !double.IsFinite(cy))
            throw new InvalidOperationException("image.polarUnwrap center must be finite.");

        var inner = node.GetDouble("innerRadius", 0);
        var outer = node.GetDouble("outerRadius", 0);
        if (!double.IsFinite(inner) || inner < 0) throw new InvalidOperationException("image.polarUnwrap innerRadius must be finite and >= 0.");
        if (!double.IsFinite(outer) || outer < 0) throw new InvalidOperationException("image.polarUnwrap outerRadius must be finite and >= 0 (0 derives it).");
        if (outer <= 0) outer = hasCircle ? circle.Radius : PreprocessGapMath.AutoOuterRadius(cx, cy, width, height);
        if (outer <= inner + 1e-6) throw new InvalidOperationException("image.polarUnwrap requires outerRadius > innerRadius.");
        if (outer > Math.Max(width, height) * 4.0)
            throw new InvalidOperationException("image.polarUnwrap outerRadius is far outside the image; check the center.");

        var startAngle = node.GetDouble("angleStartDeg", 0);
        var sweep = node.GetDouble("angleSweepDeg", 360);
        if (!double.IsFinite(startAngle) || startAngle is < -3600 or > 3600)
            throw new InvalidOperationException("image.polarUnwrap angleStartDeg must be within -3600..3600.");
        if (!double.IsFinite(sweep) || sweep is < 1 or > 360)
            throw new InvalidOperationException("image.polarUnwrap angleSweepDeg must be within 1..360.");
        var direction = node.GetString("direction", "counterClockwise").Trim().ToLowerInvariant();
        if (direction is not ("counterclockwise" or "clockwise"))
            throw new InvalidOperationException($"image.polarUnwrap direction '{direction}' is not supported (counterClockwise | clockwise).");
        var interpolation = node.GetString("interpolation", "linear").Trim().ToLowerInvariant();
        var inter = interpolation switch
        {
            "nearest" => InterpolationFlags.Nearest,
            "linear" => InterpolationFlags.Linear,
            "cubic" => InterpolationFlags.Cubic,
            _ => throw new InvalidOperationException($"image.polarUnwrap interpolation '{interpolation}' is not supported (nearest | linear | cubic).")
        };

        var band = outer - inner;
        var radial = node.GetInt("radialSamples", 0);
        if (radial <= 0) radial = (int)Math.Clamp(Math.Round(band), 2, 4096);
        if (radial is < 2 or > 4096) throw new InvalidOperationException("image.polarUnwrap radialSamples must be within 2..4096.");
        var midRadius = inner + band / 2;
        var angular = node.GetInt("angularSamples", 0);
        if (angular <= 0) angular = (int)Math.Clamp(Math.Round(2 * Math.PI * midRadius * (sweep / 360.0)), 16, 8192);
        if (angular is < 8 or > 8192) throw new InvalidOperationException("image.polarUnwrap angularSamples must be within 8..8192.");

        var total = (long)angular * radial;
        if (total > 16_000_000) throw new InvalidOperationException($"image.polarUnwrap would allocate {total} pixels; lower the sample counts.");

        Mat? output = null;
        try
        {
            output = PreprocessEnhancementAlgorithms.UnwrapPolar(
                sourceMat, cx, cy, inner, outer, startAngle, sweep, direction, radial, angular, inter, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("centerX", cx), ("centerY", cy),
                    ("innerRadius", inner), ("outerRadius", outer), ("angleStartDeg", startAngle),
                    ("angleSweepDeg", sweep), ("direction", direction), ("interpolation", interpolation),
                    ("width", angular), ("height", radial));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["width"] = VisionValue.Integer(angular),
                        ["height"] = VisionValue.Integer(radial),
                        ["centerX"] = VisionValue.Double(cx), ["centerY"] = VisionValue.Double(cy),
                        ["innerRadius"] = VisionValue.Double(inner), ["outerRadius"] = VisionValue.Double(outer),
                        ["angleStartDeg"] = VisionValue.Double(startAngle), ["angleSweepDeg"] = VisionValue.Double(sweep),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["centerX"] = cx, ["centerY"] = cy, ["innerRadius"] = inner, ["outerRadius"] = outer,
                        ["angleStartDeg"] = startAngle, ["angleSweepDeg"] = sweep, ["direction"] = direction,
                        ["interpolation"] = interpolation, ["width"] = angular, ["height"] = radial,
                        ["algorithmBackend"] = "opencv", ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            output?.Dispose();
        }
    }
}

public sealed class BilateralFilterNode : IVisionNodeExecutor
{
    public string Type => "image.bilateral";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = context.RequireImage("image");
        var sourceMat = source.Mat;
        if (sourceMat.Empty()) throw new InvalidOperationException("image.bilateral requires a non-empty image.");
        if (sourceMat.Depth() != MatType.CV_8U)
            throw new InvalidOperationException("image.bilateral requires an 8-bit image; convert 16-bit sources with image.depthScale first.");
        if (sourceMat.Channels() is not (1 or 3))
            throw new InvalidOperationException($"image.bilateral supports 1 or 3 channel images; received {sourceMat.Channels()}.");

        var diameter = node.GetInt("diameter", 0);
        if (diameter is < 0 or > 31) throw new InvalidOperationException("image.bilateral diameter must be within 0..31 (0=auto).");
        var sigmaColor = node.GetDouble("sigmaColor", 25);
        var sigmaSpace = node.GetDouble("sigmaSpace", 25);
        if (!double.IsFinite(sigmaColor) || sigmaColor is < 1 or > 500)
            throw new InvalidOperationException("image.bilateral sigmaColor must be within 1..500.");
        if (!double.IsFinite(sigmaSpace) || sigmaSpace is < 1 or > 500)
            throw new InvalidOperationException("image.bilateral sigmaSpace must be within 1..500.");
        if (diameter == 0) diameter = sigmaSpace >= 50 ? 9 : 5;

        var roi = context.TryGetValue("roi", out var roiValue) ? roiValue.Require<VisionRoi>("roi") : node.GetRoi();
        using var mask = roi?.BuildMask(sourceMat.Size(), cancellationToken);

        Mat? output = null;
        try
        {
            output = PreprocessEnhancementAlgorithms.Bilateral(sourceMat, mask, diameter, sigmaColor, sigmaSpace, cancellationToken);
            var image = VisionImage.Own(output);
            output = null;
            return ValueTask.FromResult(NodeOutputContracts.CreateOwnedResult(image, () =>
            {
                var diagnostics = CoordinateValidation.Diagnostics(Type, ("diameter", diameter),
                    ("sigmaColor", sigmaColor), ("sigmaSpace", sigmaSpace), ("roiApplied", roi is not null),
                    ("width", image.Width), ("height", image.Height), ("channels", image.Mat.Channels()));
                return new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["image"] = VisionValue.Image(image),
                        ["width"] = VisionValue.Integer(image.Width),
                        ["height"] = VisionValue.Integer(image.Height),
                        ["channels"] = VisionValue.Integer(image.Mat.Channels()),
                        ["sigmaColor"] = VisionValue.Double(sigmaColor),
                        ["sigmaSpace"] = VisionValue.Double(sigmaSpace),
                        ["diameter"] = VisionValue.Integer(diameter),
                        ["diagnostics"] = VisionValue.String(diagnostics)
                    },
                    new Dictionary<string, object?>
                    {
                        ["diameter"] = diameter, ["sigmaColor"] = sigmaColor, ["sigmaSpace"] = sigmaSpace,
                        ["roiApplied"] = roi is not null, ["width"] = image.Width, ["height"] = image.Height,
                        ["channels"] = image.Mat.Channels(), ["algorithmBackend"] = "opencv",
                        ["diagnostics"] = diagnostics
                    });
            }, cancellationToken));
        }
        finally
        {
            output?.Dispose();
        }
    }
}
