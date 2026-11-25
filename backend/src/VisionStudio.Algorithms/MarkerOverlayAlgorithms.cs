using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class MarkerOverlayAlgorithms
{
    internal static Mat Aruco(Mat gray, Point2f[][] corners, int[] ids)
    {
        var overlay = new Mat();
        try
        {
            JLVisionOperators.CvtColor(gray, overlay, ColorConversionCodes.GRAY2BGR);
            for (var index = 0; index < ids.Length; index++)
            {
                var points = corners[index]
                    .Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                    .ToArray();
                Cv2.Polylines(overlay, new[] { points }, true, Scalar.LimeGreen, 2);
                var labelPoint = new Point(points.Min(point => point.X), Math.Max(0, points.Min(point => point.Y) - 6));
                Cv2.PutText(overlay, $"ID {ids[index]}", labelPoint, HersheyFonts.HersheySimplex, 0.6, Scalar.LimeGreen, 2);
            }
            return overlay;
        }
        catch { overlay.Dispose(); throw; }
    }

    internal static Mat Charuco(Mat gray, Point2f[] charucoCorners, int[] charucoIds, Point2f[][] markerCorners, int[] markerIds)
    {
        var overlay = new Mat();
        try
        {
            JLVisionOperators.CvtColor(gray, overlay, ColorConversionCodes.GRAY2BGR);
            for (var index = 0; index < markerIds.Length && index < markerCorners.Length; index++)
            {
                var points = markerCorners[index]
                    .Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                    .ToArray();
                Cv2.Polylines(overlay, new[] { points }, true, Scalar.LimeGreen, 2);
                Cv2.PutText(overlay, $"M{markerIds[index]}", points[0], HersheyFonts.HersheySimplex, 0.5, Scalar.LimeGreen, 1);
            }
            for (var index = 0; index < charucoCorners.Length; index++)
            {
                var point = new Point((int)Math.Round(charucoCorners[index].X), (int)Math.Round(charucoCorners[index].Y));
                Cv2.Circle(overlay, point, 3, Scalar.Yellow, -1);
                Cv2.PutText(overlay, charucoIds.Length > index ? charucoIds[index].ToString() : string.Empty,
                    new Point(point.X + 4, point.Y - 4), HersheyFonts.HersheySimplex, 0.4, Scalar.Yellow, 1);
            }
            return overlay;
        }
        catch { overlay.Dispose(); throw; }
    }
}
