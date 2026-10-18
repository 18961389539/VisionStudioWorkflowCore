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
    // Identical payloads in different places are distinct parts (duplicate labels, repeated DPM
    // marks), so results merge on content plus position, not on content alone.
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
        var timer = System.Diagnostics.Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        var source = context.RequireImage("image").Mat;
        using var gray = RecognitionImageHelpers.ToGray8(source);
        var mode = node.GetString("mode", "all").Trim().ToLowerInvariant();
        if (mode is not ("all" or "qr" or "1d" or "2d"))
            throw new InvalidOperationException($"Unsupported barcode mode '{mode}'. Supported modes are 'all', 'qr', '1d', and '2d'.");
        var tryHarder = node.GetBool("tryHarder", true);
        var multiple = node.GetBool("multiple", true);
        var tryInverted = node.GetBool("tryInverted", true);
        var maxResults = node.GetInt("maxResults", 100);
        if (maxResults is < 1 or > 1000) throw new InvalidOperationException("Barcode maxResults must be between 1 and 1000.");
        var dedupRadius = node.GetDouble("dedupRadiusPx", 8);
        if (!double.IsFinite(dedupRadius) || dedupRadius is < 0 or > 512)
            throw new InvalidOperationException("Barcode dedupRadiusPx must be between 0 and 512 pixels.");
        var tileSearch = node.GetString("tileSearch", "auto").Trim().ToLowerInvariant();
        if (tileSearch is not ("auto" or "off"))
            throw new InvalidOperationException("Barcode tileSearch must be auto or off.");
        var preprocess = node.GetString("preprocess", "auto").Trim().ToLowerInvariant();
        if (preprocess is not ("auto" or "none" or "otsu" or "adaptive"))
            throw new InvalidOperationException("Barcode preprocess must be auto, none, otsu, or adaptive.");
        var adaptiveBlockSize = node.GetInt("adaptiveBlockSize", 31);
        if (adaptiveBlockSize is < 3 or > 101 || adaptiveBlockSize % 2 == 0)
            throw new InvalidOperationException("Barcode adaptiveBlockSize must be an odd number between 3 and 101.");
        var adaptiveConstant = node.GetDouble("adaptiveConstant", 10);
        if (!double.IsFinite(adaptiveConstant) || adaptiveConstant is < -100 or > 100)
            throw new InvalidOperationException("Barcode adaptiveConstant must be between -100 and 100.");
        var formats = mode switch
        {
            "qr" => [BarcodeFormat.QR_CODE],
            "1d" => OneDimensionalFormats,
            "2d" => TwoDimensionalFormats,
            "all" => [.. OneDimensionalFormats, .. TwoDimensionalFormats],
            _ => throw new InvalidOperationException($"Unsupported barcode mode '{mode}'.")
        };

        var roi = node.GetRoi();
        using var scanImage = ApplyRoi(gray, roi, cancellationToken);
        var variants = BuildVariants(scanImage, preprocess, adaptiveBlockSize, adaptiveConstant);
        var resultsFound = new List<BarcodeCandidate>();
        var rawCount = 0;
        var attempts = 0;
        var tileAttempts = 0;
        try
        {
            foreach (var variant in variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;
                AddDecoded(Decode(variant.Image, formats, tryHarder, multiple, cancellationToken), variant.Name, false);
                if (resultsFound.Count > maxResults || !multiple && resultsFound.Count > 0) break;
            }

            // Search opposite polarity only when it can add value. A first successful
            // normal-polarity read is enough for single-result mode; multi mode still
            // checks inverted pixels because a frame can contain mixed code polarity.
            if (tryInverted && (multiple || resultsFound.Count == 0))
            {
                foreach (var variant in variants)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var inverted = new Mat();
                    JLVisionOperators.BitwiseNot(variant.Image, inverted);
                    attempts++;
                    AddDecoded(Decode(inverted, formats, tryHarder, multiple, cancellationToken), variant.Name, true);
                    if (resultsFound.Count > maxResults || !multiple && resultsFound.Count > 0) break;
                }
            }

            // ZXing's DataMatrix multi-detector returns nothing as soon as a frame holds more
            // than one symbol (verified: one DataMatrix decodes, two identical ones yield zero
            // results), which silently turned "multiple" into a single-symbol reader for DPM
            // parts. Retry once on overlapping half-frame tiles with single-symbol decoding:
            // each tile sees one symbol, and position-based deduplication removes the overlap.
            if (multiple && resultsFound.Count == 0 && tileSearch == "auto" && formats.Any(TwoDimensionalFormats.Contains))
            {
                foreach (var tile in BuildRecoveryTiles(variants[0].Image.Width, variants[0].Image.Height))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var view = new Mat(variants[0].Image, tile);
                    tileAttempts++;
                    AddDecoded(Decode(view, formats, tryHarder, false, cancellationToken), variants[0].Name, false, tile.X, tile.Y);
                    if (resultsFound.Count > maxResults) break;
                }
            }
        }
        finally
        {
            foreach (var variant in variants) variant.Image.Dispose();
        }

        var truncated = resultsFound.Count > maxResults;
        var results = resultsFound.Take(maxResults).Select(candidate => candidate.Result).ToArray();
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
        var overlays = results.Take(30).SelectMany((result, index) => result.Points.Take(8).Select((point, pointIndex) =>
            new VisionOverlay($"barcode-{index}-point-{pointIndex}", VisionOverlayType.Point,
                X: point.X, Y: point.Y, Label: pointIndex == 0 ? $"{result.Format}: {result.Text}" : null,
                Stroke: "#13c2c2", StrokeWidth: 3))).ToArray();
        timer.Stop();
        return ValueTask.FromResult(new NodeExecutorResult(outputs, new Dictionary<string, object?>
        {
            ["mode"] = mode,
            ["tryHarder"] = tryHarder,
            ["multiple"] = multiple,
            ["tryInverted"] = tryInverted,
            ["preprocess"] = preprocess,
            ["roiApplied"] = roi is not null,
            ["attempts"] = attempts,
            ["count"] = results.Length,
            ["rawCount"] = rawCount,
            ["duplicatesMerged"] = rawCount - resultsFound.Count,
            ["dedupRadiusPx"] = dedupRadius,
            ["tileSearch"] = tileSearch,
            ["tileAttempts"] = tileAttempts,
            ["truncated"] = truncated,
            ["elapsedMs"] = timer.Elapsed.TotalMilliseconds,
            ["winningPass"] = results.FirstOrDefault()?.Preprocessing,
            ["inverted"] = results.FirstOrDefault()?.Inverted
        }, overlays));

        void AddDecoded(IEnumerable<ZXing.Result> decoded, string variant, bool inverted, int offsetX = 0, int offsetY = 0)
        {
            foreach (var result in decoded)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!formats.Contains(result.BarcodeFormat)) continue;
                rawCount++;
                var points = result.ResultPoints?.Select(p => new BarcodePoint(p.X + offsetX, p.Y + offsetY)).ToArray() ?? [];
                var format = result.BarcodeFormat.ToString();
                // Deduplicate by position, never by content alone: two physical symbols that carry
                // the same payload (duplicate labels, repeated DPM marks) must still be counted
                // twice, while the same symbol re-read from another preprocessing variant or from
                // inverted pixels lands within a few pixels of the first hit.
                if (points.Length > 0)
                {
                    var centerX = points.Average(p => p.X);
                    var centerY = points.Average(p => p.Y);
                    var duplicate = resultsFound.Any(existing =>
                        existing.Result.Format == format
                        && existing.Result.Text == result.Text
                        && (!existing.HasGeometry
                            || MeasurementMath.Hypot(existing.CenterX - centerX, existing.CenterY - centerY) <= dedupRadius));
                    if (!duplicate)
                        resultsFound.Add(new BarcodeCandidate(new BarcodeResult(result.Text, format, points, variant, inverted), centerX, centerY, true));
                }
                else if (!resultsFound.Any(existing => existing.Result.Format == format && existing.Result.Text == result.Text))
                {
                    // A decoder without result points cannot be located; fall back to content so
                    // repeated variant passes do not inflate the count.
                    resultsFound.Add(new BarcodeCandidate(new BarcodeResult(result.Text, format, [], variant, inverted), double.NaN, double.NaN, false));
                }
                if (resultsFound.Count > maxResults) return;
            }
        }
    }

    private static IEnumerable<ZXing.Result> Decode(Mat image, BarcodeFormat[] formats, bool tryHarder, bool multiple, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        image.GetArray(out byte[] pixels);
        var luminance = new RGBLuminanceSource(pixels, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.Gray8);
        var reader = new BarcodeReaderGeneric { AutoRotate = true };
        reader.Options.PossibleFormats = formats.ToList();
        reader.Options.TryHarder = tryHarder;
        var decoded = multiple ? reader.DecodeMultiple(luminance) : Wrap(reader.Decode(luminance));
        token.ThrowIfCancellationRequested();
        return decoded ?? System.Array.Empty<ZXing.Result>();
    }

    private static List<(string Name, Mat Image)> BuildVariants(Mat source, string preprocess, int adaptiveBlockSize, double adaptiveConstant)
    {
        var variants = new List<(string Name, Mat Image)>();
        try
        {
            if (preprocess is "auto" or "none") variants.Add(("original", source.Clone()));
            if (preprocess is "auto" or "otsu")
            {
                var otsu = new Mat();
                try { JLVisionOperators.Threshold(source, otsu, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu); variants.Add(("otsu", otsu)); }
                catch { otsu.Dispose(); throw; }
            }
            if (preprocess is "auto" or "adaptive")
            {
                // OpenCV requires the local window to fit the image. Shrink it for small
                // symbols while retaining an odd window size of at least three pixels.
                var largestOdd = Math.Min(source.Width, source.Height);
                if (largestOdd % 2 == 0) largestOdd--;
                var blockSize = Math.Min(adaptiveBlockSize, largestOdd);
                if (blockSize >= 3)
                {
                    var adaptive = new Mat();
                    try
                    {
                        JLVisionOperators.AdaptiveThreshold(source, adaptive, 255, AdaptiveThresholdTypes.MeanC,
                            ThresholdTypes.Binary, blockSize, adaptiveConstant);
                        variants.Add(("adaptive", adaptive));
                    }
                    catch { adaptive.Dispose(); throw; }
                }
            }
            if (variants.Count == 0) throw new InvalidOperationException("Barcode preprocessing could not produce a usable image; image dimensions must be at least 3x3 for adaptive thresholding.");
            return variants;
        }
        catch { foreach (var variant in variants) variant.Image.Dispose(); throw; }
    }

    /// <summary>
    /// Overlapping tiles for multi-symbol recovery. Each tile spans 60% of one axis and the full
    /// other axis, so any symbol up to 60% of the frame fits inside at least one tile, while the
    /// overlap lets position-based deduplication discard double reads.
    /// </summary>
    private static IEnumerable<OpenCvSharp.Rect> BuildRecoveryTiles(int width, int height)
    {
        const double span = 0.6;
        var tileWidth = Math.Max(1, (int)Math.Ceiling(width * span));
        var tileHeight = Math.Max(1, (int)Math.Ceiling(height * span));
        var tiles = new List<OpenCvSharp.Rect>(4);
        if (tileWidth < width)
        {
            tiles.Add(new OpenCvSharp.Rect(0, 0, tileWidth, height));
            tiles.Add(new OpenCvSharp.Rect(width - tileWidth, 0, tileWidth, height));
        }
        if (tileHeight < height)
        {
            tiles.Add(new OpenCvSharp.Rect(0, 0, width, tileHeight));
            tiles.Add(new OpenCvSharp.Rect(0, height - tileHeight, width, tileHeight));
        }
        return tiles.Where(tile => tile.Width >= 16 && tile.Height >= 16).Distinct();
    }

    private static Mat ApplyRoi(Mat source, VisionRoi? roi, CancellationToken token)
    {
        if (roi is null) return source.Clone();
        roi.ValidateForMeasurement(token);
        using var mask = roi.BuildMask(source.Size(), token);
        if (JLVisionOperators.CountNonZero(mask) == 0)
            throw new InvalidOperationException("Barcode ROI does not overlap the input image.");
        var result = new Mat(source.Size(), source.Type(), Scalar.All(255));
        try { source.CopyTo(result, mask); return result; }
        catch { result.Dispose(); throw; }
    }

    private static ZXing.Result[]? Wrap(ZXing.Result? result) => result is null ? null : [result];
    private sealed record BarcodePoint(
        [property: System.Text.Json.Serialization.JsonPropertyName("x")] float X,
        [property: System.Text.Json.Serialization.JsonPropertyName("y")] float Y);
    private sealed record BarcodeResult(string Text, string Format, BarcodePoint[] Points, string Preprocessing, bool Inverted);
    private sealed record BarcodeCandidate(BarcodeResult Result, double CenterX, double CenterY, bool HasGeometry);
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

        var engineLease = OcrEnginePool.Rent(resolvedDataPath, language, oem);
        try
        {
            using var gray = RecognitionImageHelpers.ToGray8(workSource);
            using var processed = Preprocess(gray, scale, binarize, invert);
            Cv2.ImEncode(".png", processed, out var encoded);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var engine = engineLease.Engine;
                OcrEnginePool.ResetForRequest(engine, whitelist, blacklist, dpi);
                using var image = TesseractPixImage.LoadFromMemory(encoded);
                using var page = engine.Process(image, psm);
                cancellationToken.ThrowIfCancellationRequested();
                var allWords = OcrTextParsing.ParseTsvWords(page.TsvText, scale, offsetX, offsetY);
                // Per-character boxes and confidences come from the page layout iterator; Tesseract's
                // box output has no confidence and a bottom-left origin, so it stays as a fallback
                // for engines that expose no symbols. Empty is reported, never invented.
                OcrTextParsing.OcrChar[] chars = [];
                try { chars = ReadLayoutChars(page, scale, offsetX, offsetY); }
                catch (Exception) { chars = []; }
                if (chars.Length == 0)
                {
                    try { chars = OcrTextParsing.ParseBoxChars(page.BoxText, scale, offsetX, offsetY, (int)Math.Round(workSource.Height * scale)); }
                    catch (Exception) { chars = []; }
                }
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
                        ["chars"] = VisionValue.String(JsonSerializer.Serialize(chars.Select(ch => new { text = ch.Text, confidence = ch.Confidence, x = ch.X, y = ch.Y, width = ch.Width, height = ch.Height }))),
                        ["result"] = VisionValue.String(JsonSerializer.Serialize(new { text, confidence, language, psm = psmName, oem = oemName, wordCount = words.Length, words }))
                    },
                    new Dictionary<string, object?> { ["language"] = language, ["psm"] = psmName, ["oem"] = oemName, ["confidence"] = confidence, ["characters"] = text.Length, ["wordCount"] = words.Length, ["charCount"] = chars.Length, ["lowConfidenceWordCount"] = allWords.Length - words.Length, ["blacklist"] = blacklist.Length > 0, ["engineReused"] = engineLease.Reused }));
            }
            catch (Exception ex) when (ex is not (InvalidOperationException or OperationCanceledException))
            {
                engineLease.Discard();
                throw new InvalidOperationException($"OCR failed using language '{language}' and tessdata directory '{resolvedDataPath}': {ex.Message}", ex);
            }
            finally
            {
                System.Array.Clear(encoded);
            }
        }
        finally
        {
            engineLease.Dispose();
            cropped?.Dispose();
        }
    }

    /// <summary>
    /// Walks blocks → paragraphs → lines → words → symbols to collect per-character boxes with
    /// their own confidence. Tesseract reports 0-100 confidences and top-left origin boxes in
    /// processed-image pixels, so they map back by scale and ROI offset without a Y flip.
    /// </summary>
    private static OcrTextParsing.OcrChar[] ReadLayoutChars(TesseractOCR.Page page, double scale, double offsetX, double offsetY)
    {
        var chars = new List<OcrTextParsing.OcrChar>();
        foreach (var block in page.Layout)
        foreach (var paragraph in block.Paragraphs)
        foreach (var line in paragraph.TextLines)
        foreach (var word in line.Words)
        foreach (var symbol in word.Symbols)
        {
            var text = symbol.Text;
            if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(text)) continue;
            var box = symbol.BoundingBox;
            if (box is null) continue;
            chars.Add(new OcrTextParsing.OcrChar(text,
                box.Value.X1 / scale + offsetX,
                box.Value.Y1 / scale + offsetY,
                box.Value.Width / scale,
                box.Value.Height / scale,
                Math.Clamp(symbol.Confidence / 100d, 0, 1)));
        }
        return chars.ToArray();
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
