using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine;

/// <summary>Explicit pixel mappings keep generic spatial operations traceable by the graph compiler.</summary>
internal static class ImageOperationGeometry
{
    internal static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
        { "cropPart", "cropRectangle2", "cropDomainRel", "rotateImage", "zoomImageFactor", "addBorder", "changeFormat" };
    internal static bool IsSpatial(NodeDefinition node) => node.Type.Equals("jlvision.imageOp", StringComparison.OrdinalIgnoreCase)
        && Operations.Contains(node.GetString("operator", "abs").Trim());

    internal static NodeExecutorResult Execute(VisionImage source, NodeDefinition node, string operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double Read(string name, double fallback) => OperatorParameterForms.Number(node, name, fallback);
        var width = source.Width; var height = source.Height;
        double a = 1, b = 0, c = 0, d = 0, e = 1, f = 0;
        int Dimension(double value)
        {
            if (!double.IsFinite(value) || value < 1 || value > 32768) throw new InvalidOperationException("Spatial output dimensions must be in 1..32768.");
            return Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
        }
        switch (operation)
        {
            case "cropPart":
                c = -Read("column", 0); f = -Read("row", 0);
                width = Dimension(Read("size", 3)); height = Dimension(Read("size2", 3));
                break;
            case "cropDomainRel":
                var left = Read("column", 0); var top = Read("row", 0);
                width = Dimension(source.Width - left - Read("size2", 0));
                height = Dimension(source.Height - top - Read("size", 0)); c = -left; f = -top;
                break;
            case "cropRectangle2":
                var phi = Read("mult", 0); var x = Read("column", 0); var y = Read("row", 0);
                width = Dimension(2 * Read("size", 20)); height = Dimension(2 * Read("size2", 20));
                a = e = Math.Cos(phi); b = Math.Sin(phi); d = -b;
                c = (width - 1) / 2d - a * x - b * y; f = (height - 1) / 2d - d * x - e * y;
                break;
            case "rotateImage":
                var angle = Read("mult", 0); a = e = Math.Cos(angle); b = Math.Sin(angle); d = -b;
                width = Dimension(Math.Ceiling(source.Width * Math.Abs(a) + source.Height * Math.Abs(b) - 1e-10));
                height = Dimension(Math.Ceiling(source.Width * Math.Abs(b) + source.Height * Math.Abs(a) - 1e-10));
                var cx = (source.Width - 1) / 2d; var cy = (source.Height - 1) / 2d;
                c = (width - 1) / 2d - a * cx - b * cy; f = (height - 1) / 2d - d * cx - e * cy;
                break;
            case "zoomImageFactor":
                width = Dimension(source.Width * Read("size", 1)); height = Dimension(source.Height * Read("size2", 1));
                a = (double)width / source.Width; e = (double)height / source.Height;
                c = (a - 1) / 2; f = (e - 1) / 2;
                break;
            case "addBorder":
                var border = Math.Round(Read("size", 1), MidpointRounding.AwayFromZero);
                width = Dimension(source.Width + 2 * border); height = Dimension(source.Height + 2 * border); c = f = border;
                break;
            case "changeFormat":
                width = Dimension(Read("size", 256)); height = Dimension(Read("size2", 256)); break;
        }
        if ((long)width * height > 100_000_000) throw new InvalidOperationException("Spatial operation exceeds the 100 MP output budget.");
        using var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.SetArray(new[] { a, b, c, d, e, f });
        var output = new Mat();
        try
        {
            JLVisionOperators.WarpAffine(source.Mat, output, matrix, new Size(width, height), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0));
            token.ThrowIfCancellationRequested();
            using var preview = Nodes.JLVisionNodeSupport.Preview(output);
            var transform = new VisionTransform2D(source.CoordinateFrame, $"image:{node.Id}:spatial", "px", a, b, c, d, e, f, 0, 0, 1);
            var diagnostics = Nodes.CoordinateValidation.Diagnostics(node.Type, ("operator", operation), ("coordinateMapping", "explicit-pixel-affine"),
                ("inputWidth", source.Width), ("inputHeight", source.Height), ("outputWidth", width), ("outputHeight", height));
            return new(new Dictionary<string, VisionValue> { ["image"] = VisionValue.Image(VisionImage.Own(output)),
                ["overlay"] = VisionValue.Image(VisionImage.Own(preview.Clone())), ["transform"] = VisionValue.Transform(transform),
                ["operator"] = VisionValue.String(operation), ["width"] = VisionValue.Integer(width), ["height"] = VisionValue.Integer(height),
                ["diagnostics"] = VisionValue.String(diagnostics) }, new Dictionary<string, object?> { ["algorithmBackend"] = "jlvision", ["operator"] = operation,
                ["outputWidth"] = width, ["outputHeight"] = height, ["sourceFrame"] = transform.SourceFrame, ["targetFrame"] = transform.TargetFrame,
                ["diagnostics"] = diagnostics });
        }
        catch { output.Dispose(); throw; }
    }
}
