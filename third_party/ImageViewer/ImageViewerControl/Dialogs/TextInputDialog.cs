using System.Windows;
using System.Windows.Controls;
using ImageViewer.Localization;

namespace ImageViewer.Dialogs
{
    internal sealed class TextInputDialog : Window
    {
        private readonly TextBox _textBox;

        public TextInputDialog(string message, string defaultValue)
        {
            Title = UiText.Get("DialogTextInputTitle");
            Width = 300;
            Height = 150;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var stackPanel = new StackPanel { Margin = new Thickness(10) };
            stackPanel.Children.Add(new TextBlock { Text = message, Margin = new Thickness(0, 0, 0, 10) });
            _textBox = new TextBox { Text = defaultValue };
            stackPanel.Children.Add(_textBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var okButton = new Button { Content = UiText.Get("DialogButtonOk"), Width = 60, IsDefault = true, Margin = new Thickness(0, 0, 10, 0) };
            okButton.Click += (s, e) => { DialogResult = true; Input = _textBox.Text; Close(); };
            var cancelButton = new Button { Content = UiText.Get("DialogButtonCancel"), Width = 60, IsCancel = true };
            cancelButton.Click += (s, e) => { DialogResult = false; Close(); };

            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            stackPanel.Children.Add(buttonPanel);

            Content = stackPanel;
            Loaded += (_, _) =>
            {
                _textBox.Focus();
                _textBox.SelectAll();
            };
        }

        public string Input { get; private set; } = string.Empty;
    }
}
