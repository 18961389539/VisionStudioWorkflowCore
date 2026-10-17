using System.Globalization;

namespace VisionStudio.Engine.Algorithms;

/// <summary>Pure OCR helpers: Tesseract TSV word extraction and whitelist validation.
/// Coordinates are converted from processed-image pixels back to source pixels.</summary>
public static class OcrTextParsing
{
    public sealed record OcrWord(string Text, double Confidence, double X, double Y, double Width, double Height);

    public static string ValidateWhitelist(string whitelist)
    {
        if (whitelist.Length == 0) return string.Empty;
        if (whitelist.Any(char.IsControl)) throw new InvalidOperationException("OCR whitelist must not contain control characters.");
        if (whitelist.Length > 256) throw new InvalidOperationException("OCR whitelist must be at most 256 characters.");
        return whitelist;
    }

    /// <summary>Character blacklist validation (mirror of the whitelist rules). A
    /// character cannot be both allowed and forbidden; that conflict is rejected by
    /// the caller so it fails before the engine starts.</summary>
    public static string ValidateBlacklist(string blacklist)
    {
        if (blacklist.Length == 0) return string.Empty;
        if (blacklist.Any(char.IsControl)) throw new InvalidOperationException("OCR blacklist must not contain control characters.");
        if (blacklist.Length > 256) throw new InvalidOperationException("OCR blacklist must be at most 256 characters.");
        return blacklist;
    }

    public static void EnsureNoWhitelistBlacklistConflict(string whitelist, string blacklist)
    {
        var conflict = whitelist.Intersect(blacklist).FirstOrDefault();
        if (conflict != default) throw new InvalidOperationException($"OCR character '{conflict}' cannot be in both whitelist and blacklist.");
    }

    public sealed record OcrChar(string Text, double X, double Y, double Width, double Height);

    /// <summary>
    /// Parse Tesseract box output (one line per character: char left bottom right top page,
    /// bottom-left origin) into source-pixel boxes. Malformed lines are skipped, never guessed.
    /// </summary>
    /// <param name="processedImageHeight">Height of the (possibly upscaled) image the box
    /// coordinates refer to; the Y axis is flipped from bottom-origin to top-origin there
    /// before dividing by <paramref name="scale"/>.</param>
    public static OcrChar[] ParseBoxChars(string? boxText, double scale, double offsetX, double offsetY, int processedImageHeight)
    {
        if (string.IsNullOrWhiteSpace(boxText) || !(scale > 0)) return [];
        var chars = new List<OcrChar>();
        foreach (var line in boxText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var bottom) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var right) ||
                !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var top)) continue;
            var y = (processedImageHeight - top) / scale + offsetY;
            chars.Add(new(parts[0], left / scale + offsetX, y, (right - left) / scale, (top - bottom) / scale));
        }
        return chars.ToArray();
    }

    public static OcrWord[] ParseTsvWords(string? tsv, double scale, double offsetX, double offsetY)
    {
        if (string.IsNullOrWhiteSpace(tsv) || !(scale > 0) || !double.IsFinite(scale)) return [];
        var words = new List<OcrWord>();
        var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < lines.Length; i++)
        {
            var columns = lines[i].TrimEnd('\r').Split('\t');
            if (columns.Length < 12) continue;
            if (!int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) || level != 5) continue;
            var text = columns[11];
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!double.TryParse(columns[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence) || confidence < 0) continue;
            if (!double.TryParse(columns[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
                !double.TryParse(columns[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var top) ||
                !double.TryParse(columns[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
                !double.TryParse(columns[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)) continue;
            words.Add(new(text, Math.Clamp(confidence / 100d, 0, 1), left / scale + offsetX, top / scale + offsetY, width / scale, height / scale));
        }
        return words.ToArray();
    }
}
