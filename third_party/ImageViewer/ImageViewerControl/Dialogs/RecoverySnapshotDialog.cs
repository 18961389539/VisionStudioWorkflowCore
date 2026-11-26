using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ImageViewer.Controls;
using ImageViewer.Localization;

namespace ImageViewer.Dialogs;

internal sealed class RecoverySnapshotDialog : Window
{
    private readonly ListBox _snapshots = new();

    public RecoverySnapshotDialog(Window owner, IReadOnlyList<RecoverySnapshotOption> options)
    {
        Owner = owner;
        Title = UiText.Get("RecoveryPickerTitle");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 590;
        Height = 385;
        MinWidth = 400;
        MinHeight = 285;
        ShowInTaskbar = false;

        var layout = new DockPanel { Margin = new Thickness(16) };
        Content = layout;
        var help = new TextBlock
        {
            Text = UiText.Get("RecoveryPickerHelp"),
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(help, Dock.Top);
        layout.Children.Add(help);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        var restore = new Button { Content = UiText.Get("RecoveryRestoreButton"), IsDefault = true, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4) };
        restore.Click += (_, _) => RestoreSelected();
        buttons.Children.Add(restore);
        buttons.Children.Add(new Button
        {
            Content = UiText.Get("RecoveryPickerCancel"),
            IsCancel = true,
            MinWidth = 90,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0)
        });

        AutomationProperties.SetName(_snapshots, UiText.Get("RecoveryPickerTitle"));
        foreach (RecoverySnapshotOption option in options)
        {
            string title = option.ProjectName ?? option.ImageName ?? Path.GetFileNameWithoutExtension(option.Path);
            string time = option.LastWriteUtc.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
            string detail = option.ImageName != null && option.ProjectName != null
                ? UiText.Format("RecoveryPickerImageAndTime", option.ImageName, time)
                : time;
            var item = new ListBoxItem
            {
                Tag = option,
                Padding = new Thickness(8, 6, 8, 6),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                        new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap }
                    }
                }
            };
            AutomationProperties.SetName(item, $"{title}, {detail}");
            _snapshots.Items.Add(item);
        }
        _snapshots.SelectedIndex = 0;
        restore.IsEnabled = _snapshots.SelectedIndex >= 0;
        _snapshots.SelectionChanged += (_, _) => restore.IsEnabled = _snapshots.SelectedIndex >= 0;
        _snapshots.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject element &&
                ItemsControl.ContainerFromElement(_snapshots, element) is ListBoxItem)
            {
                RestoreSelected();
            }
        };
        Loaded += (_, _) => _snapshots.Focus();
        layout.Children.Add(_snapshots);
    }

    public RecoverySnapshotOption? SelectedSnapshot { get; private set; }

    private void RestoreSelected()
    {
        if (_snapshots.SelectedItem is ListBoxItem { Tag: RecoverySnapshotOption option })
        {
            SelectedSnapshot = option;
            DialogResult = true;
        }
    }
}
