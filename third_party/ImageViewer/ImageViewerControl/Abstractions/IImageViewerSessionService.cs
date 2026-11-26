using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Plugins;
using ImageViewer.Services;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerSessionService
    {
        void SaveToFile(string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry);

        Task SaveToFileAsync(string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default);

        /// <summary>Saves a recovery snapshot that may later be removed by the autosave controller.</summary>
        Task SaveRecoveryToFileAsync(string filePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default)
            => SaveToFileAsync(filePath, snapshot, pluginRegistry, cancellationToken);

        string SerializeSession(string? sessionName, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry);

        ImageViewerSessionData LoadFromFile(string filePath, RoiPluginRegistry pluginRegistry);

        Task<ImageViewerSessionData> LoadFromFileAsync(string filePath, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default);

        ImageViewerSessionData LoadFromJson(string sessionJson, RoiPluginRegistry pluginRegistry, string? sessionBaseDirectory = null);
    }
}
