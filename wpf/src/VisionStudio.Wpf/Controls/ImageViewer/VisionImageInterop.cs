using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace VisionStudio.Wpf.Controls.ImageViewer;

/// <summary>
/// 进程内图像内存互操作工具：将 <see cref="Mat"/> 或内存字节流直接转换为冻结的 WPF <see cref="BitmapSource"/>，
/// 避免方案 C 桌面端在渲染实时帧时经过 HTTP 或磁盘中转。
/// </summary>
public static class VisionImageInterop
{
    /// <summary>
    /// 将 OpenCvSharp <see cref="Mat"/> 直接拷贝为冻结的 WPF <see cref="BitmapSource"/>。
    /// </summary>
    public static BitmapSource ToBitmapSource(Mat mat)
    {
        ArgumentNullException.ThrowIfNull(mat);
        if (mat.Empty())
        {
            throw new ArgumentException("输入图像 Mat 为空，无法转换为 BitmapSource。", nameof(mat));
        }

        // 若不是标准 8U/16U 通道格式，先归一化/转换到 8U 以便 WPF 直接渲染
        Mat? temporary = null;
        var source = mat;
        try
        {
            var type = mat.Type();
            if (type != MatType.CV_8UC1 && type != MatType.CV_8UC3 && type != MatType.CV_8UC4 && type != MatType.CV_16UC1)
            {
                temporary = new Mat();
                if (mat.Channels() == 1)
                {
                    mat.ConvertTo(temporary, MatType.CV_8UC1);
                }
                else if (mat.Channels() == 3)
                {
                    mat.ConvertTo(temporary, MatType.CV_8UC3);
                }
                else
                {
                    Cv2.CvtColor(mat, temporary, ColorConversionCodes.BGRA2BGR);
                    if (temporary.Depth() != MatType.CV_8U)
                    {
                        temporary.ConvertTo(temporary, MatType.CV_8UC3);
                    }
                }
                source = temporary;
            }

            if (!source.IsContinuous())
            {
                var contiguous = source.Clone();
                temporary?.Dispose();
                temporary = contiguous;
                source = contiguous;
            }

            var pixelFormat = source.Type() switch
            {
                var t when t == MatType.CV_8UC1 => PixelFormats.Gray8,
                var t when t == MatType.CV_16UC1 => PixelFormats.Gray16,
                var t when t == MatType.CV_8UC3 => PixelFormats.Bgr24,
                var t when t == MatType.CV_8UC4 => PixelFormats.Bgra32,
                _ => throw new NotSupportedException($"暂不支持的像素格式: {source.Type()}")
            };

            var stride = checked((int)source.Step());
            var bufferSize = checked(stride * source.Rows);
            var bitmap = BitmapSource.Create(
                source.Cols,
                source.Rows,
                96,
                96,
                pixelFormat,
                null,
                source.Data,
                bufferSize,
                stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            temporary?.Dispose();
        }
    }

    /// <summary>
    /// 从编码字节数组（JPEG / PNG）解码为冻结的 <see cref="BitmapSource"/>。
    /// </summary>
    public static BitmapSource FromEncodedBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// 读取指定像素坐标的灰度/RGB 文本描述，用于状态栏实时显示。
    /// </summary>
    public static string ReadPixelText(BitmapSource? source, int x, int y)
    {
        if (source is null || x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight)
        {
            return "-";
        }

        var bpp = (source.Format.BitsPerPixel + 7) / 8;
        if (bpp <= 0 || bpp > 8)
        {
            return $"({x}, {y})";
        }

        var buffer = new byte[bpp];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, bpp, 0);

        if (source.Format == PixelFormats.Gray8)
        {
            return $"({x}, {y}) 灰度={buffer[0]}";
        }
        if (source.Format == PixelFormats.Gray16)
        {
            var val = BitConverter.ToUInt16(buffer, 0);
            return $"({x}, {y}) 灰度16={val}";
        }
        if (bpp >= 3)
        {
            return $"({x}, {y}) RGB=({buffer[2]}, {buffer[1]}, {buffer[0]})";
        }

        return $"({x}, {y})";
    }
}
