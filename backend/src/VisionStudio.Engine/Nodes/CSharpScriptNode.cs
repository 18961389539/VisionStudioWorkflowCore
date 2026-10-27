using OpenCvSharp;
using VisionStudio.Engine.Camera;
using VisionStudio.Scripting;

namespace VisionStudio.Engine.Nodes;

/// <summary>
/// Built-in C# script node (script.csharp, 2026-10-27).
///
/// Scripts are compiled in-process with Roslyn against a minimal reference set. The runner rejects
/// file/network/reflection/process/threading APIs with a syntax-tree allowlist, injects a cooperative
/// loop budget, and exposes only typed inputs, an optional Mat image and a typed output object.
/// This is a trusted-engineer runtime; it is not a hard OS sandbox.
/// </summary>
public sealed class CSharpScriptNode : IVisionNodeExecutor
{
    public string Type => "script.csharp";

    private static readonly string[] InputNames = ["a", "b", "c", "d", "e", "f", "g", "h"];

    public async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (node is null) throw new InvalidOperationException(Type + " requires a node definition.");
        if (context is null) throw new ArgumentNullException(nameof(context));

        var source = node.GetString("source", CSharpScriptCatalog.DefaultSource);
        var timeoutMs = node.GetInt("timeoutMs", 250);
        var maxTicks = node.GetInt("maxTicks", 1_000_000);
        var maxOutputBytes = node.GetInt("maxOutputBytes", 65_536);
        var maxImagePixels = node.GetInt("maxImagePixels", 16_777_216);
        if (timeoutMs is < 10 or > 10_000)
            throw new InvalidOperationException("script.csharp timeoutMs must be within 10..10000.");
        if (maxTicks is < 100 or > 100_000_000)
            throw new InvalidOperationException("script.csharp maxTicks must be within 100..100000000.");
        if (maxOutputBytes is < 1 or > 1_048_576)
            throw new InvalidOperationException("script.csharp maxOutputBytes must be within 1..1048576.");
        if (maxImagePixels is < 1 or > 100_000_000)
            throw new InvalidOperationException("script.csharp maxImagePixels must be within 1..100000000.");

        Mat? inputImage = null;
        if (context.TryGetValue("image", out var imageValue))
        {
            var image = imageValue.Require<IVisionImage>("image");
            if (image.NativeImage is not Mat mat)
                throw new InvalidOperationException("script.csharp requires an OpenCV Mat image input.");
            inputImage = mat.Clone();
        }

        var inputs = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in InputNames)
            {
                if (!context.TryGetValue(name, out var value)) continue;
                inputs[name] = value.Type switch
                {
                    VisionDataType.Double => value.Require<double>(name),
                    VisionDataType.Integer => value.Require<long>(name),
                    VisionDataType.Boolean => value.Require<bool>(name),
                    VisionDataType.String => value.Require<string>(name),
                    VisionDataType.NumberList => value.Require<IReadOnlyList<double>>(name).ToArray(),
                    VisionDataType.PointList => value.Require<IReadOnlyList<VisionPoint2D>>(name)
                        .Select(point => new ScriptPoint(point.X, point.Y)).ToArray(),
                    _ => throw new InvalidOperationException(
                        $"script.csharp input '{name}' has type {value.Type}; supported script input types are Double, Integer, Boolean, String, NumberList and PointList. Connect images to the image port and use dedicated nodes for geometry.")
                };
            }

            var imageForScript = inputImage;
            inputImage = null; // ownership transferred to ScriptGlobals and disposed by the runner.
            using var scriptResult = await CSharpScriptRunner.RunAsync(
                source,
                new ScriptExecutionOptions(timeoutMs, maxTicks, maxOutputBytes, maxImagePixels),
                inputs,
                imageForScript,
                cancellationToken).ConfigureAwait(false);

            var outputs = new Dictionary<string, VisionValue>(StringComparer.OrdinalIgnoreCase);
            if (scriptResult.Result is { } result)
            {
                outputs["result"] = result switch
                {
                    double number => VisionValue.Double(number),
                    long integer => VisionValue.Integer(integer),
                    bool boolean => VisionValue.Boolean(boolean),
                    string text => VisionValue.String(text),
                    _ => throw new InvalidOperationException(
                        $"script.csharp produced unsupported result type '{result.GetType().FullName}'.")
                };
            }
            if (scriptResult.Number is { } numberValue) outputs["number"] = VisionValue.Double(numberValue);
            if (scriptResult.Text is { } textValue) outputs["text"] = VisionValue.String(textValue);
            if (scriptResult.Boolean is { } booleanValue) outputs["boolean"] = VisionValue.Boolean(booleanValue);
            if (scriptResult.Json is { } jsonValue) outputs["json"] = VisionValue.String(jsonValue);

            var diagnostics = CoordinateValidation.Diagnostics(
                Type,
                ("sourceHash", scriptResult.SourceHash),
                ("compileMs", Math.Round(scriptResult.CompileMs, 3)),
                ("executeMs", Math.Round(scriptResult.ExecuteMs, 3)),
                ("ticks", scriptResult.Ticks),
                ("resultType", scriptResult.Result?.GetType().Name ?? "none"),
                ("logs", string.Join(" | ", scriptResult.Logs)),
                ("outputImage", scriptResult.Image is null ? "none" : $"{scriptResult.Image.Width}x{scriptResult.Image.Height}"));
            outputs["diagnostics"] = VisionValue.String(diagnostics);

            var summary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceHash"] = scriptResult.SourceHash,
                ["compileMs"] = Math.Round(scriptResult.CompileMs, 3),
                ["executeMs"] = Math.Round(scriptResult.ExecuteMs, 3),
                ["ticks"] = scriptResult.Ticks,
                ["logs"] = scriptResult.Logs,
                ["outputs"] = outputs.Keys.ToArray(),
                ["algorithmBackend"] = "roslyn",
                ["scriptBackend"] = "csharp",
                ["diagnostics"] = diagnostics
            };

            var outputImage = scriptResult.TakeImage();
            if (outputImage is null)
                return new NodeExecutorResult(outputs, summary);

            var imageOutput = VisionImage.Own(outputImage);
            outputs["image"] = VisionValue.Image(imageOutput);
            return NodeOutputContracts.CreateOwnedResult(imageOutput,
                () => new NodeExecutorResult(outputs, summary), cancellationToken);
        }
        finally
        {
            inputImage?.Dispose();
        }
    }
}
