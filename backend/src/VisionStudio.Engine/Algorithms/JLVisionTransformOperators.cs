using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static partial class JLVisionOperators
{
    public static Mat GetRotationMatrix2D(Point2f center, double angle, double scale)
    {
        var a = scale * Math.Cos(angle * Math.PI / 180); var b = scale * Math.Sin(angle * Math.PI / 180);
        var matrix = new Mat(2, 3, MatType.CV_64FC1);
        matrix.Set(0, 0, a); matrix.Set(0, 1, b); matrix.Set(0, 2, (1 - a) * center.X - b * center.Y);
        matrix.Set(1, 0, -b); matrix.Set(1, 1, a); matrix.Set(1, 2, b * center.X + (1 - a) * center.Y);
        return matrix;
    }
    public static Mat GetPerspectiveTransform(Point2f[] source, Point2f[] target)
    {
        using var sx = new JlTuple(source.Select(p => (double)p.X).ToArray()); using var sy = new JlTuple(source.Select(p => (double)p.Y).ToArray());
        using var tx = new JlTuple(target.Select(p => (double)p.X).ToArray()); using var ty = new JlTuple(target.Select(p => (double)p.Y).ToArray());
        var fitted = JLVisionExtendedOperators.Tuples(261, "vector_to_proj_hom_mat2d", 2, new(sx), new(sy), new(tx), new(ty), new("normalized_dlt"), new(), new(), new(), new(), new(), new());
        using var raw = fitted[0]; using var covariance = fitted[1];
        var matrix = new Mat(3, 3, MatType.CV_64FC1);
        for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) matrix.Set(r, c, raw[r * 3 + c].D);
        return matrix;
    }
    public static void WarpAffine(Mat source, Mat target, Mat matrix, Size size, InterpolationFlags interpolation = InterpolationFlags.Linear, BorderTypes borderMode = BorderTypes.Constant, Scalar borderValue = default)
    {
        using var image = JLVisionImages.FromMat(source);
        var a = matrix.At<double>(1, 1); var b = matrix.At<double>(1, 0); var c = matrix.At<double>(0, 1); var d = matrix.At<double>(0, 0);
        using var result = JLVisionExtendedOperators.Image(1542, "affine_trans_image_size", [image], new(new[] { a, b, matrix.At<double>(1, 2) + .5 * (1 - a - b), c, d, matrix.At<double>(0, 2) + .5 * (1 - c - d) }), new(interpolation == InterpolationFlags.Nearest ? "nearest_neighbor" : "bilinear"), new(size.Width), new(size.Height));
        JLVisionImages.Store(result, target);
    }
    public static void WarpPerspective(Mat source, Mat target, Mat matrix, Size size, InterpolationFlags interpolation = InterpolationFlags.Linear, BorderTypes borderMode = BorderTypes.Constant, Scalar borderValue = default)
    {
        // Swap x/y axes and change from pixel-centre to pixel-corner coordinates.
        var swap = new[,] { { 0d, 1, 0 }, { 1d, 0, 0 }, { 0d, 0, 1 } };
        var h = new double[3, 3]; for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) h[r, c] = matrix.At<double>(r, c);
        double[,] Multiply(double[,] x, double[,] y) { var z = new double[3, 3]; for (var r = 0; r < 3; r++) for (var c = 0; c < 3; c++) for (var i = 0; i < 3; i++) z[r, c] += x[r, i] * y[i, c]; return z; }
        var corrected = Multiply(new[,] { { 1d, 0, .5 }, { 0d, 1, .5 }, { 0d, 0, 1 } }, Multiply(Multiply(swap, Multiply(h, swap)), new[,] { { 1d, 0, -.5 }, { 0d, 1, -.5 }, { 0d, 0, 1 } }));
        using var image = JLVisionImages.FromMat(source);
        using var result = JLVisionExtendedOperators.Image(1540, "projective_trans_image_size", [image], new(corrected.Cast<double>().ToArray()), new(interpolation == InterpolationFlags.Nearest ? "nearest_neighbor" : "bilinear"), new(size.Width), new(size.Height), new("false"));
        JLVisionImages.Store(result, target);
    }
}
