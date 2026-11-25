using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class RegionRasterAlgorithms
{
    internal static Mat FromContours(
        IReadOnlyList<VisionContour> contours, int width, int height, int expand, bool invert,
        CancellationToken cancellationToken)
    {
        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        try
        {
            foreach (var contour in contours)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var points = contour.Points.Select(point =>
                {
                    ToleranceGeometry.Finite(point.X, point.Y);
                    return new Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
                }).ToArray();
                if (points.Length >= 3) Cv2.FillPoly(mask, [points], Scalar.All(255), LineTypes.Link8);
            }
            if (expand > 0)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(2 * expand + 1, 2 * expand + 1));
                Cv2.Dilate(mask, mask, kernel);
            }
            if (invert) Cv2.BitwiseNot(mask, mask);
            cancellationToken.ThrowIfCancellationRequested();
            return mask;
        }
        catch
        {
            mask.Dispose();
            throw;
        }
    }
}
