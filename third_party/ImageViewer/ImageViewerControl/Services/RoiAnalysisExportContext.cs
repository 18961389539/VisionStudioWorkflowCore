using System.Collections.Generic;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Services
{
    /// <summary>
    /// 分析结果导出的可复现上下文。CSV 保持简洁，完整输入写入同名 metadata JSON。
    /// </summary>
    public sealed record RoiAnalysisExportContext(
        string? SourcePath,
        RoiPluginRegistry? PluginRegistry,
        IReadOnlyDictionary<string, string>? RenderSettings = null,
        ImageAnalysisQualityProfile? QualityProfile = null);
}
