using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private async void OnViewCommandMenuClick(object sender, RoutedEventArgs e)
        {
            await RunUiOperationAsync("视图菜单命令", () =>
            {
                if (TryGetTaggedCommand(sender, out ImageViewerViewCommand command))
                {
                    _viewCommandController.Execute(command);
                    UpdateContextMenuState();
                }

                return Task.CompletedTask;
            });
        }

        private async void OnAnalysisCommandMenuClick(object sender, RoutedEventArgs e)
        {
            await RunUiOperationAsync("分析菜单命令", () =>
            {
                if (TryGetTaggedCommand(sender, out ImageViewerAnalysisCommand command))
                {
                    _analysisCommandController.Execute(command);
                    UpdateContextMenuState();
                }

                return Task.CompletedTask;
            });
        }

        private async void OnRoiMenuCommandClick(object sender, RoutedEventArgs e)
        {
            await RunUiOperationAsync("ROI 菜单命令", () =>
            {
                if (TryGetTaggedCommand(sender, out ImageViewerRoiMenuCommand command))
                {
                    _roiMenuCommandController.Execute(command);
                    UpdateContextMenuState();
                }

                return Task.CompletedTask;
            });
        }

        private async void OnFileMenuCommandClick(object sender, RoutedEventArgs e)
        {
            await RunUiOperationAsync("文件菜单命令", () => HandleFileMenuCommandClickAsync(sender));
        }

        private async void OnToolbarFileCommandClick(object sender, RoutedEventArgs e)
        {
            await RunUiOperationAsync("工具栏文件命令", () => HandleFileMenuCommandClickAsync(sender));
        }

        private void OnToolbarViewCommandClick(object sender, RoutedEventArgs e)
        {
            if (TryGetTaggedCommand(sender, out ImageViewerViewCommand command))
            {
                _viewCommandController.Execute(command);
                UpdateContextMenuState();
            }
        }

        private void OnRightOverlayToggleClick(object sender, RoutedEventArgs e)
        {
            _rightOverlayVisibilityOverride = rightOverlayPanel.Visibility != Visibility.Visible;
            UpdateRightOverlayPanelLayout();
        }

        private void UpdateRightOverlayPanelLayout()
        {
            double width = viewerLayout.ActualWidth;
            bool show = _rightOverlayVisibilityOverride ?? (width <= 0 || width >= RoiSidebarAutoHideWidth);
            if (width > 0)
            {
                rightOverlayPanel.Width = Math.Min(RoiSidebarWidth, width * 0.43);
            }

            rightOverlayPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            string hint = UiText.Get(show ? "RoiSidebarHide" : "RoiSidebarShow");
            rightOverlayToggleButton.ToolTip = hint;
            System.Windows.Automation.AutomationProperties.SetHelpText(rightOverlayToggleButton, hint);
        }

        private void OnRoiFilterTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_roiListView is null)
            {
                return;
            }

            ICollectionView view = _roiListView;
            string query = roiFilterTextBox.Text.Trim();
            string[] words = query.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            view.Filter = item => item is RoiBase roi && (words.Length == 0 || MatchesAllWords(roi, words));
            view.Refresh();
        }

        private static bool MatchesAllWords(RoiBase roi, IReadOnlyList<string> words)
        {
            string searchable = $"{roi.DisplayName} {roi.DisplayTypeName} {roi.Label}";
            foreach (string word in words)
            {
                if (!searchable.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private void OnRoiListZoomClick(object sender, RoutedEventArgs e)
        {
            _viewCommandController.Execute(ImageViewerViewCommand.ZoomToSelection);
            UpdateContextMenuState();
        }

        private void OnRoiListDeleteClick(object sender, RoutedEventArgs e)
        {
            _roiMenuCommandController.Execute(ImageViewerRoiMenuCommand.DeleteSelected);
            UpdateContextMenuState();
        }

        private async Task HandleFileMenuCommandClickAsync(object sender)
        {
            if (sender is MenuItem { Tag: ImageViewerRecentProjectMenuTag recentProject, IsEnabled: true })
            {
                await _fileMenuCommandController.OpenRecentProjectAsync(recentProject.ProjectPath);
                return;
            }

            if (TryGetTaggedCommand(sender, out ImageViewerFileMenuCommand command))
            {
                await _fileMenuCommandController.ExecuteAsync(command);
            }
        }
    }
}
