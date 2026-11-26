using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Models;
using ImageViewer.Services;

internal static class Program
{
    private const int SliceCount = 24;
    private const int ImageSize = 4096;

    [STAThread]
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        string directory = Path.GetFullPath(args[1]);
        if (args[0] == "prepare")
        {
            Prepare(directory);
            return 0;
        }

        string[] paths = Enumerable.Range(0, SliceCount).Select(index => GetInputPath(directory, index)).ToArray();
        string output = Path.Combine(directory, "measurements.csv");
        using var process = Process.GetCurrentProcess();
        long initialBytes = process.PrivateMemorySize64;
        long peakBytes = initialBytes;
        var timer = Stopwatch.StartNew();
        Task<BatchRoiAnalysisExportSummary> task = BatchRoiAnalysisExportService.ExportAsync(
            output, paths, [new CircleRoi { Center = new PointD(1500, 1500), Radius = 32, Label = "sample" }], 1, "px");
        while (!task.IsCompleted)
        {
            process.Refresh();
            peakBytes = Math.Max(peakBytes, process.PrivateMemorySize64);
            Thread.Sleep(5);
        }

        BatchRoiAnalysisExportSummary summary = task.GetAwaiter().GetResult();
        timer.Stop();
        process.Refresh();
        peakBytes = Math.Max(peakBytes, process.PrivateMemorySize64);
        string normalizedCsv = File.ReadAllText(output)
            .Replace(directory + Path.DirectorySeparatorChar, "<fixture>/", StringComparison.Ordinal)
            .ReplaceLineEndings("\n");
        string csvSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCsv)));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            imageCount = SliceCount,
            imageSize = ImageSize,
            processed = summary.ProcessedFileCount,
            rows = summary.ExportedRowCount,
            initialBytes,
            peakBytes,
            peakGrowthBytes = peakBytes - initialBytes,
            elapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            csvSha256
        }));
        return summary.ProcessedFileCount == SliceCount && summary.ExportedRowCount == SliceCount ? 0 : 1;
    }

    private static string GetInputPath(string directory, int index) => Path.Combine(directory, $"{index:D3}.png");

    private static void Prepare(string directory)
    {
        Directory.CreateDirectory(directory);
        var pixels = new byte[ImageSize * ImageSize];
        Array.Fill(pixels, (byte)96);
        BitmapSource source = BitmapSource.Create(ImageSize, ImageSize, 96, 96, PixelFormats.Gray8, null, pixels, ImageSize);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        string firstPath = GetInputPath(directory, 0);
        using (FileStream stream = File.Create(firstPath))
        {
            encoder.Save(stream);
        }
        for (int index = 1; index < SliceCount; index++)
        {
            File.Copy(firstPath, GetInputPath(directory, index));
        }
    }
}
