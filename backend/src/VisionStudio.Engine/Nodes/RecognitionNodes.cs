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

/// <summary>Decodes QR, DataMatrix/PDF417/Aztec and common one-dimensional formats from a real image.</summary>
public sealed class BarcodeDecodeNode : IVisionNodeExecutor
{
    private static readonly BarcodeFormat[] OneDimensionalFormats =
    [
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93,
        BarcodeFormat.EAN_8, BarcodeFormat.EAN_13, BarcodeFormat.UPC_A,
        BarcodeFormat.UPC_E, BarcodeFormat.ITF, BarcodeFormat.CODABAR
    ];

    // Industrial 2D symbologies (HALCON find_data_code_2d / Cognex DataMan staples).
    // Previously only QR was offered, so ECC200 DataMatrix parts could not be read at all.
    private static readonly BarcodeFormat[] TwoDimensionalFormats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX,
        BarcodeFormat.PDF_417, BarcodeFormat.AZTEC
    ];

    public string Type => "recognition.barcode";

    public ValueTask<NodeExecutorResult> ExecuteAsync(NodeExecutionContext context, NodeDefinition node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        using var gray = RecognitionImageHelpers.ToGray8(source);
        var mode = node.GetString("mode", "all").Trim().ToLowerInvariant();
        if (mode is not ("all" or "qr" or "1d" or "2d"))
            throw new InvalidOperationException($"Unsupported barcode mode '{mode}'. Supported modes are 'all', 'qr', '1d', and '2d'.");
        var tryHarder = node.GetBool("tryHarder", true);
        var multiple = node.GetBool("multiple", true);
        var formats = mode switch
        {
            "qr" => [BarcodeFormat.QR_CODE],
            "1d" => OneDimensionalFormats,
            "2d" => TwoDimensionalFormats,
            "all" => [.. OneDimensionalFormats, .. TwoDimensionalFormats],
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
            .Where(x => formats.Contains(x.BarcodeFormat))
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
        // Always expose the first result: with multiple=true the optional
        // text/format ports previously vanished, breaking downstream text.match.
        if (results.Length >= 1)
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

/// <summary>
/// Runs Tesseract 5 against the configured tessdata language model with OCRMax-style
/// controls: page-segmentation/engine modes, character whitelist, DPI hint, upscale and
/// binarization preprocessing, optional rectangular ROI, per-word boxes/confidences and
/// a minimum-confidence filter. Invalid configuration fails deterministically.
/// </summary>
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
        var psmName = node.GetString("psm", "auto").Trim().ToLowerInvariant();
        var psm = psmName switch
        {
            "auto" => PageSegMode.Auto, "autoosd" => PageSegMode.AutoOsd, "singleblock" => PageSegMode.SingleBlock,
            "singleline" => PageSegMode.SingleLine, "singleword" => PageSegMode.SingleWord,
            "singlechar" => PageSegMode.SingleChar, "sparsetext" => PageSegMode.SparseText,
            _ => throw new InvalidOperationException($"Unsupported OCR psm '{psmName}'. Supported: auto, autoOsd, singleBlock, singleLine, singleWord, singleChar, sparseText.")
        };
        var oemName = node.GetString("oem", "default").Trim().ToLowerInvariant();
        var oem = oemName switch
        {
            "default" => EngineMode.Default, "lstm" => EngineMode.LstmOnly, "legacy" => EngineMode.TesseractOnly,
            "combined" => EngineMode.TesseractAndLstm,
            _ => throw new InvalidOperationException($"Unsupported OCR oem '{oemName}'. Supported: default, lstm, legacy, combined.")
        };
        var whitelist = OcrTextParsing.ValidateWhitelist(node.GetString("whitelist", string.Empty));
        var blacklist = OcrTextParsing.ValidateBlacklist(node.GetString("blacklist", string.Empty));
        OcrTextParsing.EnsureNoWhitelistBlacklistConflict(whitelist, blacklist);
        var dpi = node.GetInt("dpi", 0);
        if (dpi is not 0 && dpi is < 70 or > 1200) throw new InvalidOperationException("OCR dpi must be 0 (auto) or between 70 and 1200.");
        var scale = node.GetDouble("upscale", 1);
        if (!double.IsFinite(scale) || scale is < 0.5 or > 4) throw new InvalidOperationException("OCR upscale must be between 0.5 and 4.");
        var binarize = node.GetString("binarize", "none").Trim().ToLowerInvariant();
        if (binarize is not ("none" or "otsu" or "adaptive")) throw new InvalidOperationException("OCR binarize must be none, otsu, or adaptive.");
        var invert = node.GetBool("invert", false);
        var minConfidence = node.GetDouble("minConfidence", 0);
        if (!double.IsFinite(minConfidence) || minConfidence is < 0 or > 1) throw new InvalidOperationException("OCR minConfidence must be between 0 and 1.");

        var resolvedDataPath = Path.GetFullPath(dataPath);
        if (!Directory.Exists(resolvedDataPath))
            throw new InvalidOperationException($"OCR tessdata directory '{resolvedDataPath}' does not exist.");
        foreach (var code in languageCodes)
        {
            var trainedData = Path.Combine(resolvedDataPath, $"{code}.traineddata");
            if (!File.Exists(trainedData))
                throw new InvalidOperationException($"OCR language model '{trainedData}' is missing; configure the required .traineddata file.");
        }

        // Optional rectangular ROI only: cropping other ROI shapes would silently change
        // the text layout, so reject them instead of guessing.
        var offsetX = 0d; var offsetY = 0d;
        Mat? cropped = null;
        var workSource = source;
        var roi = node.GetRoi();
        if (roi is not null)
        {
            if (roi.Type != VisionRoiType.Rectangle || roi.X is null || roi.Y is null || roi.Width is null || roi.Height is null)
                throw new InvalidOperationException("OCR ROI must be a rectangle with X, Y, Width and Height.");
            var x = Math.Clamp((int)Math.Floor(roi.X.Value), 0, Math.Max(0, source.Width - 1));
            var y = Math.Clamp((int)Math.Floor(roi.Y.Value), 0, Math.Max(0, source.Height - 1));
            var width = Math.Clamp((int)Math.Ceiling(roi.Width.Value), 1, source.Width - x);
            var height = Math.Clamp((int)Math.Ceiling(roi.Height.Value), 1, source.Height - y);
            cropped = new Mat(source, new OpenCvSharp.Rect(x, y, width, height)).Clone();
            workSource = cropped; offsetX = x; offsetY = y;
        }

        try
        {
            using var gray = RecognitionImageHelpers.ToGray8(workSource);
            using var processed = Preprocess(gray, scale, binarize, invert);
            Cv2.ImEncode(".png", processed, out var encoded);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var engine = new TesseractOCR.Engine(resolvedDataPath, language, oem);
                if (whitelist.Length > 0) engine.SetVariable("tessedit_char_whitelist", whitelist);
                if (blacklist.Length > 0) engine.SetVariable("tessedit_char_blacklist", blacklist);
                if (dpi > 0) engine.SetVariable("user_defined_dpi", dpi);
                using var image = TesseractPixImage.LoadFromMemory(encoded);
                using var page = engine.Process(image, psm);
                cancellationToken.ThrowIfCancellationRequested();
                var allWords = OcrTextParsing.ParseTsvWords(page.TsvText, scale, offsetX, offsetY);
                // Per-character boxes come from Tesseract box output (bottom-origin);
                // empty when the engine/model produces none - reported, never invented.
                OcrTextParsing.OcrChar[] chars = [];
                try { chars = OcrTextParsing.ParseBoxChars(page.BoxText, scale, offsetX, offsetY, (int)Math.Round(workSource.Height * scale)); }
                catch (Exception) { chars = []; }
                var words = minConfidence > 0 ? allWords.Where(w => w.Confidence >= minConfidence).ToArray() : allWords;
                var text = minConfidence > 0 ? string.Join(" ", words.Select(w => w.Text)) : page.Text.Trim();
                var confidence = words.Length > 0
                    ? Math.Clamp(words.Average(w => w.Confidence), 0, 1)
                    : Math.Clamp(double.IsFinite(page.MeanConfidence) ? page.MeanConfidence : 0, 0, 1);
                var wordsJson = JsonSerializer.Serialize(words.Select(w => new { text = w.Text, confidence = w.Confidence, x = w.X, y = w.Y, width = w.Width, height = w.Height }));
                return ValueTask.FromResult(new NodeExecutorResult(
                    new Dictionary<string, VisionValue>
                    {
                        ["text"] = VisionValue.String(text),
                        ["confidence"] = VisionValue.Double(confidence),
                        ["wordCount"] = VisionValue.Integer(words.Length),
                        ["words"] = VisionValue.String(wordsJson),
                        ["chars"] = VisionValue.String(JsonSerializer.Serialize(chars.Select(ch => new { text = ch.Text, x = ch.X, y = ch.Y, width = ch.Width, height = ch.Height }))),
                        ["result"] = VisionValue.String(JsonSerializer.Serialize(new { text, confidence, language, psm = psmName, oem = oemName, wordCount = words.Length, words }))
                    },
                    new Dictionary<string, object?> { ["language"] = language, ["psm"] = psmName, ["oem"] = oemName, ["confidence"] = confidence, ["characters"] = text.Length, ["wordCount"] = words.Length, ["charCount"] = chars.Length, ["lowConfidenceWordCount"] = allWords.Length - words.Length, ["blacklist"] = blacklist.Length > 0 }));
            }
            catch (Exception ex) when (ex is not (InvalidOperationException or OperationCanceledException))
            {
                throw new InvalidOperationException($"OCR failed using language '{language}' and tessdata directory '{resolvedDataPath}': {ex.Message}", ex);
            }
            finally
            {
                System.Array.Clear(encoded);
            }
        }
        finally { cropped?.Dispose(); }
    }

    private static Mat Preprocess(Mat gray, double scale, string binarize, bool invert)
    {
        Mat current = gray.Clone();
        try
        {
            if (Math.Abs(scale - 1) > 1e-9)
            {
                var resized = new Mat();
                JLVisionOperators.Resize(current, resized, new Size(Math.Max(1, (int)Math.Round(current.Width * scale)), Math.Max(1, (int)Math.Round(current.Height * scale))), interpolation: InterpolationFlags.Cubic);
                current.Dispose(); current = resized;
            }
            if (binarize != "none")
            {
                var binary = new Mat();
                if (binarize == "otsu") JLVisionOperators.Threshold(current, binary, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                else JLVisionOperators.AdaptiveThreshold(current, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 31, 10);
                current.Dispose(); current = binary;
            }
            if (invert)
            {
                var inverted = new Mat();
                JLVisionOperators.BitwiseNot(current, inverted);
                current.Dispose(); current = inverted;
            }
            return current;
        }
        catch { current.Dispose(); throw; }
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
