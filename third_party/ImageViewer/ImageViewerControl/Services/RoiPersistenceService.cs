using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Services
{
    public static class RoiPersistenceService
    {
        private const int CurrentDocumentVersion = 1;
        

        public static void SaveToFile(string filePath, IEnumerable<RoiBase> rois, double pixelSize, string? physicalUnit, RoiPluginRegistry pluginRegistry, ImageAnalysisQualityProfile? qualityProfile = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(rois);
            ArgumentNullException.ThrowIfNull(pluginRegistry);

            ImageViewerAtomicFile.WriteAllText(filePath, Serialize(rois, pixelSize, physicalUnit, pluginRegistry, qualityProfile));
        }

        public static Task SaveToFileAsync(string filePath, IEnumerable<RoiBase> rois, double pixelSize, string? physicalUnit, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
        {
            return SaveToFileAsync(filePath, rois, pixelSize, physicalUnit, pluginRegistry, qualityProfile: null, cancellationToken);
        }

        /// <summary>
        /// 异步保存 ROI 文档。
        /// Chinese: ROI→DTO 在调用线程（UI）完成，纯序列化与写盘放到后台——与会话保存保持同一约定，
        /// 避免在 UI 线程上把整份标注文档转成 JSON。
        /// English: The ROI-to-DTO conversion runs on the calling (UI) thread; serialization and the file write
        /// are offloaded, matching the session save convention.
        /// </summary>
        public static async Task SaveToFileAsync(string filePath, IEnumerable<RoiBase> rois, double pixelSize, string? physicalUnit, RoiPluginRegistry pluginRegistry, ImageAnalysisQualityProfile? qualityProfile, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(rois);
            ArgumentNullException.ThrowIfNull(pluginRegistry);

            RoiDocument document = CreateDocument(rois, pixelSize, physicalUnit, pluginRegistry, qualityProfile: qualityProfile);
            string json = await Task.Run(
                () => JsonSerializer.Serialize(document, ImageViewer.Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.RoiDocument),
                cancellationToken).ConfigureAwait(false);

            await ImageViewerAtomicFile.WriteAllTextAsync(
                filePath,
                json,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        public static string Serialize(IEnumerable<RoiBase> rois, double pixelSize, string? physicalUnit, RoiPluginRegistry pluginRegistry, ImageAnalysisQualityProfile? qualityProfile = null)
        {
            var roiPlugins = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));
            return JsonSerializer.Serialize(CreateDocument(rois, pixelSize, physicalUnit, roiPlugins, qualityProfile: qualityProfile), ImageViewer.Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.RoiDocument);
        }

        /// <summary>
        /// 构造 ROI 文档对象。
        /// Chinese: 供会话文档直接内嵌使用，避免把 ROI 文档再序列化成字符串造成 JSON 套 JSON；
        /// unresolvedItems 为加载时未能识别的载荷，原样附在文档尾部，保证缺插件时数据不被抹掉。
        /// English: Builds the ROI document object so session files can embed it directly instead of nesting JSON
        /// in a string. Unresolved payloads are appended verbatim so a missing plugin never erases data.
        /// </summary>
        internal static RoiDocument CreateDocument(
            IEnumerable<RoiBase> rois,
            double pixelSize,
            string? physicalUnit,
            RoiPluginRegistry pluginRegistry,
            IEnumerable<RoiPersistenceData>? unresolvedItems = null,
            ImageAnalysisQualityProfile? qualityProfile = null)
        {
            ArgumentNullException.ThrowIfNull(rois);
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            if (!double.IsFinite(pixelSize) || pixelSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pixelSize), pixelSize, "Pixel size must be a finite positive value.");
            }

            ImageAnalysisQualityProfile effectiveQualityProfile = qualityProfile ?? ImageAnalysisQualityProfile.Default;
            effectiveQualityProfile.Validate();
            return new RoiDocument
            {
                Version = CurrentDocumentVersion,
                PixelSize = pixelSize,
                PhysicalUnit = string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit,
                QualityProfile = effectiveQualityProfile,
                Items = rois
                    .Select(roi => CreateItem(roi, pluginRegistry))
                    .Concat(unresolvedItems ?? [])
                    .ToList()
            };
        }

        public static RoiDocumentLoadResult LoadFromFile(string filePath, RoiPluginRegistry pluginRegistry)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(pluginRegistry);

            return Deserialize(File.ReadAllText(filePath), pluginRegistry);
        }

        public static async Task<RoiDocumentLoadResult> LoadFromFileAsync(string filePath, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(pluginRegistry);

            return Deserialize(await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false), pluginRegistry);
        }

        public static RoiDocumentLoadResult Deserialize(string json, RoiPluginRegistry pluginRegistry)
        {
            ArgumentNullException.ThrowIfNull(json);
            var roiPlugins = pluginRegistry ?? throw new ArgumentNullException(nameof(pluginRegistry));

            var document = JsonSerializer.Deserialize(json, ImageViewer.Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.RoiDocument) ?? new RoiDocument();
            return CreateRois(document, roiPlugins);
        }

        /// <summary>
        /// 从 ROI 文档对象还原 ROI 集合。
        /// Chinese: 供会话文档直接内嵌使用；版本校验集中在这里，只保留一份。
        /// 无法识别的类型（插件缺失 / 类型键变更）不再静默丢弃，而是随结果带出，供调用方告警并在保存时原样保留。
        /// English: Restores ROIs from the ROI document object; version validation lives here only. Payloads with no
        /// registered plugin are returned as unresolved items instead of being dropped silently.
        /// </summary>
        internal static RoiDocumentLoadResult CreateRois(RoiDocument document, RoiPluginRegistry pluginRegistry)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(pluginRegistry);

            ValidateDocumentVersion(document.Version);

            var rois = new List<RoiBase>(document.Items.Count);
            var unresolvedItems = new List<RoiPersistenceData>();
            foreach (RoiPersistenceData item in document.Items)
            {
                RoiBase? roi = CreateRoi(item, pluginRegistry);
                if (roi == null)
                {
                    unresolvedItems.Add(item);
                    continue;
                }

                rois.Add(roi);
            }

            double pixelSize = document.PixelSize ?? 1.0;
            if (!double.IsFinite(pixelSize) || pixelSize <= 0)
            {
                throw new ArgumentException("ROI document PixelSize must be a finite positive value.", nameof(document));
            }

            return new RoiDocumentLoadResult(
                rois,
                unresolvedItems,
                pixelSize,
                string.IsNullOrWhiteSpace(document.PhysicalUnit) ? "px" : document.PhysicalUnit)
            {
                QualityProfile = document.QualityProfile
            };
        }

        /// <summary>
        /// 校验 ROI 文档版本。
        /// Chinese: 版本号高于当前支持版本时拒绝加载，避免用旧代码误读新格式；缺失或非正数视为早期文件，宽容接受。
        /// English: Rejects documents written by a newer format so old code never misreads a new layout.
        /// Missing or non-positive versions are treated as early files and accepted leniently.
        /// </summary>
        private static void ValidateDocumentVersion(int version)
        {
            if (version > CurrentDocumentVersion)
            {
                throw new NotSupportedException(
                    $"The ROI document version {version} is newer than the supported version {CurrentDocumentVersion}.");
            }
        }

        private static RoiPersistenceData CreateItem(RoiBase roi, RoiPluginRegistry roiPlugins)
        {
            var plugin = roiPlugins.FindByRoi(roi)
                ?? throw new InvalidOperationException($"No ROI plugin registered for type '{roi.GetType().FullName}'.");

            var item = new RoiPersistenceData();
            item.PopulateCommonState(roi, plugin.TypeKey);
            plugin.PopulatePersistenceData(roi, item);
            return item;
        }

        private static RoiBase? CreateRoi(RoiPersistenceData item, RoiPluginRegistry roiPlugins)
        {
            var plugin = ResolvePlugin(item, roiPlugins);
            if (plugin == null)
            {
                return null;
            }

            var roi = plugin.CreateRoi(item);
            roi.ApplyCommonState(item);
            return roi;
        }

        private static IRoiPlugin? ResolvePlugin(RoiPersistenceData item, RoiPluginRegistry roiPlugins)
        {
            if (string.IsNullOrWhiteSpace(item.Type))
            {
                return null;
            }

            // 稳定 TypeKey 优先。
            if (roiPlugins.FindByTypeKey(item.Type) is { } byStableKey)
            {
                return byStableKey;
            }

            // 迁移：v1 时代的旧文件把 CLR 类型名写进了 Type。只有“恰好一个已注册插件”的 CLR 类型名
            // 与之匹配时才迁移——两个插件同名（不同程序集/命名空间）时拒绝解析（进 unresolved 载荷），
            // 宁可让用户看到“未知标注”也不猜。
            IRoiPlugin[] legacyMatches = roiPlugins.Plugins
                .Where(plugin => string.Equals(plugin.RoiType.Name, item.Type, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return legacyMatches.Length == 1 ? legacyMatches[0] : null;
        }

    }

    /// <summary>
    /// ROI 文档加载结果。
    /// Chinese: 除解析成功的标注外，还带出未能识别的载荷（缺插件 / 类型键变更），
    /// 调用方据此告警并在保存时原样回写，避免"打开即丢"。
    /// English: Load result carrying both the parsed ROIs and the payloads no plugin could resolve, so callers can
    /// warn the operator and preserve them on the next save.
    /// </summary>
    public sealed record RoiDocumentLoadResult(
        IReadOnlyList<RoiBase> Rois,
        IReadOnlyList<RoiPersistenceData> UnresolvedItems,
        double PixelSize,
        string PhysicalUnit)
    {
        public ImageAnalysisQualityProfile? QualityProfile { get; init; }
    }
}
