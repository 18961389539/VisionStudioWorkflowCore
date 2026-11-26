using System;
using System.Threading.Tasks;

namespace ImageViewer.Controls
{
    internal interface IImageViewerFileMenuCommandHost
    {
        Task ShowOpenImageDialogAsync();

        Task OpenRecentProjectAsync(string filePath);

        Task SaveRoisAsync();

        Task LoadRoisAsync();

        Task SaveSessionAsync();

        Task LoadSessionAsync();

        Task ExportProjectPackageAsync();

        Task SaveRecipeAsync();

        Task LoadRecipeAsync();

        Task LoadAlignedRecipeAsync();

        void CaptureRecipeDatumPoint(int index, bool currentImage);

        void ToggleAutoSave();

        void UpdateContextMenuState();
    }

    internal sealed class ImageViewerFileMenuCommandController : ImageViewerMenuCommandControllerBase<IImageViewerFileMenuCommandHost>
    {
        public ImageViewerFileMenuCommandController(IImageViewerFileMenuCommandHost host)
            : base(host, host.UpdateContextMenuState)
        {
        }

        public async Task ExecuteAsync(ImageViewerFileMenuCommand command)
        {
            switch (command)
            {
                case ImageViewerFileMenuCommand.OpenImage:
                    await Host.ShowOpenImageDialogAsync();
                    break;
                case ImageViewerFileMenuCommand.SaveRois:
                    await Host.SaveRoisAsync();
                    break;
                case ImageViewerFileMenuCommand.LoadRois:
                    await Host.LoadRoisAsync();
                    break;
                case ImageViewerFileMenuCommand.SaveSession:
                    await Host.SaveSessionAsync();
                    break;
                case ImageViewerFileMenuCommand.LoadSession:
                    await Host.LoadSessionAsync();
                    break;
                case ImageViewerFileMenuCommand.ExportProjectPackage:
                    await Host.ExportProjectPackageAsync();
                    break;
                case ImageViewerFileMenuCommand.SaveRecipe:
                    await Host.SaveRecipeAsync();
                    break;
                case ImageViewerFileMenuCommand.LoadRecipe:
                    await Host.LoadRecipeAsync();
                    break;
                case ImageViewerFileMenuCommand.CaptureRecipeReferenceDatum1:
                    Host.CaptureRecipeDatumPoint(0, currentImage: false);
                    break;
                case ImageViewerFileMenuCommand.CaptureRecipeReferenceDatum2:
                    Host.CaptureRecipeDatumPoint(1, currentImage: false);
                    break;
                case ImageViewerFileMenuCommand.CaptureCurrentAlignmentDatum1:
                    Host.CaptureRecipeDatumPoint(0, currentImage: true);
                    break;
                case ImageViewerFileMenuCommand.CaptureCurrentAlignmentDatum2:
                    Host.CaptureRecipeDatumPoint(1, currentImage: true);
                    break;
                case ImageViewerFileMenuCommand.LoadAlignedRecipe:
                    await Host.LoadAlignedRecipeAsync();
                    break;
                case ImageViewerFileMenuCommand.ToggleAutoSave:
                    Host.ToggleAutoSave();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }

            RefreshMenuState();
        }

        public async Task OpenRecentProjectAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return;
            }

            await Host.OpenRecentProjectAsync(filePath);
            RefreshMenuState();
        }
    }
}
