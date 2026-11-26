using System;
using System.Windows;
using System.Windows.Input;
using ImageViewer.Drawing;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Utils;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        public void StartEllipseRoiMode()
        {
            StartDraw(BuiltInDrawControllers.Ellipse, UiText.Get("ToolEllipse"));
        }
    }
}
