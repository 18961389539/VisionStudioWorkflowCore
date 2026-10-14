using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class JLVisionRegions
{
    public static JlRegion ToNative(VisionRegion region)
    {
        JlRegion? result = null;
        try
        {
            foreach (var component in region.Components)
            {
                using var rows = new JlTuple(component.Runs.Select(r => r.Row).ToArray());
                using var starts = new JlTuple(component.Runs.Select(r => r.StartColumn).ToArray());
                using var ends = new JlTuple(component.Runs.Select(r => r.EndColumn).ToArray());
                var item = new JlRegion();
                try { item.GenRegionRuns(rows, starts, ends); }
                catch { item.Dispose(); throw; }
                if (result is null) result = item;
                else { using (item) { var joined = result.ConcatObj(item); result.Dispose(); result = joined; } }
            }
            if (result is null) { result = new JlRegion(); result.GenEmptyRegion(); }
            return result;
        }
        catch { result?.Dispose(); throw; }
    }

    public static VisionRegion FromNative(JlRegion native, int width, int height, CancellationToken token)
    {
        var components = new List<VisionRegionComponent>();
        var count = native.CountObj();
        if (count > 10000) throw new InvalidOperationException("Region component budget exceeded (10000).");
        var runCount = 0;
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            using var item = native.SelectObj(index);
            item.GetRegionRuns(out var rows, out var starts, out var ends);
            using (rows) using (starts) using (ends)
            {
                runCount = checked(runCount + rows.Length);
                if (runCount > VisionRegion.MaxRuns) throw new InvalidOperationException("Region run budget exceeded (1000000).");
                if (rows.Length == 0) continue;
                var runs = new VisionRegionRun[rows.Length];
                for (var i = 0; i < runs.Length; i++) runs[i] = new(rows[i].I, starts[i].I, ends[i].I);
                components.Add(new(runs));
            }
        }
        return new(width, height, components);
    }

    public static Mat Mask(VisionRegion region)
    {
        if (region.Components.Count == 0) return new Mat(region.Height, region.Width, MatType.CV_8UC1, Scalar.All(0));
        using var native = ToNative(region);
        using var union = native.Union1();
        using var image = union.RegionToBin(255, 0, region.Width, region.Height);
        return JLVisionImages.ToMat(image);
    }

    public static long PixelArea(VisionRegion region)
    {
        if (region.Components.Count == 0) return 0;
        using var native = ToNative(region);
        using var union = native.Union1();
        return union.AreaCenter(out double _, out double _);
    }
}
