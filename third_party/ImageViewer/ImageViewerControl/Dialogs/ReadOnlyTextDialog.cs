using System.Windows;
using System.Windows.Controls;
using ImageViewer.Localization;

namespace ImageViewer.Dialogs
{
    internal sealed class ReadOnlyTextDialog : Window
    {
        public ReadOnlyTextDialog(string title, string text, string? confirmLabel = null)
        {
            Title = title;
            Width = 420;
            Height = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var textBox = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(12)
            };

            var closeButton = new Button
            {
                Content = UiText.Get(confirmLabel == null ? "DialogButtonClose" : "DialogButtonCancel"),
                IsCancel = true,
                Width = 72,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 12, 12)
            };
            closeButton.Click += (_, _) => Close();

            var buttonHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            if (confirmLabel != null)
            {
                var confirmButton = new Button
                {
                    Content = confirmLabel,
                    MinWidth = 88,
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(0, 0, 0, 12)
                };
                confirmButton.Click += (_, _) => DialogResult = true;
                buttonHost.Children.Add(confirmButton);
            }
            buttonHost.Children.Add(closeButton);
            DockPanel.SetDock(buttonHost, Dock.Bottom);

            var panel = new DockPanel();
            panel.Children.Add(buttonHost);
            panel.Children.Add(textBox);
            Content = panel;
        }
    }
}
