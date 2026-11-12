using JLVisionLib;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Algorithms;

// 2026-11-12 从单文件 JLVisionMatching.cs（2809 行）按域拆出，纯移动、未改方法体。
// 候选打分：训练对比度参考、候选分数、重叠度、SSD，以及 JLVisionLib 算子参数的薄包装。
internal static partial class JLVisionMatching
{
    private static double Overlap(VisionRectangle2D a, VisionRectangle2D b)
    {
        var intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) * Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / Math.Max(1e-9, a.Width * a.Height + b.Width * b.Height - intersection);
    }

    private static Mat DecodeGrayscaleImage(string encodedImage, string label)
    {
        if (string.IsNullOrWhiteSpace(encodedImage))
            throw new InvalidOperationException("Contrast fraction needs the lossless original training image. Reopen the shape node, fetch its training image, and retrain it before using this threshold.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encodedImage); }
        catch (FormatException ex) { throw new InvalidOperationException($"The {label} is invalid.", ex); }
        using var decoded = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
        if (decoded.Empty()) throw new InvalidOperationException($"The {label} could not be decoded.");
        var gray = new Mat();
        try
        {
            if (decoded.Channels() == 1) decoded.CopyTo(gray);
            else if (decoded.Channels() == 3) Cv2.CvtColor(decoded, gray, ColorConversionCodes.BGR2GRAY);
            else if (decoded.Channels() == 4) Cv2.CvtColor(decoded, gray, ColorConversionCodes.BGRA2GRAY);
            else throw new InvalidOperationException($"The {label} must have 1, 3, or 4 channels.");
            return gray;
        }
        catch { gray.Dispose(); throw; }
    }

    private static ShapeContrastReference CreateContrastReference(JlShapeModel model, Mat? trainingImage,
        double trainingOriginX, double trainingOriginY)
    {
        using var contours = model.GetShapeModelContours(1);
        var points = ExtractShapeContours(contours).SelectMany(contour => contour).ToArray();
        if (points.Length == 0) throw new InvalidOperationException("The shape model contains no contour points for contrast measurement.");
        var stride = Math.Max(1, (int)Math.Ceiling(points.Length / 1024d));
        var samples = points.Where((_, index) => index % stride == 0).ToArray();
        var trainingMean = trainingImage is null ? 0 : MeanPointContrast(trainingImage, samples,
            point => (point.X + trainingOriginX, point.Y + trainingOriginY));
        if (trainingImage is not null && trainingMean <= 1e-6)
            throw new InvalidOperationException("The stored training image has no measurable contrast along the shape-model features.");
        return new(samples, trainingMean, trainingOriginX, trainingOriginY);
    }

    private static double MeasureCandidateContrast(Mat image, ShapeContrastReference reference,
        double poseX, double poseY, double angle, double scaleX, double scaleY)
    {
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return MeanPointContrast(image, reference.Points, point =>
            (poseX + cos * scaleX * point.X + sin * scaleY * point.Y,
             poseY - sin * scaleX * point.X + cos * scaleY * point.Y));
    }

    private static double MeanPointContrast(Mat image, IReadOnlyList<ShapeFeaturePoint> points,
        Func<ShapeFeaturePoint, (double X, double Y)> project)
    {
        if (points.Count == 0) return 0;
        double total = 0;
        foreach (var point in points)
        {
            var position = project(point);
            total += LocalGradientContrast(image, position.X, position.Y);
        }
        return total / points.Count;
    }

    private static double LocalGradientContrast(Mat image, double x, double y)
    {
        var centerX = (int)Math.Round(x);
        var centerY = (int)Math.Round(y);
        var best = 0d;
        for (var offsetY = -1; offsetY <= 1; offsetY++)
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                var px = centerX + offsetX;
                var py = centerY + offsetY;
                if (px <= 0 || py <= 0 || px >= image.Width - 1 || py >= image.Height - 1) continue;
                var gx = image.At<byte>(py, px + 1) - image.At<byte>(py, px - 1);
                var gy = image.At<byte>(py + 1, px) - image.At<byte>(py - 1, px);
                best = Math.Max(best, Math.Min(255, Math.Sqrt(gx * gx + gy * gy)));
            }
        return best;
    }

    private static double CandidateScore(ShapeMatchCandidate candidate, bool scoreUsingClutter) =>
        scoreUsingClutter && candidate.ClutterScore is { } clutter
            ? candidate.Score * (1 - Math.Clamp(clutter, 0, 1))
            : candidate.Score;

    private static JlTuple CreateShapeSearchSubPixel(int maxDeformationPx, bool clutterEnabled) =>
        maxDeformationPx > 0
            ? new JlTuple(new[] { "least_squares", $"max_deformation {maxDeformationPx}" })
            : new JlTuple(clutterEnabled ? "interpolation" : "least_squares");

    private static JlTuple CreateShapeSearchMinScore(double minScore, double maxClutterScore, bool clutterEnabled) =>
        new(clutterEnabled ? new[] { minScore, maxClutterScore } : new[] { minScore });

    private static void FindShapeWithOptions(JlImage image, JlShapeModel model, double angleStart, double angleExtent,
        double minScore, double maxClutterScore, int numMatches, int numLevels, int maxDeformationPx, bool clutterEnabled,
        double maxOverlap, double greediness, out JlTuple rows, out JlTuple columns, out JlTuple angles, out JlTuple scores)
    {
        using var minimumScores = CreateShapeSearchMinScore(minScore, maxClutterScore, clutterEnabled);
        using var subPixel = CreateShapeSearchSubPixel(maxDeformationPx, clutterEnabled);
        using var levels = new JlTuple(numLevels);
        using var modelId = new JlTuple(model);
        using var start = new JlTuple(angleStart);
        using var extent = new JlTuple(angleExtent);
        using var matches = new JlTuple(numMatches);
        using var overlap = new JlTuple(maxOverlap);
        using var searchGreediness = new JlTuple(greediness);
        JlOperatorSet.FindShapeModel(image, modelId, start, extent, minimumScores, matches, overlap, subPixel,
            levels, searchGreediness, out rows, out columns, out angles, out scores);
    }

    private static void FindScaledShapeWithOptions(JlImage image, JlShapeModel model, double angleStart, double angleExtent,
        double scaleMin, double scaleMax, double minScore, double maxClutterScore, int numMatches, int numLevels,
        int maxDeformationPx, bool clutterEnabled, double maxOverlap, double greediness, out JlTuple rows, out JlTuple columns, out JlTuple angles,
        out JlTuple scales, out JlTuple scores)
    {
        using var minimumScores = CreateShapeSearchMinScore(minScore, maxClutterScore, clutterEnabled);
        using var subPixel = CreateShapeSearchSubPixel(maxDeformationPx, clutterEnabled);
        using var levels = new JlTuple(numLevels);
        using var modelId = new JlTuple(model);
        using var start = new JlTuple(angleStart);
        using var extent = new JlTuple(angleExtent);
        using var minimumScale = new JlTuple(scaleMin);
        using var maximumScale = new JlTuple(scaleMax);
        using var matches = new JlTuple(numMatches);
        using var overlap = new JlTuple(maxOverlap);
        using var searchGreediness = new JlTuple(greediness);
        JlOperatorSet.FindScaledShapeModel(image, modelId, start, extent, minimumScale, maximumScale, minimumScores,
            matches, overlap, subPixel, levels, searchGreediness, out rows, out columns, out angles, out scales, out scores);
    }

    private static void FindAnisoShapeWithOptions(JlImage image, JlShapeModel model, double angleStart, double angleExtent,
        double scaleYMin, double scaleYMax, double scaleXMin, double scaleXMax, double minScore, double maxClutterScore,
        int numMatches, int numLevels, int maxDeformationPx, bool clutterEnabled, double maxOverlap, double greediness, out JlTuple rows,
        out JlTuple columns, out JlTuple angles, out JlTuple scaleY, out JlTuple scaleX, out JlTuple scores)
    {
        using var minimumScores = CreateShapeSearchMinScore(minScore, maxClutterScore, clutterEnabled);
        using var subPixel = CreateShapeSearchSubPixel(maxDeformationPx, clutterEnabled);
        using var levels = new JlTuple(numLevels);
        using var modelId = new JlTuple(model);
        using var start = new JlTuple(angleStart);
        using var extent = new JlTuple(angleExtent);
        using var yMin = new JlTuple(scaleYMin);
        using var yMax = new JlTuple(scaleYMax);
        using var xMin = new JlTuple(scaleXMin);
        using var xMax = new JlTuple(scaleXMax);
        using var matches = new JlTuple(numMatches);
        using var overlap = new JlTuple(maxOverlap);
        using var searchGreediness = new JlTuple(greediness);
        JlOperatorSet.FindAnisoShapeModel(image, modelId, start, extent, yMin, yMax, xMin, xMax, minimumScores,
            matches, overlap, subPixel, levels, searchGreediness, out rows, out columns, out angles, out scaleY, out scaleX, out scores);
    }

    private static double Ssd(JlImage scene, JlImage template, double x, double y, double angle, double originX, double originY)
    {
        using var source = JLVisionImages.ToMat(scene); template.GetImageSize(out int width, out int height);
        using var inverse = new Mat(2, 3, MatType.CV_64FC1); var c = Math.Cos(angle); var s = Math.Sin(angle);
        inverse.Set(0, 0, c); inverse.Set(0, 1, -s); inverse.Set(0, 2, originX - c * x + s * y);
        inverse.Set(1, 0, s); inverse.Set(1, 1, c); inverse.Set(1, 2, originY - s * x - c * y);
        using var aligned = new Mat(); JLVisionOperators.WarpAffine(source, aligned, inverse, new(width, height));
        using var alignedImage = JLVisionImages.FromMat(aligned); using var ar = alignedImage.ConvertImageType("real"); using var tr = template.ConvertImageType("real");
        using var difference = ar.SubImage(tr, 1d, 0d); using var squared = difference.MultImage(difference, 1d, 0d);
        using var a2 = ar.MultImage(ar, 1d, 0d); using var t2 = tr.MultImage(tr, 1d, 0d); using var domain = tr.GetDomain();
        var energy = Math.Sqrt(a2.Intensity(domain, out double _) * t2.Intensity(domain, out double _));
        return energy > 0 ? 1 - Math.Clamp(squared.Intensity(domain, out double _) / energy, 0, 1) : 0;
    }
}
