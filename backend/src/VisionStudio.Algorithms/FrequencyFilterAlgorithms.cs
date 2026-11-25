using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class FrequencyFilterAlgorithms
{
    internal sealed record Options(
        string Filter, double WavelengthMinPx, double WavelengthMaxPx,
        double DirectionDeg, double DirectionWidthDeg, double MaskBlurPx,
        bool KeepDc, string OutputMode, int MaxPixels);

    internal sealed record FftResult(Mat Image, int PaddedWidth, int PaddedHeight, double PassRatio);

    internal static Mat Convolve(
        Mat source, double[][] kernel, double divisor, Point? anchor, double delta, BorderTypes border,
        CancellationToken cancellationToken)
    {
        var rows = kernel.Length;
        var cols = kernel[0].Length;
        var buffer = new float[rows * cols];
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < cols; column++)
                buffer[row * cols + column] = (float)(kernel[row][column] / divisor);

        using var kernelMat = new Mat(rows, cols, MatType.CV_32FC1);
        kernelMat.SetArray(buffer);
        using var source32 = new Mat();
        source.ConvertTo(source32, MatType.CV_32F);
        using var filtered32 = new Mat();
        Cv2.Filter2D(source32, filtered32, MatType.CV_32F, kernelMat, anchor, delta, border);
        cancellationToken.ThrowIfCancellationRequested();
        return ToSourceDepth(filtered32, source.Type());
    }

    internal static FftResult Filter(Mat source, Options options, CancellationToken cancellationToken)
    {
        var paddedWidth = Cv2.GetOptimalDFTSize(source.Cols);
        var paddedHeight = Cv2.GetOptimalDFTSize(source.Rows);
        var paddedPixels = (long)paddedWidth * paddedHeight;
        if (paddedPixels > options.MaxPixels)
            throw new InvalidOperationException(
                $"image.fftFilter would pad to {paddedWidth}x{paddedHeight} ({paddedPixels} px) above maxPixels={options.MaxPixels}; " +
                "downscale upstream (image.resize) or raise maxPixels — FFT memory grows with the padded area.");

        using var mask = BuildMask(paddedWidth, paddedHeight, options, out var passRatio);
        if (passRatio <= 0)
            throw new InvalidOperationException(
                "image.fftFilter mask removes every frequency; widen the wavelength band or the direction window.");

        var channels = source.Channels();
        var split = Cv2.Split(source);
        var filtered = new Mat[channels];
        try
        {
            for (var index = 0; index < channels; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                filtered[index] = FilterChannel(split[index], mask, paddedWidth, paddedHeight, options, cancellationToken);
            }

            using var merged = new Mat();
            Cv2.Merge(filtered, merged);
            return new FftResult(ToSourceDepth(merged, source.Type()), paddedWidth, paddedHeight, passRatio);
        }
        finally
        {
            foreach (var mat in split) mat.Dispose();
            foreach (var mat in filtered) mat?.Dispose();
        }
    }

    private static Mat ToSourceDepth(Mat source32, MatType sourceType)
    {
        var result = new Mat();
        try { source32.ConvertTo(result, sourceType); return result; }
        catch { result.Dispose(); throw; }
    }

    private static Mat BuildMask(int width, int height, Options options, out double passRatio)
    {
        // 未搬移的频谱以角点为 DC；按周期环绕计算频率，不需要 fftShift。
        var (fLow, fHigh) = Band(options);
        var wedge = options.DirectionWidthDeg is > 0 and < 360;
        var halfWidthRad = options.DirectionWidthDeg * Math.PI / 360.0;
        var direction = options.DirectionDeg % 180.0;
        if (direction < 0) direction += 180.0;
        var directionRad = direction * Math.PI / 180.0;
        var keepOnly = options.Filter == "bandpass";

        var buffer = new float[width * height];
        long passed = 0;
        for (var y = 0; y < height; y++)
        {
            var cy = y <= height / 2 ? y : y - height;
            var fy = (double)cy / height;
            for (var x = 0; x < width; x++)
            {
                var cx = x <= width / 2 ? x : x - width;
                var fx = (double)cx / width;
                var frequency = Math.Sqrt(fx * fx + fy * fy);
                var inBand = frequency >= fLow && frequency <= fHigh;
                bool keep = options.Filter switch
                {
                    "lowpass" => frequency <= fHigh,
                    "highpass" => frequency >= fLow,
                    "bandpass" => inBand,
                    _ => !inBand
                };
                if (wedge)
                {
                    var angle = Math.Atan2(fy, fx);
                    var delta = Math.Abs(NormalizeRadians(angle - directionRad));
                    delta = Math.Min(delta, Math.PI - delta);
                    var inWedge = delta <= halfWidthRad;
                    keep = keepOnly ? keep && inWedge : !(!keep && inWedge);
                }
                buffer[y * width + x] = keep ? 1f : 0f;
                if (keep) passed++;
            }
        }

        passRatio = (double)passed / (width * height);
        var mask = new Mat(height, width, MatType.CV_32FC1);
        try
        {
            mask.SetArray(buffer);
            if (options.MaskBlurPx > 0)
                // 频谱掩膜是周期性的，不能按普通图像边界处理。
                Cv2.GaussianBlur(mask, mask, new Size(0, 0), options.MaskBlurPx, options.MaskBlurPx, BorderTypes.Wrap);
            if (options.KeepDc) mask.Set(0, 0, 1f);
            return mask;
        }
        catch { mask.Dispose(); throw; }
    }

    private static (double Flow, double Fhigh) Band(Options options)
    {
        var fMinWavelength = Math.Min(0.5, 1.0 / options.WavelengthMinPx);
        var fMaxWavelength = Math.Min(0.5, 1.0 / options.WavelengthMaxPx);
        return options.Filter switch
        {
            "lowpass" => (0, fMinWavelength),
            "highpass" => (fMaxWavelength, 0.5),
            _ => (fMaxWavelength, fMinWavelength)
        };
    }

    private static double NormalizeRadians(double radians)
    {
        var value = radians % (2 * Math.PI);
        if (value <= -Math.PI) value += 2 * Math.PI;
        if (value > Math.PI) value -= 2 * Math.PI;
        return value;
    }

    private static Mat FilterChannel(
        Mat channel, Mat mask, int paddedWidth, int paddedHeight, Options options, CancellationToken cancellationToken)
    {
        using var source32 = new Mat();
        channel.ConvertTo(source32, MatType.CV_32F);
        using var padded = new Mat();
        Cv2.CopyMakeBorder(source32, padded, 0, paddedHeight - source32.Rows, 0, paddedWidth - source32.Cols,
            BorderTypes.Reflect101, Scalar.All(0));
        cancellationToken.ThrowIfCancellationRequested();
        using var spectrum = new Mat();
        Cv2.Dft(padded, spectrum, DftFlags.ComplexOutput);
        cancellationToken.ThrowIfCancellationRequested();

        var planes = Cv2.Split(spectrum);
        try
        {
            Cv2.Multiply(planes[0], mask, planes[0]);
            Cv2.Multiply(planes[1], mask, planes[1]);
            using var merged = new Mat();
            Cv2.Merge(planes, merged);
            using var inverse = new Mat();
            Cv2.Idft(merged, inverse, DftFlags.Scale | DftFlags.RealOutput);
            using var cropped = new Mat(inverse, new Rect(0, 0, source32.Cols, source32.Rows));
            if (options.OutputMode == "removed")
            {
                Mat? removed = new Mat();
                try
                {
                    Cv2.Subtract(source32, cropped, removed);
                    if (channel.Depth() == MatType.CV_32F)
                    {
                        var result = removed;
                        removed = null;
                        return result;
                    }
                    // 整型残差加回输入均值，避免负半周被饱和截掉。
                    var centered = new Mat();
                    try { removed.ConvertTo(centered, MatType.CV_32F, 1.0, Cv2.Mean(channel).Val0); return centered; }
                    catch { centered.Dispose(); throw; }
                }
                finally { removed?.Dispose(); }
            }
            return cropped.Clone();
        }
        finally { foreach (var plane in planes) plane.Dispose(); }
    }
}
