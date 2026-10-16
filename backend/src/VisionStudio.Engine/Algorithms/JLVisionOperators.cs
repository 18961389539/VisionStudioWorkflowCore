using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

/// <summary>JLVision image algorithms with the existing buffer/coordinate contracts. No OpenCV fallback.</summary>
internal static partial class JLVisionOperators
{
    public static void CvtColor(Mat source, Mat target, ColorConversionCodes code)
    {
        using var image = JLVisionImages.FromMat(source);
        using var count = image.CountChannels();
        if (code is ColorConversionCodes.BGR2GRAY or ColorConversionCodes.BGRA2GRAY)
        { using var gray = count.I == 1 ? image.CopyImage() : image.Rgb1ToGray(); JLVisionImages.Store(gray, target); return; }
        if (code == ColorConversionCodes.GRAY2BGR)
        { using var color = image.Compose3(image, image); JLVisionImages.Store(color, target); return; }
        if (code == ColorConversionCodes.BGRA2BGR)
        { JLVisionImages.Store(image, target); return; }
        if (code == ColorConversionCodes.BGR2HSV)
        {
            using var red = image.Decompose3(out var green, out var blue);
            using (green) using (blue)
            using (var hue = red.TransFromRgb(green, blue, out var saturation, out var value, "hsv"))
            using (saturation) using (value)
            {
                // JLVision byte hue spans 0..255, existing BGR HSV buffers span 0..179.
                using var cvHue = hue.ScaleImage(179d / 255, 0d);
                // ToMat returns BGR; compose in reverse so B=hue, G=saturation, R=value.
                using var combined = value.Compose3(saturation, cvHue);
                JLVisionImages.Store(combined, target);
            }
            return;
        }
        throw new InvalidOperationException($"Unsupported JLVision color conversion: {code}.");
    }

    public static void ExtractChannel(Mat source, Mat target, int channel)
    {
        using var image = JLVisionImages.FromMat(source);
        using var n = image.CountChannels();
        var nativeChannel = n.I == 3 ? 3 - channel : channel + 1;
        using var result = image.AccessChannel(nativeChannel);
        JLVisionImages.Store(result, target);
    }
    public static void Split(Mat source, out Mat[] channels)
    {
        channels = new Mat[source.Channels()];
        try { for (var i = 0; i < channels.Length; i++) { channels[i] = new Mat(); ExtractChannel(source, channels[i], i); } }
        catch { foreach (var channel in channels) channel?.Dispose(); throw; }
    }
    public static void Max(Mat a, Mat b, Mat target)
    { using var x = JLVisionImages.FromMat(a); using var y = JLVisionImages.FromMat(b); using var result = x.MaxImage(y); JLVisionImages.Store(result, target); }
    public static void AddWeighted(Mat a, double alpha, Mat b, double beta, double gamma, Mat target)
    {
        using var x = JLVisionImages.FromMat(a); using var y = JLVisionImages.FromMat(b);
        using var xr = x.ConvertImageType("real"); using var yr = y.ConvertImageType("real");
        using var xs = xr.ScaleImage(alpha, gamma); using var ys = yr.ScaleImage(beta, 0d);
        using var sum = xs.AddImage(ys, 1d, 0d); using var result = sum.ConvertImageType("byte"); JLVisionImages.Store(result, target);
    }
    public static void GaussianBlur(Mat source, Mat target, Size kernel, double sigmaX, double sigmaY = 0)
    {
        if (kernel.Width < 1 || kernel.Height < 1) throw new InvalidOperationException("Gaussian kernel dimensions must be positive.");
        if (kernel.Width == 1 && kernel.Height == 1) { source.CopyTo(target); return; }
        using var image = JLVisionImages.FromMat(source);
        if (sigmaX <= 0 && sigmaY <= 0 && kernel.Width == kernel.Height && kernel.Width <= 11)
        { using var smooth = image.GaussImage(kernel.Width); JLVisionImages.Store(smooth, target); return; }
        var sx = sigmaX > 0 ? sigmaX : 0.3 * ((kernel.Width - 1) * 0.5 - 1) + 0.8;
        var sy = sigmaY > 0 ? sigmaY : sx;
        var padX = Math.Max(kernel.Width / 2, (int)Math.Ceiling(6 * sx)); var padY = Math.Max(kernel.Height / 2, (int)Math.Ceiling(6 * sy));
        using var padded = ReflectPadding(source, padX, padY);
        using var paddedImage = JLVisionImages.FromMat(padded);
        using var pixelType = image.GetImageType();
        using var frequency = JLVisionExtendedOperators.Rft(paddedImage, "to_freq", "none", padded.Width);
        using var filter = new JlImage();
        filter.GenGaussFilter(sx, sy, 0d, "none", "rft", padded.Width, padded.Height);
        using var convolved = JLVisionExtendedOperators.Convolve(frequency, filter);
        using var full = JLVisionExtendedOperators.Rft(convolved, "from_freq", "n", padded.Width);
        using var spatial = full.CropRectangle1(padY, padX, padY + source.Height - 1, padX + source.Width - 1);
        using var result = spatial.ConvertImageType(pixelType[0].S);
        JLVisionImages.Store(result, target);
    }
    private static Mat ReflectPadding(Mat source, int padX, int padY)
    {
        using var packed = source.IsContinuous() ? null : source.Clone(); var image = packed ?? source;
        var width = image.Width; var height = image.Height; var pixelBytes = (int)image.ElemSize();
        var paddedWidth = checked(width + 2 * padX); var paddedHeight = checked(height + 2 * padY);
        var pixels = new byte[checked(width * height * pixelBytes)]; var padded = new byte[checked(paddedWidth * paddedHeight * pixelBytes)];
        System.Runtime.InteropServices.Marshal.Copy(image.Data, pixels, 0, pixels.Length);
        int Reflect(int x, int length) { if (length == 1) return 0; while (x < 0 || x >= length) x = x < 0 ? -x : 2 * length - 2 - x; return x; }
        for (var row = 0; row < paddedHeight; row++) for (var col = 0; col < paddedWidth; col++)
            Buffer.BlockCopy(pixels, (Reflect(row - padY, height) * width + Reflect(col - padX, width)) * pixelBytes, padded, (row * paddedWidth + col) * pixelBytes, pixelBytes);
        var result = new Mat(paddedHeight, paddedWidth, source.Type());
        System.Runtime.InteropServices.Marshal.Copy(padded, 0, result.Data, padded.Length); return result;
    }
    public static void MedianBlur(Mat source, Mat target, int kernel)
    { using var image = JLVisionImages.FromMat(source); using var filtered = image.MedianImage("square", kernel / 2, "mirrored"); JLVisionImages.Store(filtered, target); }
    public static double Threshold(Mat source, Mat target, double threshold, double maxValue, ThresholdTypes type)
    {
        using var image = JLVisionImages.FromMat(source);
        var inverted = (type & ThresholdTypes.BinaryInv) != 0;
        JlRegion region;
        if ((type & ThresholdTypes.Otsu) != 0)
        {
            using var automatic = image.BinaryThreshold("max_separability", inverted ? "dark" : "light", out int used);
            using var domain = image.GetDomain();
            image.MinMaxGray(domain, 0d, out double minimum, out double maximum, out double _);
            threshold = minimum == maximum ? 0 : used;
            region = image.Threshold(inverted ? 0d : Math.Floor(threshold) + 1, inverted ? Math.Floor(threshold) : 255d);
        }
        else region = image.Threshold(inverted ? 0d : Math.Floor(threshold) + 1, inverted ? Math.Floor(threshold) : 255d);
        using (region)
        using (var binary = region.RegionToBin((int)maxValue, 0, source.Width, source.Height)) JLVisionImages.Store(binary, target);
        return threshold;
    }
    public static void AdaptiveThreshold(Mat source, Mat target, double maxValue, AdaptiveThresholdTypes method, ThresholdTypes type, int blockSize, double c)
    {
        using var image = JLVisionImages.FromMat(source);
        using var mean = method == AdaptiveThresholdTypes.MeanC ? image.MeanImage(blockSize, blockSize) : image.GaussImage(blockSize);
        using var region = image.DynThreshold(mean, -c, (type & ThresholdTypes.BinaryInv) != 0 ? "dark" : "light");
        using var binary = region.RegionToBin((int)maxValue, 0, source.Width, source.Height); JLVisionImages.Store(binary, target);
    }
    public static void BitwiseNot(Mat source, Mat target)
    { using var image = JLVisionImages.FromMat(source); using var inverted = image.InvertImage(); JLVisionImages.Store(inverted, target); }
    public static int CountNonZero(Mat source)
    { using var image = JLVisionImages.FromMat(source); using var region = image.Threshold(1d, 255d); return region.AreaCenter(out double _, out double _); }
    public static Mat CropBinaryMask(Mat source, out Rect bounds, out int selectedPixels)
    {
        if (source.Empty() || source.Type() != MatType.CV_8UC1)
            throw new InvalidOperationException("A non-empty 8-bit single-channel mask is required.");

        using var image = JLVisionImages.FromMat(source);
        using var region = image.Threshold(1d, 255d);
        selectedPixels = region.AreaCenter(out double _, out double _);
        if (selectedPixels == 0)
        {
            bounds = default;
            return new Mat();
        }

        region.SmallestRectangle1(out int top, out int left, out int bottom, out int right);
        bounds = new Rect(left, top, right - left + 1, bottom - top + 1);
        using var croppedMask = image.CropRectangle1(top, left, bottom, right);
        using var croppedRegion = croppedMask.Threshold(1d, 255d);
        using var binary = croppedRegion.RegionToBin(255, 0, bounds.Width, bounds.Height);
        return JLVisionImages.ToMat(binary);
    }
    public static void Resize(Mat source, Mat target, Size size, double fx = 0, double fy = 0, InterpolationFlags interpolation = InterpolationFlags.Linear)
    {
        using var image = JLVisionImages.FromMat(source);
        using var resized = image.ZoomImageSize(size.Width, size.Height, interpolation == InterpolationFlags.Nearest ? "nearest_neighbor" : interpolation == InterpolationFlags.Cubic ? "bicubic" : interpolation == InterpolationFlags.Area ? "weighted" : "bilinear");
        JLVisionImages.Store(resized, target);
    }
    public static Mat GetStructuringElement(MorphShapes shape, Size size)
    {
        var kernel = new Mat(size.Height, size.Width, MatType.CV_8UC1, Scalar.All(shape == MorphShapes.Rect ? 1 : 0));
        if (shape != MorphShapes.Rect)
            for (var y = 0; y < size.Height; y++) for (var x = 0; x < size.Width; x++)
                if (shape == MorphShapes.Cross ? x == size.Width / 2 || y == size.Height / 2 : Math.Pow((x - size.Width / 2d + .5) / (size.Width / 2d), 2) + Math.Pow((y - size.Height / 2d + .5) / (size.Height / 2d), 2) <= 1)
                    kernel.Set(y, x, (byte)1);
        return kernel;
    }
    public static void Erode(Mat source, Mat target, Mat element, int iterations = 1) => MorphologyEx(source, target, MorphTypes.Erode, element, iterations);
    public static void Dilate(Mat source, Mat target, Mat element, int iterations = 1) => MorphologyEx(source, target, MorphTypes.Dilate, element, iterations);
    public static void MorphologyEx(Mat source, Mat target, MorphTypes operation, Mat element, int iterations = 1)
    {
        using var kernelImage = JLVisionImages.FromMat(element);
        using var mask = kernelImage.Threshold(1d, 255d);
        using var heights = new JlImage("byte", element.Width, element.Height);
        using var structuring = heights.ReduceDomain(mask);
        using var image = JLVisionImages.FromMat(source);
        var result = image.CopyImage();
        try
        {
            for (var i = 0; i < iterations; i++)
            {
                JlImage next = operation switch { MorphTypes.Erode => result.GrayErosion(structuring), MorphTypes.Dilate => result.GrayDilation(structuring), MorphTypes.Open => result.GrayOpening(structuring), MorphTypes.Close => result.GrayClosing(structuring), _ => throw new InvalidOperationException($"Unsupported morphology operation '{operation}'.") };
                result.Dispose(); result = next;
            }
            JLVisionImages.Store(result, target);
        }
        finally { result.Dispose(); }
    }
}
