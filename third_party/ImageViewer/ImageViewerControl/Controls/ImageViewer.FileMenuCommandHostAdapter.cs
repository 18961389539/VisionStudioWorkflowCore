using System;
using System.Threading.Tasks;

namespace ImageViewer.Controls
{
    internal interface IImageViewerFileDialogCapability
    {
        Task ShowOpenImageDialogAsync();
    }

    internal interface IImageViewerFileOperationsCapability
    {
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
    }

    internal sealed class ImageViewerFileMenuCommandHostAdapter : IImageViewerFileMenuCommandHost
    {
        private readonly IImageViewerFileDialogCapability _dialog;
        private readonly IImageViewerFileOperationsCapability _operations;
        private readonly IImageViewerContextMenuCapability _contextMenu;

        public ImageViewerFileMenuCommandHostAdapter(
            IImageViewerFileDialogCapability dialog,
            IImageViewerFileOperationsCapability operations,
            IImageViewerContextMenuCapability contextMenu)
        {
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _operations = operations ?? throw new ArgumentNullException(nameof(operations));
            _contextMenu = contextMenu ?? throw new ArgumentNullException(nameof(contextMenu));
        }

        public Task ShowOpenImageDialogAsync() => _dialog.ShowOpenImageDialogAsync();

        public Task OpenRecentProjectAsync(string filePath) => _operations.OpenRecentProjectAsync(filePath);

        public Task SaveRoisAsync() => _operations.SaveRoisAsync();

        public Task LoadRoisAsync() => _operations.LoadRoisAsync();

        public Task SaveSessionAsync() => _operations.SaveSessionAsync();

        public Task LoadSessionAsync() => _operations.LoadSessionAsync();

        public Task ExportProjectPackageAsync() => _operations.ExportProjectPackageAsync();

        public Task SaveRecipeAsync() => _operations.SaveRecipeAsync();

        public Task LoadRecipeAsync() => _operations.LoadRecipeAsync();
        public Task LoadAlignedRecipeAsync() => _operations.LoadAlignedRecipeAsync();
        public void CaptureRecipeDatumPoint(int index, bool currentImage) => _operations.CaptureRecipeDatumPoint(index, currentImage);

        public void ToggleAutoSave() => _operations.ToggleAutoSave();

        public void UpdateContextMenuState() => _contextMenu.UpdateContextMenuState();
    }

    internal sealed class ImageViewerFileDialogCapability : IImageViewerFileDialogCapability
    {
        private readonly Func<Task> _showOpenImageDialogAsync;

        public ImageViewerFileDialogCapability(Func<Task> showOpenImageDialogAsync) => _showOpenImageDialogAsync = showOpenImageDialogAsync ?? throw new ArgumentNullException(nameof(showOpenImageDialogAsync));

        public Task ShowOpenImageDialogAsync() => _showOpenImageDialogAsync();
    }

    internal sealed class ImageViewerFileOperationsCapability : IImageViewerFileOperationsCapability
    {
        private readonly ImageViewerSessionController _sessionController;
        private readonly ImageViewerRoiPersistenceController _roiPersistenceController;

        public ImageViewerFileOperationsCapability(
            ImageViewerSessionController sessionController,
            ImageViewerRoiPersistenceController roiPersistenceController)
        {
            _sessionController = sessionController ?? throw new ArgumentNullException(nameof(sessionController));
            _roiPersistenceController = roiPersistenceController ?? throw new ArgumentNullException(nameof(roiPersistenceController));
        }

        public Task OpenRecentProjectAsync(string filePath) => _sessionController.OpenRecentProjectAsync(filePath);
        public Task SaveRoisAsync() => _roiPersistenceController.SaveRoisAsync();
        public Task LoadRoisAsync() => _roiPersistenceController.LoadRoisAsync();
        public Task SaveSessionAsync() => _sessionController.SaveSessionAsync();
        public Task LoadSessionAsync() => _sessionController.LoadProjectAsync();
        public Task ExportProjectPackageAsync() => _sessionController.ExportProjectPackageAsync();
        public Task SaveRecipeAsync() => _sessionController.SaveRecipeAsync();
        public Task LoadRecipeAsync() => _sessionController.LoadRecipeAsync();
        public Task LoadAlignedRecipeAsync() => _sessionController.LoadAlignedRecipeAsync();
        public void CaptureRecipeDatumPoint(int index, bool currentImage) => _sessionController.CaptureRecipeDatumPoint(index, currentImage);
        public void ToggleAutoSave() => _sessionController.ToggleAutoSave();
    }
}
