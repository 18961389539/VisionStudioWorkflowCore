using OpenCvSharp;
using VisionStudio.Engine.Algorithms;
using VisionStudio.Engine.Runtime;

namespace VisionStudio.Engine.Nodes;

/// <summary>Image encoding and atomic file publication share one validated configuration.</summary>
internal static class ImageFileWriter
{
    public static async ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = ImageSavePlan.Create(context, node);
        var path = plan.TargetPath;
        var codec = plan.Codec;
        var image = context.RequireImage("image");
        var level = NodeCompletionGuards.DiagnosticsLevel(node);
        if (!plan.ShouldSave)
            return Result(plan, path, false, 0, image.Mat.Empty() ? 0 : image.Width, image.Mat.Empty() ? 0 : image.Height, 0, level, "result-filtered", 0);
        if (image.Mat.Empty() || image.Width < 1 || image.Height < 1)
        {
            if (node.GetBool("failOnEmpty", true)) throw new InvalidOperationException("output.saveImage requires a non-empty image.");
            return Result(plan, path, false, 0, 0, 0, 0, level, "empty-image-skipped", 0);
        }
        if (Directory.Exists(path)) throw new IOException("目标图像路径已被目录占用，请更改保存文件名。");
        if (File.Exists(path))
        {
            if (plan.ConflictPolicy == "skip") return Result(plan, path, false, 0, image.Width, image.Height, 0, level, "exists-skipped", 1);
            if (plan.ConflictPolicy == "fail") throw new IOException("目标图像已存在；请选择另存新文件、跳过或显式覆盖。");
        }
        var maxWidth = Integer(node, "maxWidth", 0, 0, 32768);
        var maxHeight = Integer(node, "maxHeight", 0, 0, 32768);
        if (maxWidth > 0 && image.Width > maxWidth || maxHeight > 0 && image.Height > maxHeight)
            throw new InvalidOperationException("Image exceeds the configured output dimensions; no file was written.");
        var quality = Integer(node, "jpegQuality", 92, 1, 100);
        var compression = Integer(node, "pngCompression", 3, 0, 9);
        using var prepared = Prepare(image.Mat, Choice(node, "colorMode", "asIs", "asIs", "gray", "bgr"),
            Choice(node, "pixelPolicy", "asIs", "asIs", "u8", "u16"));
        var depth = prepared.Depth(); var channels = prepared.Channels();
        if (channels is not (1 or 3 or 4)) throw new InvalidOperationException("Image encoders require 1, 3 or 4 channels.");
        if (codec == ".png" && depth != MatType.CV_8U && depth != MatType.CV_16U)
            throw new InvalidOperationException("PNG requires 8-bit or 16-bit unsigned pixels; select pixelPolicy=u8/u16.");
        if (codec is ".bmp" or ".webp" or ".ppm" or ".pgm" or ".pbm" && depth != MatType.CV_8U
            && !(codec is ".ppm" or ".pgm" && depth == MatType.CV_16U))
            throw new InvalidOperationException("Selected codec cannot preserve this pixel depth; choose pixelPolicy=u8 or a compatible format.");
        if (codec is ".jpg" or ".jpeg" && (prepared.Depth() != MatType.CV_8U || prepared.Channels() is not (1 or 3)))
            throw new InvalidOperationException("JPEG requires an 8-bit gray or BGR image; select pixelPolicy=u8 and a compatible colorMode.");
        var encoding = codec is ".jpg" or ".jpeg"
            ? new[] { new ImageEncodingParam(ImwriteFlags.JpegQuality, quality) }
            : codec == ".png" ? new[] { new ImageEncodingParam(ImwriteFlags.PngCompression, compression) } : [];
        if (!Cv2.ImEncode(codec, prepared, out var bytes, encoding) || bytes.Length == 0)
            throw new IOException("Image encoding returned no bytes.");
        var directory = Path.GetDirectoryName(path)!;
        if (node.GetBool("createDirectories", true)) Directory.CreateDirectory(directory);
        else if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Output directory does not exist and Create Directories is disabled.");
        var budget = NodeCompletionGuards.RetryAttempts(NodeCompletionGuards.RetryPolicy(node));
        var collisions = 0;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep staging names short even when the final filename approaches the filesystem limit.
            var temporary = Path.Combine(directory, $".vision-image-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = path;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // The move decides the winner even if another writer appeared after preflight.
                        SaveImageNode.Commit(temporary, candidate, plan.ConflictPolicy == "overwrite");
                        return Result(plan, candidate, true, bytes.LongLength, prepared.Width, prepared.Height, attempt, level, "saved", collisions);
                    }
                    catch (IOException) when (plan.ConflictPolicy != "overwrite" && File.Exists(candidate))
                    {
                        collisions++;
                        if (plan.ConflictPolicy == "skip") return Result(plan, candidate, false, 0, prepared.Width, prepared.Height, attempt, level, "exists-skipped", collisions);
                        if (plan.ConflictPolicy == "fail") throw new IOException("目标文件在保存过程中已被其他写入者创建，未覆盖该文件。");
                        if (collisions >= 32) throw new IOException("连续发生文件名冲突，请检查输出目录并调整命名规则。");
                        candidate = plan.AlternatePath();
                    }
                }
            }
            catch (IOException) when (attempt < budget && collisions < 32 && !(plan.ConflictPolicy == "fail" && File.Exists(path)))
            {
                await Task.Delay(100 * attempt, cancellationToken).ConfigureAwait(false);
            }
            finally { SaveImageNode.TryDelete(temporary); }
        }
    }

    private static Mat Prepare(Mat source, string colorMode, string pixelPolicy)
    {
        var result = source.Clone();
        try
        {
            if (pixelPolicy != "asIs")
            {
                using var converted = new Mat();
                result.ConvertTo(converted, pixelPolicy == "u8" ? MatType.CV_8U : MatType.CV_16U);
                converted.CopyTo(result);
            }
            if (colorMode == "gray" && result.Channels() != 1 || colorMode == "bgr" && result.Channels() != 3)
            {
                var conversion = (colorMode, result.Channels()) switch
                {
                    ("gray", 3) => ColorConversionCodes.BGR2GRAY,
                    ("gray", 4) => ColorConversionCodes.BGRA2GRAY,
                    ("bgr", 1) => ColorConversionCodes.GRAY2BGR,
                    ("bgr", 4) => ColorConversionCodes.BGRA2BGR,
                    _ => throw new InvalidOperationException("Unsupported channel count for the requested color conversion.")
                };
                using var converted = new Mat();
                JLVisionOperators.CvtColor(result, converted, conversion);
                converted.CopyTo(result);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static int Integer(NodeDefinition node, string name, int fallback, int min, int max)
    {
        var value = node.GetDouble(name, fallback);
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < min || value > max)
            throw new InvalidOperationException($"{name} must be an integer in {min}..{max}.");
        return (int)value;
    }

    private static string Choice(NodeDefinition node, string name, string fallback, params string[] choices)
    {
        var value = node.GetString(name, fallback).Trim();
        return choices.FirstOrDefault(choice => choice.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Invalid {name}: {value}.");
    }

    private static NodeExecutorResult Result(ImageSavePlan plan, string path, bool saved, long bytes, int width, int height, int attempts, string level, string reason, int collisions)
    {
        var diagnostics = NodeCompletionGuards.BuildDiagnostics("output.saveImage", level, 0, Math.Max(1, attempts),
            ("path", path), ("requestedPath", plan.RequestedPath), ("format", plan.Codec), ("saved", saved), ("bytesWritten", bytes), ("width", width), ("height", height), ("reason", reason),
            ("fileName", Path.GetFileName(path)), ("runId", plan.RunId), ("hostRunId", plan.HostRunId), ("saveId", plan.SaveId), ("timestamp", plan.Timestamp),
            ("namingMode", plan.NamingMode), ("resultMode", plan.ResultMode), ("disposition", plan.Disposition), ("conflictPolicy", plan.ConflictPolicy), ("collisionCount", collisions), ("fileWriteAttempts", attempts));
        return new(new Dictionary<string, VisionValue>
        {
            ["path"] = VisionValue.String(path), ["saved"] = VisionValue.Boolean(saved), ["bytesWritten"] = VisionValue.Integer(bytes),
            ["format"] = VisionValue.String(plan.Codec), ["imageWidth"] = VisionValue.Integer(width), ["imageHeight"] = VisionValue.Integer(height),
            ["reason"] = VisionValue.String(reason), ["runId"] = VisionValue.String(plan.RunId), ["attemptCount"] = VisionValue.Integer(Math.Max(1, attempts)), ["warningCount"] = VisionValue.Integer(0),
            ["diagnostics"] = VisionValue.String(diagnostics)
        }, new Dictionary<string, object?> { ["path"] = path, ["saved"] = saved, ["bytesWritten"] = bytes, ["width"] = width,
            ["height"] = height, ["attempts"] = attempts, ["reason"] = reason, ["runId"] = plan.RunId, ["fileName"] = Path.GetFileName(path),
            ["disposition"] = plan.Disposition, ["conflictPolicy"] = plan.ConflictPolicy, ["diagnostics"] = diagnostics });
    }
}
