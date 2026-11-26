using System.Threading;
using System.Threading.Tasks;
using ImageViewer.Plugins;
using ImageViewer.Services;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerProjectPackageService
    {
        Task ExportAsync(string packagePath, ImageViewerPersistenceSnapshot snapshot, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default);

        Task<ImageViewerSessionData> LoadAsync(string packagePath, RoiPluginRegistry pluginRegistry, CancellationToken cancellationToken = default);
    }
}
