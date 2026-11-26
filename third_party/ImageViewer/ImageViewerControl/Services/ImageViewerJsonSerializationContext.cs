using System.Collections.Generic;
using System.Text.Json.Serialization;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Services
{
    // 注意：禁止把 GenerationMode 改为 Serialization（仅快路径）。
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(List<RecentImageViewerProject>))]
    [JsonSerializable(typeof(RoiPluginDiscoveryOptions))]
    internal partial class ImageViewerJsonSerializationContext : JsonSerializerContext
    {
    }
}
