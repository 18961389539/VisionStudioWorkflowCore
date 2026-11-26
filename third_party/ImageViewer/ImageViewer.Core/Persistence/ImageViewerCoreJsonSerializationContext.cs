using System.Text.Json.Serialization;
using ImageViewer.Models;

namespace ImageViewer.Core.Persistence
{
    /// <summary>
    /// Source-generated JSON metadata for framework-neutral session and ROI schema types.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(ImageViewerSessionDocument))]
    [JsonSerializable(typeof(RoiDocument))]
    [JsonSerializable(typeof(MeasurementRecipeDocument))]
    [JsonSerializable(typeof(CameraCalibration))]
    [JsonSerializable(typeof(ImageAnalysisQualityProfile))]
    [JsonSerializable(typeof(ImageViewer.Core.Measurements.AdvancedMeasurementDefinition[]))]
    public partial class ImageViewerCoreJsonSerializationContext : JsonSerializerContext
    {
    }
}
