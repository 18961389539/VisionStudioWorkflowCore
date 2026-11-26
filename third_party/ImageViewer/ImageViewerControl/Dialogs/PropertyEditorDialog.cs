using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ImageViewer.Dialogs
{
    internal sealed class PropertyEditorDialog : Window
    {
        /// <summary>与主控件 ViewerSurfaceBrush (#18212E) 保持一致的面板底色。</summary>
        private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(0x18, 0x21, 0x2E));

        public PropertyEditorDialog(string title, FrameworkElement editor)
        {
            Title = title;
            Width = 360;
            Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = PanelBrush;

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border
                {
                    Background = PanelBrush,
                    Padding = new Thickness(12),
                    Child = editor
                }
            };
        }
    }
}
