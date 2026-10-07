using OpenCvSharp;

namespace VisionStudio.Engine;

public static class VisionRoiOpenCvExtensions
{
    public static Mat BuildMask(this VisionRoi roi, Size size)
    {
        var mask = new Mat(size.Height, size.Width, MatType.CV_8UC1, Scalar.All(0));
        switch (roi.Type)
        {
            case VisionRoiType.Rectangle:
            {
                var x = ClampToInt(roi.X ?? 0, 0, Math.Max(0, size.Width - 1));
                var y = ClampToInt(roi.Y ?? 0, 0, Math.Max(0, size.Height - 1));
                var w = ClampToInt(roi.Width ?? 0, 0, size.Width - x);
                var h = ClampToInt(roi.Height ?? 0, 0, size.Height - y);
                if (w > 0 && h > 0)
                    Cv2.Rectangle(mask, new Rect(x, y, w, h), Scalar.All(255), -1);
                break;
            }
            case VisionRoiType.Circle:
            {
                var x = ClampToInt(roi.X ?? 0, 0, Math.Max(0, size.Width - 1));
                var y = ClampToInt(roi.Y ?? 0, 0, Math.Max(0, size.Height - 1));
                var radius = Math.Max(0, ClampToInt(roi.Radius ?? 0, 0, Math.Max(size.Width, size.Height)));
                if (radius > 0)
                    Cv2.Circle(mask, new Point(x, y), radius, Scalar.All(255), -1, LineTypes.AntiAlias);
                break;
            }
            case VisionRoiType.Polygon:
            {
                var points = (roi.Points ?? [])
                    .Select(p => new Point(
                        ClampToInt(p.X, 0, Math.Max(0, size.Width - 1)),
                        ClampToInt(p.Y, 0, Math.Max(0, size.Height - 1))))
                    .ToArray();
                if (points.Length >= 3)
                    Cv2.FillPoly(mask, new[] { points }, Scalar.All(255), LineTypes.AntiAlias);
                break;
            }
        }
        return mask;
    }

    private static int ClampToInt(double value, int min, int max) =>
        Math.Clamp((int)Math.Round(value), min, Math.Max(min, max));
}
