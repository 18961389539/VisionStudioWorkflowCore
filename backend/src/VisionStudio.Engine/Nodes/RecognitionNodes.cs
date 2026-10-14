using VisionStudio.Engine.Algorithms;
using System.Text.Json;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;
using VisionStudio.Engine.Runtime;
using TesseractOCR;
using TesseractOCR.Enums;
using ZXing;
using ZXing.Common;
using TesseractPixImage = TesseractOCR.Pix.Image;

namespace VisionStudio.Engine.Nodes;

/// <summary>Decodes all supported QR and common one-dimensional formats from a real image.</summary>
public sealed class BarcodeDecodeNode : IVisionNodeExecutor
{
    private static readonly BarcodeFormat[] OneDimensionalFormats =
    [
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93,
        BarcodeFormat.EAN_8, BarcodeFormat.EAN_13, BarcodeFormat.UPC_A,
        BarcodeFormat.UPC_E, BarcodeFormat.ITF, BarcodeFormat.CODABAR
    ];

    public string Type => "recognition.barcode";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        using var gray = RecognitionImageHelpers.ToGray8(source);
        var mode = node.GetString("mode", "all").Trim().ToLowerInvariant();
        if (mode is not ("all" or "qr" or "1d"))
            throw new InvalidOperationException($"Unsupported barcode mode '{mode}'. Supported modes are 'all', 'qr', and '1d'.");
        var tryHarder = node.GetBool("tryHarder", true);
        var multiple = node.GetBool("multiple", true);
        var formats = mode switch
        {
            "qr" => [BarcodeFormat.QR_CODE],
            "1d" => OneDimensionalFormats,
            "all" => [.. OneDimensionalFormats, BarcodeFormat.QR_CODE],
            _ => throw new InvalidOperationException($"Unsupported barcode mode '{mode}'.")
        };

        gray.GetArray(out byte[] pixels);
        var luminance = new RGBLuminanceSource(pixels, gray.Width, gray.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new BarcodeReaderGeneric { AutoRotate = true };
        reader.Options.PossibleFormats = formats.ToList();
        reader.Options.TryHarder = tryHarder;
        var decoded = multiple ? reader.DecodeMultiple(luminance) : Wrap(reader.Decode(luminance));
        cancellationToken.ThrowIfCancellationRequested();
        var results = (decoded ?? System.Array.Empty<ZXing.Result>())
            .Where(x => mode != "qr" || x.BarcodeFormat == BarcodeFormat.QR_CODE)
            .Where(x => mode != "1d" || x.BarcodeFormat != BarcodeFormat.QR_CODE)
            .Select(x => new BarcodeResult(
                x.Text,
                x.BarcodeFormat.ToString(),
                x.ResultPoints?.Select(p => new { x = p.X, y = p.Y }).ToArray() ?? []))
            .ToArray();
        var json = JsonSerializer.Serialize(results);
        var outputs = new Dictionary<string, VisionValue>
        {
            ["found"] = VisionValue.Boolean(results.Length > 0),
            ["count"] = VisionValue.Integer(results.Length),
            ["results"] = VisionValue.String(json)
        };
        if (results.Length == 1)
        {
            outputs["text"] = VisionValue.String(results[0].Text);
            outputs["format"] = VisionValue.String(results[0].Format);
        }
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["tryHarder"] = tryHarder,
            ["multiple"] = multiple,
            ["count"] = results.Length
        }));
    }

    private static ZXing.Result[]? Wrap(ZXing.Result? result) => result is null ? null : [result];
    private sealed record BarcodeResult(string Text, string Format, object[] Points);
}

/// <summary>Runs Tesseract 5 against the configured tessdata language model.</summary>
public sealed class OcrNode : IVisionNodeExecutor
{
    public string Type => "recognition.ocr";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        var dataPath = node.GetString("dataPath", string.Empty).Trim();
        var language = node.GetString("language", "eng").Trim();
        if (dataPath.Length == 0)
            throw new InvalidOperationException("OCR requires dataPath to a directory containing Tesseract .traineddata language files.");
        if (language.Length == 0 || language.StartsWith('+') || language.EndsWith('+') || language.Contains("++", StringComparison.Ordinal))
            throw new InvalidOperationException("OCR requires a non-empty language code (for example 'eng' or 'eng+chi_sim').");
        var languageCodes = language.Split('+', StringSplitOptions.None);
        if (languageCodes.Any(code => code.Length == 0 || code.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')))
            throw new InvalidOperationException("OCR language must contain valid language codes separated by single '+' characters (for example 'eng+chi_sim').");

        var resolvedDataPath = Path.GetFullPath(dataPath);
        if (!Directory.Exists(resolvedDataPath))
            throw new InvalidOperationException($"OCR tessdata directory '{resolvedDataPath}' does not exist.");
        foreach (var code in languageCodes)
        {
            var trainedData = Path.Combine(resolvedDataPath, $"{code}.traineddata");
            if (!File.Exists(trainedData))
                throw new InvalidOperationException($"OCR language model '{trainedData}' is missing; configure the required .traineddata file.");
        }

        using var gray = RecognitionImageHelpers.ToGray8(source);
        Cv2.ImEncode(".png", gray, out var encoded);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var engine = new TesseractOCR.Engine(resolvedDataPath, language, EngineMode.Default);
            using var image = TesseractPixImage.LoadFromMemory(encoded);
            using var page = engine.Process(image);
            cancellationToken.ThrowIfCancellationRequested();
            var text = page.Text.Trim();
            var confidence = Math.Clamp(double.IsFinite(page.MeanConfidence) ? page.MeanConfidence : 0, 0, 1);
            return ValueTask.FromResult(new NodeExecutorResult(
                new Dictionary<string, VisionValue>
                {
                    ["text"] = VisionValue.String(text),
                    ["confidence"] = VisionValue.Double(confidence),
                    ["result"] = VisionValue.String(JsonSerializer.Serialize(new { text, confidence, language }))
                },
                new Dictionary<string, object?> { ["language"] = language, ["confidence"] = confidence, ["characters"] = text.Length }));
        }
        catch (Exception ex) when (ex is not (InvalidOperationException or OperationCanceledException))
        {
            throw new InvalidOperationException($"OCR failed using language '{language}' and tessdata directory '{resolvedDataPath}': {ex.Message}", ex);
        }
        finally
        {
            // Encoded input is managed memory; make its lifetime explicit around native Leptonica processing.
            System.Array.Clear(encoded);
        }
    }
}

internal static class RecognitionImageHelpers
{
    public static Mat ToGray8(Mat source)
    {
        if (source.Empty()) throw new InvalidOperationException("Recognition requires a non-empty image.");
        Mat? gray = null;
        try
        {
            if (source.Channels() == 1)
                gray = source.Clone();
            else
            {
                var code = source.Channels() switch
                {
                    3 => ColorConversionCodes.BGR2GRAY,
                    4 => ColorConversionCodes.BGRA2GRAY,
                    _ => throw new InvalidOperationException($"Recognition supports 1, 3, or 4 channel images; received {source.Channels()}.")
                };
                JLVisionOperators.CvtColor(source, gray = new Mat(), code);
            }

            if (gray.Depth() == MatType.CV_8U) return gray;
            var output = new Mat();
            var scale = gray.Depth() == MatType.CV_16U ? 1d / 257d : 1d;
            gray.ConvertTo(output, MatType.CV_8UC1, scale);
            gray.Dispose();
            gray = null;
            return output;
        }
        catch
        {
            gray?.Dispose();
            throw;
        }
        finally
        {
        }
    }
}
