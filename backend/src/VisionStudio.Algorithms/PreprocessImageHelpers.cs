// 2026-11-25 从 PreprocessNodes.cs 下沉到算法层：
// 算法实现（JLVisionMatching / ContourThicknessMeasurement）需要它，
// 它却不该反过来依赖引擎的节点目录。

using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

internal static class PreprocessImageHelpers
{
    /// <summary>
    /// 把输入规范化为单通道 CV_8U（3/4 通道转灰度；UInt16 按 <paramref name="inputBitDepth"/> 缩放到 8 位）。
    /// <paramref name="inputBitDepth"/> 为 null（auto）时沿用满量程 1/257；指定位深时按 255/(2^bits-1) 映射，
    /// 否则 10/12 位相机的有效灰度会被压进 0..16 / 0..4，阈值几乎无法使用。
    /// </summary>
    public static Mat ToGray8(Mat source, out bool converted, out Mat? owned, int? inputBitDepth = null)
    {
        converted = false;
        owned = null;
        var gray = source;
        if (source.Channels() != 1)
        {
            if (source.Channels() is not (3 or 4))
                throw new InvalidOperationException($"Grayscale conversion supports 1, 3, or 4 channels; received {source.Channels()}.");
            var convertedGray = new Mat();
            try
            {
                JLVisionOperators.CvtColor(source, convertedGray, source.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            }
            catch
            {
                convertedGray.Dispose();
                throw;
            }
            gray = convertedGray;
            converted = true;
        }

        if (gray.Depth() == MatType.CV_8U)
        {
            if (converted) owned = gray;
            return gray;
        }

        var gray8 = new Mat();
        try
        {
            // auto 保持历史口径：把 uint16 满量程映射到 byte（1/257）；指定位深时按实际有效位深映射。
            // 其他数值深度用 OpenCV 的饱和转换，不做与图像相关的隐式归一化——那会让边缘/阈值含义逐帧漂移。
            var scale = gray.Depth() == MatType.CV_16U ? ByteScale(inputBitDepth) : 1d;
            gray.ConvertTo(gray8, MatType.CV_8UC1, scale);
        }
        catch
        {
            gray8.Dispose();
            if (converted) gray.Dispose();
            throw;
        }

        if (converted) gray.Dispose();
        owned = gray8;
        return gray8;
    }

    /// <summary>uint16 → byte 的缩放系数：auto（null）= 1/257 满量程，指定 b 位 = 255/(2^b-1)。</summary>
    public static double ByteScale(int? inputBitDepth) => inputBitDepth is { } bits ? 255d / ((1 << bits) - 1) : 1d / 257d;

    /// <summary>读取 inputBitDepth 参数：auto/空 → null（满量程），8..16 → 位深；其他值报错。</summary>
    public static int? InputBitDepth(NodeDefinition node, string nodeType)
    {
        var text = node.GetString("inputBitDepth", "auto").Trim();
        if (text.Length == 0 || text.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var bits) && bits is >= 8 and <= 16)
            return bits;
        throw new InvalidOperationException($"{nodeType} inputBitDepth '{text}' is unsupported; use auto or 8..16.");
    }

    /// <summary>ROI 掩膜：ROI 外清零（调用方交出 processed 的所有权；返回可继续持有的新 Mat）。</summary>
    public static Mat ApplyRoiMask(Mat processed, VisionRoi? roi, CancellationToken cancellationToken = default)
    {
        if (roi is null) return processed;
        Mat mask;
        try
        {
            mask = roi.BuildMask(processed.Size(), cancellationToken);
        }
        catch
        {
            processed.Dispose();
            throw;
        }

        using (mask) return ApplyMask(processed, mask, cancellationToken);
    }

    /// <summary>按已栅格化的掩膜清零（掩膜为 null 时原样返回，便于和 <see cref="ThresholdStatisticsDomain"/> 复用同一份掩膜）。</summary>
    public static Mat ApplyMask(Mat processed, Mat? mask, CancellationToken cancellationToken = default)
    {
        if (mask is null) return processed;
        Mat? masked = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            masked = new Mat(processed.Rows, processed.Cols, processed.Type(), Scalar.All(0));
            processed.CopyTo(masked, mask);
            cancellationToken.ThrowIfCancellationRequested();
            processed.Dispose();
            return masked;
        }
        catch
        {
            masked?.Dispose();
            processed.Dispose();
            throw;
        }
    }

    /// <summary>把域内处理结果贴回全画布（域未裁剪、即整图时直接返回原图）。</summary>
    public static Mat RestoreToCanvas(Mat patch, Rect rect, Size canvas, CancellationToken cancellationToken = default)
    {
        if (rect.X == 0 && rect.Y == 0 && rect.Width == canvas.Width && rect.Height == canvas.Height) return patch;
        Mat? full = null;
        try
        {
            full = new Mat(canvas.Height, canvas.Width, patch.Type(), Scalar.All(0));
            using (var view = new Mat(full, rect)) patch.CopyTo(view);
            cancellationToken.ThrowIfCancellationRequested();
            patch.Dispose();
            return full;
        }
        catch
        {
            full?.Dispose();
            patch.Dispose();
            throw;
        }
    }

    public static int OddKernel(int value, int min, int max)
    {
        var clamped = Math.Clamp(value, min, max);
        if (clamped % 2 == 1) return clamped;
        return clamped + 1 <= max ? clamped + 1 : clamped - 1;
    }

    public static Mat GaussianWithBorder(Mat source, int kernel, double sigmaX, double sigmaY, BorderTypes border, bool useDefault)
    {
        var pad = kernel / 2;
        if (pad == 0 || useDefault)
        {
            var result = new Mat();
            try { JLVisionOperators.GaussianBlur(source, result, new Size(kernel, kernel), sigmaX, sigmaY); return result; }
            catch { result.Dispose(); throw; }
        }
        using var padded = new Mat();
        Cv2.CopyMakeBorder(source, padded, pad, pad, pad, pad, border, Scalar.All(0));
        using var full = new Mat();
        JLVisionOperators.GaussianBlur(padded, full, new Size(kernel, kernel), sigmaX, sigmaY);
        using var crop = new Mat(full, new Rect(pad, pad, source.Width, source.Height));
        return crop.Clone();
    }

    public static Mat MedianWithBorder(Mat source, int kernel, BorderTypes border)
    {
        var pad = kernel / 2;
        using var padded = new Mat();
        Cv2.CopyMakeBorder(source, padded, pad, pad, pad, pad, border, Scalar.All(0));
        using var full = new Mat();
        JLVisionOperators.MedianBlur(padded, full, kernel);
        using var crop = new Mat(full, new Rect(pad, pad, source.Width, source.Height));
        return crop.Clone();
    }
}
