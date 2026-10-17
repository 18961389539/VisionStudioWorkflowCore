namespace VisionStudio.Engine.Algorithms;

/// <summary>Pure subpixel translation refinement for registration: zero-mean normalized
/// cross-correlation of a template over a small integer window around a known match,
/// then parabolic interpolation of the correlation surface per axis.</summary>
public static class RegistrationRefinement
{
    public sealed record Refinement(double OffsetX, double OffsetY, double PeakScore);

    public static double Ncc(byte[] image, int imageWidth, byte[] template, int templateWidth, int templateHeight, int originX, int originY)
    {
        var imageHeight = image.Length / imageWidth;
        if (originX < 0 || originY < 0 || originX + templateWidth > imageWidth || originY + templateHeight > imageHeight) return -1;
        double meanT = 0, meanI = 0;
        var count = templateWidth * templateHeight;
        for (var y = 0; y < templateHeight; y++)
        for (var x = 0; x < templateWidth; x++) { meanT += template[y * templateWidth + x]; meanI += image[(originY + y) * imageWidth + originX + x]; }
        meanT /= count; meanI /= count;
        double numerator = 0, sumT = 0, sumI = 0;
        for (var y = 0; y < templateHeight; y++)
        for (var x = 0; x < templateWidth; x++)
        {
            var dt = template[y * templateWidth + x] - meanT;
            var di = image[(originY + y) * imageWidth + originX + x] - meanI;
            numerator += dt * di; sumT += dt * dt; sumI += di * di;
        }
        var denominator = Math.Sqrt(sumT * sumI);
        return denominator < 1e-12 ? -1 : numerator / denominator;
    }

    public static Refinement Refine(byte[] image, int imageWidth, byte[] template, int templateWidth, int templateHeight, int originX, int originY)
    {
        var scores = new double[3, 3];
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
            scores[dy + 1, dx + 1] = Ncc(image, imageWidth, template, templateWidth, templateHeight, originX + dx, originY + dy);
        var peak = scores[1, 1];
        var offsetX = Parabola(scores[1, 0], scores[1, 1], scores[1, 2]);
        var offsetY = Parabola(scores[0, 1], scores[1, 1], scores[2, 1]);
        return new(offsetX, offsetY, peak);
    }

    private static double Parabola(double left, double center, double right)
    {
        var denominator = left - 2 * center + right;
        if (Math.Abs(denominator) < 1e-12) return 0;
        return Math.Clamp(0.5 * (left - right) / denominator, -1, 1);
    }
}
