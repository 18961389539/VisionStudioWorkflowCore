using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Measurements;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Plugins;
using Xunit;

namespace ImageViewerControl.Tests;

public class AdvancedMeasurementExportServiceTests
{
    [Fact]
    public void CsvRetainsFailuresSamplesAndEscapesLabels()
    {
        var definition = new AdvancedMeasurementDefinition { Name = "=1+1,\"label\"", Kind = AdvancedMeasurementKind.GapProfile };
        var result = new AdvancedMeasurementEvaluation
        {
            Id = definition.Id, Success = true, Metrics = [new("Minimum", 2, Unit: "mm")], Samples = [2, 3], Stations = [0, 1]
        };
        var failed = new AdvancedMeasurementEvaluation { Id = Guid.NewGuid(), Error = "Missing source" };
        string csv = AdvancedMeasurementExportService.BuildCsv([definition], [result, failed]);
        Assert.Contains("'=1+1,\"\"label\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("Missing source", csv, StringComparison.Ordinal);
        Assert.Equal(6, csv.Split("\r\n", StringSplitOptions.None).Length);
        Assert.Contains("\"Sample\",\"3\",\"mm\",\"1\",\"1\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportIncludesDefinitionsSourceIdentityAndHash()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ImageViewerAdvancedExport-" + Guid.NewGuid());
        try
        {
            var first = new CircleRoi { Center = new(0, 0), Radius = 2 };
            var second = new CircleRoi { Center = new(10, 0), Radius = 2 };
            first.AdvancedMeasurements = [new() { Name = "gap", Kind = AdvancedMeasurementKind.ShortestDistance, Sources = [first.MeasurementId, second.MeasurementId] }];
            string path = Path.Combine(directory, "measurements.csv");
            await AdvancedMeasurementExportService.SaveAsync(path, [first, second], 0.5, "mm");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(path, ".metadata.json")));
            Assert.Equal(3, json.RootElement.GetProperty("results")[0].GetProperty("Metrics")[0].GetProperty("Value").GetDouble());
            Assert.Equal(first.MeasurementId, json.RootElement.GetProperty("definitions")[0].GetProperty("Sources")[0].GetGuid());
            Assert.Equal(64, json.RootElement.GetProperty("csvSha256").GetString()!.Length);
            Assert.Equal(2, json.RootElement.GetProperty("roiDocument").GetProperty("Items").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ExportCapturesMutableInputsAndUsesTheHostPluginRegistry()
    {
        WpfTestRunner.RunAsync(async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "ImageViewerAdvancedExport-" + Guid.NewGuid());
            try
            {
                var builtInCircle = RoiPluginRegistry.CreateBuiltIn().FindByRoi(new CircleRoi())!;
                var registry = RoiPluginRegistry.CreateBuiltIn();
                int callerThread = Environment.CurrentManagedThreadId;
                registry.Register(new RoiPlugin<CustomCircleRoi>(
                    typeKey: "custom-circle",
                    hitTestOrder: 0,
                    getCollection: _ => new List<CustomCircleRoi>(),
                    drawingTools: null,
                    behavior: builtInCircle.Behavior,
                    renderer: builtInCircle.Renderer,
                    createRoi: _ => new CustomCircleRoi(),
                    populatePersistenceData: (roi, data) =>
                    {
                        Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
                        builtInCircle.PopulatePersistenceData(roi, data);
                    }));
                var first = new CustomCircleRoi { Center = new(0, 0), Radius = 2 };
                var second = new CircleRoi { Center = new(10, 0), Radius = 2 };
                var definition = new AdvancedMeasurementDefinition
                {
                    Name = "original", Kind = AdvancedMeasurementKind.ShortestDistance,
                    Sources = [first.MeasurementId, second.MeasurementId]
                };
                first.AdvancedMeasurements = [definition];
                var calibration = new CameraCalibration { NormalizationRadius = 100, CalibrationId = "captured" };
                var workpiece = new WorkpieceCoordinateSystem { Origin = new(3, 4), AxisX = new(0, 1) };
                var bitmap = new WriteableBitmap(2, 2, 96, 96, PixelFormats.Gray8, null);
                byte[] initialPixels = [1, 2, 3, 4];
                bitmap.WritePixels(new Int32Rect(0, 0, 2, 2), initialPixels, 2, 0);
                string path = Path.Combine(directory, "captured.csv");

                Task export = AdvancedMeasurementExportService.SaveAsync(path, registry, [first, second],
                    0.5, "mm", calibration, bitmap, workpiece: workpiece);
                first.Center = new(100, 0);
                first.AdvancedMeasurements[0].Sources[0] = Guid.NewGuid();
                calibration.K1 = 0.1;
                calibration.CalibrationId = "edited";
                bitmap.WritePixels(new Int32Rect(0, 0, 2, 2), new byte[4], 2, 0);
                await export;

                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(path, ".metadata.json")));
                JsonElement metadata = json.RootElement;
                Assert.Equal(3, metadata.GetProperty("results")[0].GetProperty("Metrics")[0].GetProperty("Value").GetDouble());
                Assert.Equal("custom-circle", metadata.GetProperty("roiDocument").GetProperty("Items")[0].GetProperty("Type").GetString());
                Assert.Equal(0, metadata.GetProperty("roiDocument").GetProperty("Items")[0].GetProperty("Center").GetProperty("X").GetDouble());
                Assert.Equal(0, metadata.GetProperty("calibration").GetProperty("K1").GetDouble());
                Assert.Equal("captured", metadata.GetProperty("calibration").GetProperty("CalibrationId").GetString());
                Assert.Equal(3, metadata.GetProperty("workpiece").GetProperty("Origin").GetProperty("X").GetDouble());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(initialPixels)).ToLowerInvariant(),
                    metadata.GetProperty("image").GetProperty("pixelBufferSha256").GetString());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(),
                    metadata.GetProperty("csvSha256").GetString());
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact]
    public async Task CanceledExportPreservesExistingArtifacts()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ImageViewerAdvancedExport-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "results.csv");
            string metadataPath = Path.ChangeExtension(path, ".metadata.json");
            await File.WriteAllTextAsync(path, "existing results");
            await File.WriteAllTextAsync(metadataPath, "existing metadata");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AdvancedMeasurementExportService.SaveAsync(
                path, RoiPluginRegistry.CreateBuiltIn(), [], 1, "px", cancellationToken: new CancellationToken(true)));

            Assert.Equal("existing results", await File.ReadAllTextAsync(path));
            Assert.Equal("existing metadata", await File.ReadAllTextAsync(metadataPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CustomCircleRoi : CircleRoi;

    [Fact]
    public void CsvIncludesConstructionsAndKeepsSignedNumbersNumeric()
    {
        var construction = new AdvancedMeasurementDefinition { Name = "midpoint", Kind = AdvancedMeasurementKind.Midpoint };
        var distance = new AdvancedMeasurementDefinition { Kind = AdvancedMeasurementKind.ProjectedDistance };
        var constructed = new AdvancedMeasurementEvaluation { Id = construction.Id, Success = true, Geometry = [new(1, 2)] };
        var projected = new AdvancedMeasurementEvaluation { Id = distance.Id, Success = true, Metrics = [new("Distance", -2.5, Unit: "mm")] };
        string csv = AdvancedMeasurementExportService.BuildCsv([construction, distance], [constructed, projected]);
        Assert.Contains("\"Construction\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"-2.5\"", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("\"'-2.5\"", csv, StringComparison.Ordinal);
    }
}
