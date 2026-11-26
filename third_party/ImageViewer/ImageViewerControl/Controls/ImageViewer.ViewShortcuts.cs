using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;

namespace ImageViewer.Controls
{
    internal static class ImageViewerViewShortcuts
    {
        // Ctrl is required by the caller. A null Shift value also accepts Ctrl+Shift.
        private static readonly (Key Key, bool? Shift, ImageViewerViewCommand Command)[] Shortcuts =
        [
            (Key.Add, null, ImageViewerViewCommand.ZoomIn),
            (Key.OemPlus, null, ImageViewerViewCommand.ZoomIn),
            (Key.Subtract, null, ImageViewerViewCommand.ZoomOut),
            (Key.OemMinus, null, ImageViewerViewCommand.ZoomOut),
            (Key.D0, null, ImageViewerViewCommand.ResetView),
            (Key.F, null, ImageViewerViewCommand.FitToView),
            (Key.R, true, ImageViewerViewCommand.RotateLeft),
            (Key.R, false, ImageViewerViewCommand.RotateRight),
            (Key.H, true, ImageViewerViewCommand.FlipVertical),
            (Key.H, false, ImageViewerViewCommand.FlipHorizontal)
        ];

        internal static bool TryGetCommand(Key key, bool shiftPressed, out ImageViewerViewCommand command)
        {
            foreach (var shortcut in Shortcuts)
            {
                if (shortcut.Key == key && (shortcut.Shift is null || shortcut.Shift == shiftPressed))
                {
                    command = shortcut.Command;
                    return true;
                }
            }

            command = default;
            return false;
        }

        internal static string GetDisplayText(ImageViewerViewCommand command)
        {
            foreach (var shortcut in Shortcuts)
            {
                if (shortcut.Command != command)
                {
                    continue;
                }

                string keyText = shortcut.Key switch
                {
                    Key.D0 => "0",
                    Key.Add => "+",
                    Key.Subtract => "-",
                    _ => shortcut.Key.ToString()
                };
                return (shortcut.Shift == true ? "Ctrl+Shift+" : "Ctrl+") + keyText;
            }

            return string.Empty;
        }
    }

    internal sealed class ImageViewerViewShortcutTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is IImageViewerMenuCommandTag<ImageViewerViewCommand> tag
                ? ImageViewerViewShortcuts.GetDisplayText(tag.Command)
                : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
