using System;
using System.IO;
using System.Windows;
using ImageViewer.Abstractions;
using ImageViewer.Localization;
using Microsoft.Win32;

namespace ImageViewer.Dialogs
{
    public sealed class ImageViewerFileDialogService : IImageViewerFileDialogService
    {
        public string? ShowOpenImageDialog(Window? owner)
        {
            var dialog = new OpenFileDialog
            {
                Title = UiText.Get("FileDialogOpenImageTitle"),
                Filter = UiText.Get("FileDialogOpenImageFilter")
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowLocateSessionImageDialog(Window? owner, string missingImagePath, string sessionPath)
        {
            if (owner == null)
            {
                return null;
            }

            string? originalDirectory = Path.GetDirectoryName(missingImagePath);
            var dialog = new OpenFileDialog
            {
                Title = UiText.Format("FileDialogLocateSessionImageTitle", Path.GetFileName(missingImagePath)),
                Filter = UiText.Get("FileDialogOpenImageFilter"),
                InitialDirectory = (Directory.Exists(originalDirectory) ? originalDirectory : Path.GetDirectoryName(sessionPath)) ?? string.Empty,
                CheckFileExists = true
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string[] ShowOpenImageFilesDialog(Window? owner)
        {
            var dialog = new OpenFileDialog
            {
                Title = UiText.Get("FileDialogOpenBatchImagesTitle"),
                Filter = UiText.Get("FileDialogOpenImageFilter"),
                Multiselect = true
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileNames : Array.Empty<string>();
        }

        public string? ShowSaveRoiDialog(Window? owner)
        {
            var dialog = new SaveFileDialog
            {
                Title = UiText.Get("FileDialogSaveRoiTitle"),
                Filter = UiText.Get("FileDialogRoiFilter"),
                DefaultExt = ".json"
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowOpenRoiDialog(Window? owner)
        {
            var dialog = new OpenFileDialog
            {
                Title = UiText.Get("FileDialogOpenRoiTitle"),
                Filter = UiText.Get("FileDialogRoiFilter")
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowSaveSessionDialog(Window? owner) => ShowSessionSaveDialog(owner, null);

        public string? ShowSaveSessionDialog(Window? owner, string sessionLabel) =>
            ShowSessionSaveDialog(owner, sessionLabel);

        private static string? ShowSessionSaveDialog(Window? owner, string? sessionLabel)
        {
            var dialog = new SaveFileDialog
            {
                Title = sessionLabel == null
                    ? UiText.Get("FileDialogSaveSessionTitle")
                    : UiText.Format("FileDialogSaveNamedSessionTitle", sessionLabel),
                FileName = sessionLabel ?? string.Empty,
                Filter = UiText.Get("FileDialogSessionFilter"),
                DefaultExt = ".ivsession"
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowOpenSessionDialog(Window? owner)
        {
            var dialog = new OpenFileDialog
            {
                Title = UiText.Get("FileDialogOpenSessionTitle"),
                Filter = UiText.Get("FileDialogOpenSessionFilter")
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowSaveSnapshotDialog(Window? owner)
        {
            var dialog = new SaveFileDialog
            {
                Title = UiText.Get("FileDialogSaveSnapshotTitle"),
                Filter = UiText.Get("FileDialogPngFilter"),
                DefaultExt = ".png"
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowSaveAnalysisCsvDialog(Window? owner)
        {
            var dialog = new SaveFileDialog
            {
                Title = UiText.Get("FileDialogSaveAnalysisCsvTitle"),
                Filter = UiText.Get("FileDialogCsvFilter"),
                DefaultExt = ".csv"
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowSaveXpsDialog(Window? owner)
        {
            var dialog = new SaveFileDialog
            {
                Title = UiText.Get("FileDialogSaveXpsTitle"),
                Filter = UiText.Get("FileDialogXpsFilter"),
                DefaultExt = ".xps"
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowSaveProjectPackageDialog(Window? owner)
        {
            var dialog = new SaveFileDialog
            {
                Title = UiText.Get("FileDialogSaveProjectPackageTitle"),
                Filter = UiText.Get("FileDialogProjectPackageFilter"),
                DefaultExt = ".ivpkg"
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowSaveRecipeDialog(Window? owner)
        {
            var dialog = new SaveFileDialog
            {
                Title = UiText.Get("FileDialogSaveRecipeTitle"),
                Filter = UiText.Get("FileDialogRecipeFilter"),
                DefaultExt = ".ivrecipe"
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        public string? ShowOpenRecipeDialog(Window? owner)
        {
            var dialog = new OpenFileDialog
            {
                Title = UiText.Get("FileDialogOpenRecipeTitle"),
                Filter = UiText.Get("FileDialogRecipeFilter"),
                DefaultExt = ".ivrecipe"
            };

            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }
    }
}
