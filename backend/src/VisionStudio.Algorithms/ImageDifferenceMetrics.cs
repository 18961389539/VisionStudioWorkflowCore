using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Algorithms;

/// <summary>Pure image-difference metrics (global SSIM) and mask-to-region conversion.
/// Managed-only so the maths and run encoding are unit-testable without natives.
/// Global SSIM uses whole-image statistics (one window); it is a similarity summary,
/// not a windowed SSIM map - that distinction is documented in the node catalog.</summary>
public static class ImageDifferenceMetrics
{
    public static double GlobalSsim(byte[] current, byte[] reference)
    {
        if (current.Length != reference.Length || current.Length == 0)
            throw new InvalidOperationException("SSIM requires two non-empty images with identical pixel counts.");
        double meanA = 0, meanB = 0;
        for (var i = 0; i < current.Length; i++) { meanA += current[i]; meanB += reference[i]; }
        meanA /= current.Length; meanB /= reference.Length;
        double varA = 0, varB = 0, cov = 0;
        for (var i = 0; i < current.Length; i++)
        {
            var da = current[i] - meanA; var db = reference[i] - meanB;
            varA += da * da; varB += db * db; cov += da * db;
        }
        varA /= current.Length; varB /= current.Length; cov /= current.Length;
        const double c1 = 6.5025;   // (0.01*255)^2
        const double c2 = 58.5225;  // (0.03*255)^2
        var numerator = (2 * meanA * meanB + c1) * (2 * cov + c2);
        var denominator = (meanA * meanA + meanB * meanB + c1) * (varA + varB + c2);
        return denominator <= 1e-12 ? 1d : Math.Clamp(numerator / denominator, -1d, 1d);
    }

    /// <summary>8-connected components of a binary mask as a VisionRegion (row runs).</summary>
    public static VisionRegion RegionFromMask(byte[] mask, int width, int height)
    {
        if (width < 1 || height < 1 || mask.Length != width * height)
            throw new InvalidOperationException("Difference region mask must match width*height.");
        var visited = new bool[mask.Length];
        var components = new List<VisionRegionComponent>();
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (mask[start] == 0 || visited[start]) continue;
            var pixels = new List<int>();
            stack.Push(start); visited[start] = true;
            while (stack.Count > 0)
            {
                var index = stack.Pop(); pixels.Add(index);
                var x = index % width; var y = index / width;
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var nx = x + dx; var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var next = ny * width + nx;
                    if (mask[next] != 0 && !visited[next]) { visited[next] = true; stack.Push(next); }
                }
            }
            pixels.Sort();
            var runs = new List<VisionRegionRun>();
            foreach (var pixel in pixels)
            {
                var row = pixel / width; var col = pixel % width;
                if (runs.Count > 0 && runs[^1].Row == row && runs[^1].EndColumn == col - 1)
                    runs[^1] = new VisionRegionRun(row, runs[^1].StartColumn, col);
                else runs.Add(new VisionRegionRun(row, col, col));
            }
            components.Add(new VisionRegionComponent(runs));
            if (components.Count > 10000) throw new InvalidOperationException("Difference region component budget exceeded (10000).");
        }
        return new VisionRegion(width, height, components);
    }
}
