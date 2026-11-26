using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Abstractions;
using ImageViewer.Core.Display;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Persistence;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Services
{
    public sealed class ImageViewerSessionService : IImageViewerSessionService
    {
        // 版本 1 = ROI 载荷以转义字符串内嵌；版本 2 = ROI 载荷为嵌套对象。
        // Version 1 = ROI payload embedded as an escaped string; version 2 = ROI payload is a nested object.
        private const int CurrentSessionVersion = 2;

        public void SaveToFile(string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry)
            => SaveToFileCoreAsync(filePath, snapshot, pluginRegistry, standalone: true, default).GetAwaiter().GetResult();

        /// <summary>
        /// 异步保存会话。
        /// Chinese: 序列化本身也在后台线程执行——快照里的 ROI 载荷已在 UI 线程构建成脱离 UI 的纯数据图，
        /// 因此这里可以安全 offload，避免在 UI 线程上把整份标注文档转成 JSON。
        /// English: Serialization runs off the UI thread too. The snapshot already carries a detached ROI payload
        /// built on the UI thread, so offloading here cannot race the editor.
        /// </summary>
        public Task SaveToFileAsync(string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
            => SaveToFileCoreAsync(filePath, snapshot, pluginRegistry, standalone: true, cancellationToken);

        Task IImageViewerSessionService.SaveRecoveryToFileAsync(
            string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken)
            => SaveToFileCoreAsync(filePath, snapshot, pluginRegistry, standalone: false, cancellationToken);

        private async Task SaveToFileCoreAsync(string filePath, ImageViewerPersistenceSnapshot snapshot,
            RoiPluginRegistry pluginRegistry, bool standalone, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(pluginRegistry);

            using FileStream? assetLease = await Task.Run(
                () => ImageViewerProjectPackageCache.TryLeaseAsset(snapshot.ImagePath, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            string? recoveryImage = null;
            bool createdRecoveryImage = false;
            try
            {
                if (standalone)
                {
                    snapshot = await PrepareStandaloneSnapshotAsync(filePath, snapshot, assetLease != null, cancellationToken).ConfigureAwait(false);
                }
                else if (snapshot.InMemoryImage is { } bitmap)
                {
                    string sha256;
                    (recoveryImage, createdRecoveryImage, sha256) = await Task.Run(
                        () => ImageViewerSnapshotImage.SaveRecoveryImageAsync(filePath, bitmap, cancellationToken), cancellationToken).ConfigureAwait(false);
                    snapshot = snapshot with { ImagePath = recoveryImage, InMemoryImage = null, ImageSha256 = sha256 };
                }
                else if (snapshot.ImageSha256 == null)
                {
                    snapshot = snapshot with { ImageSha256 = await ReadSourceSha256Async(filePath, snapshot.ImagePath, cancellationToken).ConfigureAwait(false) };
                }

                string sessionName = Path.GetFileNameWithoutExtension(filePath);
                string sessionJson = await Task.Run(
                    () => SerializeSession(sessionName, snapshot, pluginRegistry),
                    cancellationToken).ConfigureAwait(false);
                await ImageViewerAtomicFile.WriteAllTextAsync(filePath, sessionJson, cancellationToken: cancellationToken).ConfigureAwait(false);
                createdRecoveryImage = false; // The committed session owns its sidecar from here on.
                if (!standalone)
                {
                    ImageViewerSnapshotImage.PruneRecoveryImages(filePath, snapshot.ImagePath);
                }
            }
            finally
            {
                if (createdRecoveryImage && recoveryImage != null)
                {
                    string directory = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
                    ImageViewerAtomicFile.TryDelete(Path.Combine(directory, recoveryImage));
                }
            }
        }

        private static async Task<ImageViewerPersistenceSnapshot> PrepareStandaloneSnapshotAsync(
            string sessionPath, ImageViewerPersistenceSnapshot snapshot, bool isCachedAsset, CancellationToken cancellationToken)
        {
            if (snapshot.InMemoryImage is { } bitmap)
            {
                string temporary = ImageViewerAtomicFile.CreateTemporaryPath(sessionPath) + ".png";
                try
                {
                    await Task.Run(() => ImageViewerSnapshotImage.WritePng(temporary, bitmap, cancellationToken), cancellationToken).ConfigureAwait(false);
                    string relative = await ImageViewerSessionAssets.SaveAsync(sessionPath, temporary, expectedSha256: null, cancellationToken).ConfigureAwait(false);
                    return snapshot with { ImagePath = relative, InMemoryImage = null, ImageSha256 = Path.GetFileNameWithoutExtension(relative) };
                }
                finally
                {
                    ImageViewerAtomicFile.TryDelete(temporary);
                }
            }

            if (snapshot.ImagePath is not { } source ||
                (!isCachedAsset && !ImageViewerSessionAssets.IsManagedAsset(source) && !ImageViewerSnapshotImage.IsRecoveryAsset(source)))
            {
                string? sha256 = await ReadSourceSha256Async(sessionPath, snapshot.ImagePath, cancellationToken).ConfigureAwait(false);
                if (snapshot.ImageSha256 != null)
                {
                    if (sha256 == null)
                    {
                        throw new FileNotFoundException("The original image is missing.", snapshot.ImagePath);
                    }
                    ImageViewerSessionImageIdentity.Verify(snapshot.ImageSha256, sha256, snapshot.ImagePath!);
                }
                return snapshot with { ImageSha256 = sha256 };
            }

            string relativePath = await ImageViewerSessionAssets.SaveAsync(sessionPath, source, snapshot.ImageSha256, cancellationToken).ConfigureAwait(false);
            return snapshot with { ImagePath = relativePath, ImageSha256 = Path.GetFileNameWithoutExtension(relativePath) };
        }

        private static Task<string?> ReadSourceSha256Async(string sessionPath, string? imagePath, CancellationToken cancellationToken)
            => ImageViewerSessionImageIdentity.TryReadAsync(
                ResolveImagePath(imagePath, Path.GetDirectoryName(Path.GetFullPath(sessionPath))), cancellationToken);

        /// <summary>
        /// 把快照映射成会话文档并序列化。
        /// Chinese: 只做字段拷贝，不再构造 ROI 文档、也不再访问 <see cref="RoiBase"/> 或插件注册表；
        /// <paramref name="pluginRegistry"/> 保留用于 API 兼容与空值校验（ROI→DTO 的解析已在 CaptureSnapshot 完成）。
        /// English: Pure field mapping; it no longer builds the ROI document or touches <see cref="RoiBase"/>.
        /// The registry parameter is kept for API compatibility and null checking only.
        /// </summary>
        public string SerializeSession(string? sessionName, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            if (snapshot.InMemoryImage != null)
            {
                throw new ArgumentException("Save the snapshot to a session or project package before serializing an in-memory image.", nameof(snapshot));
            }

            var session = new ImageViewerSessionDocument
            {
                Version = CurrentSessionVersion,
                SessionName = sessionName,
                SavedAtUtc = DateTimeOffset.UtcNow,
                ImagePath = snapshot.ImagePath,
                ImageSha256 = ImageViewerSessionImageIdentity.Normalize(snapshot.ImageSha256, snapshot.ImagePath),
                RoiDocument = snapshot.RoiDocument,
                Scale = snapshot.Scale,
                TranslateX = snapshot.TranslateX,
                TranslateY = snapshot.TranslateY,
                Calibration = snapshot.Calibration,
                CoordinateSystem = snapshot.CoordinateSystem,
                ReferenceDatumPoints = CloneDatumPoints(snapshot.ReferenceDatumPoints),
                CurrentDatumPoints = CloneDatumPoints(snapshot.CurrentDatumPoints),
                Display = snapshot.Display
            };

            return JsonSerializer.Serialize(session, ImageViewer.Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.ImageViewerSessionDocument);
        }

        public ImageViewerSessionData LoadFromFile(string filePath, RoiPluginRegistry pluginRegistry)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(pluginRegistry);

            return LoadFromJson(File.ReadAllText(filePath), pluginRegistry, Path.GetDirectoryName(Path.GetFullPath(filePath)));
        }

        public async Task<ImageViewerSessionData> LoadFromFileAsync(string filePath, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(pluginRegistry);

            return LoadFromJson(await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false), pluginRegistry, Path.GetDirectoryName(Path.GetFullPath(filePath)));
        }

        public ImageViewerSessionData LoadFromJson(string sessionJson, RoiPluginRegistry pluginRegistry, string? sessionBaseDirectory = null)
        {
            ArgumentNullException.ThrowIfNull(pluginRegistry);
            var session = JsonSerializer.Deserialize(sessionJson, ImageViewer.Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.ImageViewerSessionDocument)
                ?? new ImageViewerSessionDocument();
            ValidateSessionVersion(session.Version);
            var roiData = RoiPersistenceService.CreateRois(session.RoiDocument ?? new RoiDocument(), pluginRegistry);
            ImageAnalysisQualityProfile qualityProfile = roiData.QualityProfile ?? ImageAnalysisQualityProfile.Default;
            qualityProfile.Validate();
            return new ImageViewerSessionData(session.SessionName, session.SavedAtUtc, ResolveImagePath(session.ImagePath, sessionBaseDirectory), roiData.Rois, roiData.PixelSize, roiData.PhysicalUnit, session.Scale, session.TranslateX, session.TranslateY, session.Calibration)
            {
                UnresolvedRois = roiData.UnresolvedItems,
                QualityProfile = qualityProfile,
                CoordinateSystem = session.CoordinateSystem,
                ReferenceDatumPoints = CloneDatumPoints(session.ReferenceDatumPoints),
                CurrentDatumPoints = CloneDatumPoints(session.CurrentDatumPoints),
                ImageSha256 = ImageViewerSessionImageIdentity.Normalize(session.ImageSha256, session.ImagePath),
                Display = session.Display is { } display && display.TryValidate(out _)
                    ? display
                    : DisplayAdjustment.Identity
            };
        }

        /// <summary>
        /// 校验会话文档版本。
        /// Chinese: 版本号高于当前支持版本时拒绝加载，避免用旧代码误读新格式；缺失或非正数视为早期文件，宽容接受。
        /// English: Rejects session files written by a newer format so old code never misreads a new layout.
        /// Missing or non-positive versions are treated as early files and accepted leniently.
        /// </summary>
        private static void ValidateSessionVersion(int version)
        {
            if (version > CurrentSessionVersion)
            {
                throw new NotSupportedException(
                    $"The session document version {version} is newer than the supported version {CurrentSessionVersion}.");
            }
        }

        internal static List<RecipeDatumPoint> CloneDatumPoints(IReadOnlyList<RecipeDatumPoint>? points)
        {
            if (points == null)
            {
                return [];
            }
            if (points.Count > 2)
            {
                throw new InvalidDataException("A session can contain at most two datum points in each set.");
            }

            var copy = new List<RecipeDatumPoint>(points.Count);
            foreach (RecipeDatumPoint point in points)
            {
                if (point == null || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
                {
                    throw new InvalidDataException("Session datum points must contain finite image coordinates.");
                }
                copy.Add(new RecipeDatumPoint { Name = point.Name, X = point.X, Y = point.Y });
            }
            return copy;
        }

        private static string? ResolveImagePath(string? imagePath, string? sessionBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(sessionBaseDirectory) || Path.IsPathRooted(imagePath))
            {
                return imagePath;
            }

            return Path.GetFullPath(Path.Combine(sessionBaseDirectory, imagePath));
        }

    }

    public sealed record ImageViewerSessionData(
        string? SessionName,
        DateTimeOffset SavedAtUtc,
        string? ImagePath,
        IReadOnlyList<RoiBase> Rois,
        double PixelSize,
        string PhysicalUnit,
        double Scale,
        double TranslateX,
        double TranslateY,
        CameraCalibration? Calibration)
    {
        /// <summary>Optional SHA-256 to verify before applying this session's measurements to an image.</summary>
        public string? ImageSha256 { get; init; }

        public WorkpieceCoordinateSystem? CoordinateSystem { get; init; }

        public IReadOnlyList<RecipeDatumPoint> ReferenceDatumPoints { get; init; } = [];

        public IReadOnlyList<RecipeDatumPoint> CurrentDatumPoints { get; init; } = [];

        /// <summary>
        /// 加载时未能解析的 ROI 载荷。
        /// Chinese: 缺插件 / 类型键变更时不为空；随快照流回保存路径，避免数据丢失。
        /// English: ROI payloads that no registered plugin could resolve on load; carried back into saves.
        /// </summary>
        public IReadOnlyList<RoiPersistenceData> UnresolvedRois { get; init; } = [];

        public ImageAnalysisQualityProfile QualityProfile { get; init; } = ImageAnalysisQualityProfile.Default;

        /// <summary>会话里保存的显示调节；旧文件按默认参数（透传）处理。</summary>
        public DisplayAdjustment Display { get; init; } = DisplayAdjustment.Identity;
    }
}
