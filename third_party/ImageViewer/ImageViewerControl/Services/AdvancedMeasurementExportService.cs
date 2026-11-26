using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Measurements;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Persistence;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Services;

/// <summary>Exports associative measurements, samples and complete reproducible inputs.</summary>
public static class AdvancedMeasurementExportService
{
    private static readonly JsonSerializerOptions MetadataOptions = new() { WriteIndented = true };
    /// <summary>Writes CSV and metadata together with rollback protection.</summary>
    public static Task SaveAsync(string path, IReadOnlyList<RoiBase> rois, double pixelSize, string unit,
        CameraCalibration? calibration = null, BitmapSource? bitmap = null, string? sourceImagePath = null,
        WorkpieceCoordinateSystem? workpiece = null)
        => SaveAsync(path, RoiPluginRegistry.CreateBuiltIn(), rois, pixelSize, unit,
            calibration, bitmap, sourceImagePath, workpiece);

    /// <summary>Captures inputs on the caller's thread, then evaluates and exports on a background thread.</summary>
    public static async Task SaveAsync(string path, RoiPluginRegistry pluginRegistry,
        IReadOnlyList<RoiBase> rois, double pixelSize, string unit,
        CameraCalibration? calibration = null, BitmapSource? bitmap = null, string? sourceImagePath = null,
        WorkpieceCoordinateSystem? workpiece = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(pluginRegistry);
        ArgumentNullException.ThrowIfNull(rois);
        cancellationToken.ThrowIfCancellationRequested();
        RoiDocument roiDocument = RoiPersistenceService.CreateDocument(rois, pixelSize, unit, pluginRegistry);
        RoiBase[] capturedRois = RoiAnalysisExportService.CaptureRois(rois);
        BitmapSource? capturedBitmap = bitmap == null || bitmap.IsFrozen
            ? bitmap : (BitmapSource)bitmap.GetCurrentValueAsFrozen();
        CameraCalibration? capturedCalibration = calibration?.Clone();
        string fullPath = Path.GetFullPath(path);
        await Task.Run(() => SaveSnapshotAsync(fullPath, capturedRois, roiDocument, pixelSize, unit,
            capturedCalibration, capturedBitmap, sourceImagePath, workpiece, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveSnapshotAsync(string path, IReadOnlyList<RoiBase> rois, RoiDocument roiDocument,
        double pixelSize, string unit, CameraCalibration? calibration, BitmapSource? bitmap,
        string? sourceImagePath, WorkpieceCoordinateSystem? workpiece, CancellationToken cancellationToken)
    {
        var definitions = AdvancedMeasurementService.EnumerateDefinitions(rois).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var results = AdvancedMeasurementService.EvaluateAll(rois, pixelSize, unit, calibration, bitmap, workpiece).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        string csv = BuildCsv(definitions, results);
        string roiJson = JsonSerializer.Serialize(roiDocument, ImageViewerCoreJsonSerializationContext.Default.RoiDocument);
        string? imageHash = bitmap == null ? null : ImageViewerPixelAccess.ComputePixelBufferSha256(bitmap, cancellationToken);
        var metadata = new
        {
            schemaVersion = 1,
            exportedAtUtc = DateTimeOffset.UtcNow,
            softwareVersion = typeof(AdvancedMeasurementExportService).Assembly.GetName().Version?.ToString(),
            sourceImagePath,
            image = bitmap == null ? null : new { bitmap.PixelWidth, bitmap.PixelHeight, format = bitmap.Format.ToString(), pixelBufferSha256 = imageHash },
            pixelSize,
            unit,
            calibration,
            workpiece,
            roiDocument = JsonSerializer.Deserialize<JsonElement>(roiJson),
            definitions,
            results,
            csvSha256 = ImageViewerExportArtifactSet.ComputeSha256Utf8Text(csv)
        };
        string json = JsonSerializer.Serialize(metadata, MetadataOptions);
        await ImageViewerExportArtifactSet.WriteAllTextAsync(new Dictionary<string, string>
        {
            [path] = csv,
            [Path.ChangeExtension(path, ".metadata.json")] = json
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Generates invariant CSV including failures and every profile sample.</summary>
    public static string BuildCsv(IReadOnlyList<AdvancedMeasurementDefinition> definitions, IReadOnlyList<AdvancedMeasurementEvaluation> results)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(results);
        var names = definitions.GroupBy(definition => definition.Id).ToDictionary(group => group.Key, group => group.First());
        var builder = new StringBuilder("Id,Name,Operation,Success,WithinLimits,Metric,Value,Unit,SampleIndex,Station,Method,Error\r\n");
        foreach (var result in results)
        {
            names.TryGetValue(result.Id, out var definition);
            if (!result.Success) Write(result, definition, string.Empty, null, string.Empty, null, null);
            if (result.Success && result.Metrics.Length == 0 && result.Samples.Length == 0)
                Write(result, definition, "Construction", null, string.Empty, null, null);
            foreach (var metric in result.Metrics) Write(result, definition, metric.Name, metric.Value, metric.Unit ?? string.Empty, null, null);
            for (int i = 0; i < result.Samples.Length; i++)
                Write(result, definition, "Sample", result.Samples[i], result.Metrics.FirstOrDefault()?.Unit ?? string.Empty, i,
                    i < result.Stations.Length ? result.Stations[i] : null);
        }
        return builder.ToString();

        void Write(AdvancedMeasurementEvaluation result, AdvancedMeasurementDefinition? definition, string metric, double? value, string metricUnit, int? index, double? station)
        {
            string[] columns = [result.Id.ToString(), definition?.Name ?? string.Empty, definition?.Kind.ToString() ?? string.Empty,
                result.Success.ToString(CultureInfo.InvariantCulture), result.WithinLimits?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                metric, Number(value), metricUnit, index?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, Number(station), result.Method, result.Error];
            builder.AppendJoin(',', columns.Select(Escape)).Append("\r\n");
        }
    }

    private static string Number(double? value) => value.HasValue && double.IsFinite(value.Value) ? value.Value.ToString("G17", CultureInfo.InvariantCulture) : string.Empty;
    private static string Escape(string value)
    {
        // Prevent spreadsheet formula execution in user-supplied labels.
        if (value.Length > 0 && "=+@-\t\r".Contains(value[0], StringComparison.Ordinal) &&
            !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) value = "'" + value;
        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }
}
