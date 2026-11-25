using OpenCvSharp;
using OpenCvSharp.XImgProc;

namespace VisionStudio.Engine.Algorithms;

internal static class SuperpixelAlgorithms
{
    internal sealed record Result(Mat Labels, Mat Boundary);

    internal static Result Segment(Mat source, int regionSize, double ruler, int iterations, bool enforceConnectivity)
    {
        using var bgr = source.Channels() == 1 ? new Mat() : source.Clone();
        if (source.Channels() == 1) Cv2.CvtColor(source, bgr, ColorConversionCodes.GRAY2BGR);
        else if (source.Channels() == 4) Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        using var slic = SuperpixelSLIC.Create(bgr, SLICType.SLICO, regionSize, (float)ruler);
        slic.Iterate(iterations);
        if (enforceConnectivity) slic.EnforceLabelConnectivity(regionSize);

        var labels = new Mat();
        var boundary = new Mat();
        try
        {
            slic.GetLabels(labels);
            slic.GetLabelContourMask(boundary, true);
            return new Result(labels, boundary);
        }
        catch
        {
            labels.Dispose();
            boundary.Dispose();
            throw;
        }
    }
}
