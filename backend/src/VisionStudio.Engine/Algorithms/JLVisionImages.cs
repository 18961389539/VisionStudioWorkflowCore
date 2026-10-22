using System.Runtime.InteropServices;
using JLVisionLib;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

/// <summary>Copying boundary between legacy camera/plugin buffers and owned JLVision images.</summary>
public static class JLVisionImages
{
    public static JlImage FromMat(Mat source)
    {
        if (source.Empty()) throw new InvalidOperationException("JLVision requires a non-empty image.");
        using var packed = source.IsContinuous() ? null : source.Clone();
        var data = packed ?? source;
        var image = new JlImage();
        try
        {
            var type = data.Depth() switch { MatType.CV_8U => "byte", MatType.CV_16U => "uint2", MatType.CV_16S => "int2", MatType.CV_32F => "real", MatType.CV_32S => "int4", _ => throw new InvalidOperationException($"Unsupported JLVision pixel depth: {data.Type()}.") };
            if (data.Channels() == 1) image.GenImage1(type, data.Width, data.Height, data.Data);
            else if (data.Channels() is 3 or 4 && type == "byte")
                image.GenImageInterleaved(data.Data, data.Channels() == 3 ? "bgr" : "bgrx", data.Width, data.Height, 0, type, data.Width, data.Height, 0, 0, -1, 0);
            else throw new InvalidOperationException("JLVision supports single-channel numeric images and 8-bit BGR/BGRA buffers.");
            // Explicit copy detaches even native overloads whose pointer ownership differs by runtime version.
            var owned = image.CopyImage();
            image.Dispose();
            return owned;
        }
        catch { image.Dispose(); throw; }
    }

    public static Mat ToMat(JlImage image)
    {
        using var channels = image.CountChannels();
        if (channels.I == 1)
        {
            var pointer = image.GetImagePointer1(out string type, out int width, out int height);
            var matType = type switch { "byte" => MatType.CV_8UC1, "uint2" => MatType.CV_16UC1, "int2" => MatType.CV_16SC1, "real" => MatType.CV_32FC1, "int4" => MatType.CV_32SC1, _ => throw new InvalidOperationException($"Unsupported JLVision output type '{type}'.") };
            var output = new Mat(height, width, matType);
            try { Copy(pointer, output.Data, checked(width * height * (int)output.ElemSize())); return output; }
            catch { output.Dispose(); throw; }
        }
        if (channels.I != 3) throw new InvalidOperationException($"Unsupported JLVision output channels: {channels.I}.");
        image.GetImagePointer3(out IntPtr red, out IntPtr green, out IntPtr blue, out string pixelType, out int w, out int h);
        if (pixelType != "byte") throw new InvalidOperationException("Color JLVision outputs must use byte pixels.");
        var length = checked(w * h);
        var r = new byte[length]; var g = new byte[length]; var b = new byte[length];
        Marshal.Copy(red, r, 0, length); Marshal.Copy(green, g, 0, length); Marshal.Copy(blue, b, 0, length);
        var bytes = new byte[checked(length * 3)];
        for (var i = 0; i < length; i++) { bytes[3 * i] = b[i]; bytes[3 * i + 1] = g[i]; bytes[3 * i + 2] = r[i]; }
        var result = new Mat(h, w, MatType.CV_8UC3);
        try { Marshal.Copy(bytes, 0, result.Data, bytes.Length); return result; }
        catch { result.Dispose(); throw; }
    }

    public static void Store(JlImage image, Mat destination)
    {
        using var converted = ToMat(image);
        converted.CopyTo(destination);
    }

    private static void Copy(IntPtr source, IntPtr target, int bytes)
    {
        var buffer = new byte[bytes];
        Marshal.Copy(source, buffer, 0, bytes);
        Marshal.Copy(buffer, 0, target, bytes);
    }
}
