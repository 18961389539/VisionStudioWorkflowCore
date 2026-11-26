using System.Windows;

namespace ImageViewer.Abstractions
{
    public interface IImageViewerFileDialogService
    {
        string? ShowOpenImageDialog(Window? owner);

        /// <summary>Lets the user find the original image referenced by a session. A null result cancels opening.</summary>
        string? ShowLocateSessionImageDialog(Window? owner, string missingImagePath, string sessionPath) => null;

        string[] ShowOpenImageFilesDialog(Window? owner);

        string? ShowSaveRoiDialog(Window? owner);

        string? ShowOpenRoiDialog(Window? owner);

        string? ShowSaveSessionDialog(Window? owner);

        /// <summary>Lets a host identify which view is being saved when several sessions are open.</summary>
        string? ShowSaveSessionDialog(Window? owner, string sessionLabel) => ShowSaveSessionDialog(owner);

        string? ShowOpenSessionDialog(Window? owner);

        string? ShowSaveSnapshotDialog(Window? owner);

        string? ShowSaveAnalysisCsvDialog(Window? owner);

        string? ShowSaveXpsDialog(Window? owner);

        string? ShowSaveProjectPackageDialog(Window? owner);

        string? ShowSaveRecipeDialog(Window? owner);

        string? ShowOpenRecipeDialog(Window? owner);
    }
}
