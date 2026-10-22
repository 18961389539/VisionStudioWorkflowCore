using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionStudio.Abstractions;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine;

/// <summary>
/// File-backed memory mapped transport for large worker images. The backing file is only a rendezvous object;
/// pixel bytes are read/written through MemoryMappedFile. File names are random and confined to RootPath.
/// </summary>
public sealed class PluginWorkerSharedMemoryStore
{
    public const string TransportName = "FileBackedSharedMemory";
    private const long MaxImageBytes = 2L * 1024 * 1024 * 1024;

    public PluginWorkerSharedMemoryStore(string rootPath, int thresholdBytes)
    {
        RootPath = Path.GetFullPath(rootPath);
        ThresholdBytes = Math.Max(0, thresholdBytes);
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }
    public int ThresholdBytes { get; }

    public bool CanUse(Mat image)
    {
        if (ThresholdBytes <= 0) return false;
        if (!TryGetPixelFormat(image.Type(), out _, out var bytesPerPixel)) return false;
        var length = checked((long)image.Rows * image.Cols * bytesPerPixel);
        return length >= ThresholdBytes && length <= MaxImageBytes;
    }

    public PluginWorkerValue EncodeImage(Mat image)
    {
        if (!TryGetPixelFormat(image.Type(), out var pixelFormat, out var bytesPerPixel))
            throw new NotSupportedException($"Mat type '{image.Type()}' is not supported by the shared-memory worker image codec.");

        var payloadBytes = checked((long)image.Rows * image.Cols * bytesPerPixel);
        if (payloadBytes <= 0 || payloadBytes > MaxImageBytes)
            throw new InvalidOperationException($"Invalid worker shared image size {payloadBytes} bytes.");
        if (payloadBytes > int.MaxValue)
            throw new InvalidOperationException("Worker shared image exceeds the current 2 GB process buffer limit.");

        Mat? clone = null;
        var source = image;
        if (!image.IsContinuous())
        {
            clone = image.Clone();
            source = clone;
        }

        try
        {
            var bytes = new byte[(int)payloadBytes];
            Marshal.Copy(source.Data, bytes, 0, bytes.Length);
            var fileName = $"{Guid.NewGuid():N}.vsmem";
            var path = ResolveFile(fileName);
            using (var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.CreateNew, null, payloadBytes, MemoryMappedFileAccess.ReadWrite))
            using (var accessor = mmf.CreateViewAccessor(0, payloadBytes, MemoryMappedFileAccess.Write))
                accessor.WriteArray(0, bytes, 0, bytes.Length);

            return new PluginWorkerValue(
                VisionDataType.Image,
                SharedImage: new PluginWorkerSharedImage(fileName, image.Rows, image.Cols, pixelFormat, payloadBytes));
        }
        finally { clone?.Dispose(); }
    }

    public VisionValue DecodeImage(PluginWorkerSharedImage descriptor, bool deleteAfterRead = true)
    {
        if (descriptor.Transport != PluginWorkerImageTransport.FileBackedSharedMemory)
            throw new NotSupportedException($"Unsupported shared image transport '{descriptor.Transport}'.");
        if (descriptor.Rows <= 0 || descriptor.Cols <= 0)
            throw new InvalidOperationException("Worker shared image dimensions are invalid.");
        if (!TryGetMatType(descriptor.PixelFormat, out var matType, out var bytesPerPixel))
            throw new InvalidOperationException($"Worker shared image pixel format '{descriptor.PixelFormat}' is unsupported.");

        var expected = checked((long)descriptor.Rows * descriptor.Cols * bytesPerPixel);
        if (descriptor.PayloadBytes != expected || expected <= 0 || expected > MaxImageBytes || expected > int.MaxValue)
            throw new InvalidOperationException($"Worker shared image payload length mismatch. Expected {expected}, descriptor {descriptor.PayloadBytes}.");

        var path = ResolveFile(descriptor.FileName);
        if (!File.Exists(path)) throw new FileNotFoundException("Worker shared image backing file is missing.", path);
        if (new FileInfo(path).Length != expected)
            throw new InvalidOperationException("Worker shared image backing file length does not match its descriptor.");

        var bytes = new byte[(int)expected];
        try
        {
            using (var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, expected, MemoryMappedFileAccess.Read))
            using (var accessor = mmf.CreateViewAccessor(0, expected, MemoryMappedFileAccess.Read))
                accessor.ReadArray(0, bytes, 0, bytes.Length);

            var mat = new Mat(descriptor.Rows, descriptor.Cols, matType);
            Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
            return VisionValue.Image(VisionImage.Own(mat));
        }
        finally
        {
            if (deleteAfterRead) TryDelete(descriptor.FileName);
        }
    }

    public void Delete(PluginWorkerValue value)
    {
        if (value.SharedImage is not null) TryDelete(value.SharedImage.FileName);
    }

    public void CleanupStaleFiles(TimeSpan maxAge)
    {
        if (!Directory.Exists(RootPath)) return;
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var path in Directory.EnumerateFiles(RootPath, "*.vsmem", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
            }
            catch { }
        }
    }

    private void TryDelete(string fileName)
    {
        try
        {
            var path = ResolveFile(fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private string ResolveFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new InvalidOperationException("Worker shared image file name must be a simple file name.");
        var path = Path.GetFullPath(Path.Combine(RootPath, fileName));
        var prefix = RootPath.EndsWith(Path.DirectorySeparatorChar) ? RootPath : RootPath + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Worker shared image path escaped the configured shared-memory root.");
        return path;
    }

    private static bool TryGetPixelFormat(MatType type, out string format, out int bytesPerPixel)
    {
        if (type == MatType.CV_8UC1) { format = "8UC1"; bytesPerPixel = 1; return true; }
        if (type == MatType.CV_8UC3) { format = "8UC3"; bytesPerPixel = 3; return true; }
        if (type == MatType.CV_8UC4) { format = "8UC4"; bytesPerPixel = 4; return true; }
        if (type == MatType.CV_16UC1) { format = "16UC1"; bytesPerPixel = 2; return true; }
        if (type == MatType.CV_16UC3) { format = "16UC3"; bytesPerPixel = 6; return true; }
        if (type == MatType.CV_16UC4) { format = "16UC4"; bytesPerPixel = 8; return true; }
        if (type == MatType.CV_32FC1) { format = "32FC1"; bytesPerPixel = 4; return true; }
        if (type == MatType.CV_32FC3) { format = "32FC3"; bytesPerPixel = 12; return true; }
        if (type == MatType.CV_32FC4) { format = "32FC4"; bytesPerPixel = 16; return true; }
        format = ""; bytesPerPixel = 0; return false;
    }

    private static bool TryGetMatType(string format, out MatType type, out int bytesPerPixel)
    {
        switch (format)
        {
            case "8UC1": type = MatType.CV_8UC1; bytesPerPixel = 1; return true;
            case "8UC3": type = MatType.CV_8UC3; bytesPerPixel = 3; return true;
            case "8UC4": type = MatType.CV_8UC4; bytesPerPixel = 4; return true;
            case "16UC1": type = MatType.CV_16UC1; bytesPerPixel = 2; return true;
            case "16UC3": type = MatType.CV_16UC3; bytesPerPixel = 6; return true;
            case "16UC4": type = MatType.CV_16UC4; bytesPerPixel = 8; return true;
            case "32FC1": type = MatType.CV_32FC1; bytesPerPixel = 4; return true;
            case "32FC3": type = MatType.CV_32FC3; bytesPerPixel = 12; return true;
            case "32FC4": type = MatType.CV_32FC4; bytesPerPixel = 16; return true;
            default: type = default; bytesPerPixel = 0; return false;
        }
    }
}
