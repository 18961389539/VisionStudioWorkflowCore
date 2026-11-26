using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Services;
using Microsoft.Extensions.DependencyInjection;
using Viewer = ImageViewer.Controls.ImageViewer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                await VerifyAsync(args[0], args[1]);
                Console.WriteLine($"Package smoke passed on .NET {Environment.Version}.");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                app.Shutdown(1);
            }
        };
        return app.Run();
    }

    private static async Task VerifyAsync(string sessionPath, string csvPath)
    {
        string storageRoot = Path.Combine(Path.GetDirectoryName(csvPath)!, "session-storage");
        using ServiceProvider provider = new ServiceCollection()
            .AddSingleton<IImageViewerSessionStoragePolicy>(new LocalAppDataImageViewerSessionStoragePolicy(storageRoot, TimeSpan.FromSeconds(30)))
            .AddImageViewerHost()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();
        var viewer = scope.ServiceProvider.GetRequiredService<Viewer>();
        if (!await viewer.OpenProjectFromFileAsync(sessionPath) ||
            viewer.ImageSource is not BitmapSource { IsFrozen: true, PixelWidth: 512, PixelHeight: 512 } bitmap ||
            viewer.ViewerState.AllRois is not [LineMeasureRoi reference, LineMeasureRoi check] ||
            viewer.IsDirty)
        {
            throw new InvalidOperationException("The installed control did not load the demo image and its two measurement lines.");
        }

        double referenceLength = RoiInspectionEvaluator.Evaluate(reference, 1).MeasuredValue ?? 0;
        if (referenceLength != 100)
        {
            throw new InvalidOperationException("The demo reference line must be 100 px long.");
        }

        viewer.PixelSize = 50 / referenceLength;
        viewer.PhysicalUnit = "mm";
        check.Tolerance = new MeasurementTolerance { Nominal = 40, TolerancePlus = 0.5, ToleranceMinus = 0.5 };
        RoiInspectionResult result = RoiInspectionEvaluator.Evaluate(check, viewer.PixelSize);
        if (result.MeasuredValue != 40 || result.SpecificationStatus != RoiSpecificationStatus.Passed)
        {
            throw new InvalidOperationException("The calibrated check line must pass at 40 mm.");
        }

        var context = new RoiAnalysisExportContext(
            Path.Combine(Path.GetDirectoryName(sessionPath)!, "TestImage.png"), viewer.PluginRegistry);
        await RoiAnalysisExportService.SaveCsvAsync(
            csvPath, viewer.ViewerState.AllRois, bitmap, viewer.PixelSize, viewer.PhysicalUnit, viewer.Calibration, context);
        string[] lines = await File.ReadAllLinesAsync(csvPath);
        if (lines.Length != 3 || !lines[2].Contains("\"Passed\",40,", StringComparison.Ordinal) ||
            !File.Exists(Path.ChangeExtension(csvPath, ".metadata.json")))
        {
            throw new InvalidOperationException("The installed control did not export the expected measurements and metadata.");
        }
    }
}
