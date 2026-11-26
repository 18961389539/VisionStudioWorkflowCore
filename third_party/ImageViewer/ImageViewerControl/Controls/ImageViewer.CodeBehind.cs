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
        private const double RoiSidebarAutoHideWidth = 680;
        private const double RoiSidebarWidth = 300;
        private bool? _rightOverlayVisibilityOverride;
        private ICollectionView? _roiListView;

        private void InitializeEventHandlers()
        {
            rootGrid.MouseWheel += OnMouseWheel;
            rootGrid.MouseDown += OnMouseDown;
            rootGrid.MouseMove += OnMouseMove;
            rootGrid.MouseUp += OnMouseUp;
            rootGrid.MouseRightButtonDown += OnMouseRightButtonDown;
            rootGrid.LostMouseCapture += OnLostMouseCapture;
            rootGrid.DragOver += OnDragOver;
            rootGrid.DragLeave += OnDragLeave;
            rootGrid.Drop += OnDrop;
            KeyDown += OnKeyDown;
            rootGrid.SizeChanged += OnRootGridSizeChanged;
            viewerLayout.SizeChanged += OnViewerLayoutSizeChanged;
        }

        private void UnregisterEventHandlers()
        {
            rootGrid.MouseWheel -= OnMouseWheel;
            rootGrid.MouseDown -= OnMouseDown;
            rootGrid.MouseMove -= OnMouseMove;
            rootGrid.MouseUp -= OnMouseUp;
            rootGrid.MouseRightButtonDown -= OnMouseRightButtonDown;
            rootGrid.LostMouseCapture -= OnLostMouseCapture;
            rootGrid.DragOver -= OnDragOver;
            rootGrid.DragLeave -= OnDragLeave;
            rootGrid.Drop -= OnDrop;
            rootGrid.SizeChanged -= OnRootGridSizeChanged;
            viewerLayout.SizeChanged -= OnViewerLayoutSizeChanged;
            KeyDown -= OnKeyDown;
        }

        private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ImageSource != null && e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0
                && e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                // Resizing or docking a panel should keep the same image point at the viewport center.
                _controlComposition.ViewportController.TranslateBy(new Vector(
                    (e.NewSize.Width - e.PreviousSize.Width) / 2,
                    (e.NewSize.Height - e.PreviousSize.Height) / 2));
            }

            _imageViewStateController.HandleRootGridSizeChanged();
        }

        private void OnViewerLayoutSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
            {
                UpdateRightOverlayPanelLayout();
            }
        }

        private void UpdateToolbarLayout()
        {
            toolbarPanel.Visibility = ShowToolbar ? Visibility.Visible : Visibility.Collapsed;
            toolbarCompactButton.Visibility = ShowToolbar ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnCompactToolbarClick(object sender, RoutedEventArgs e) => ShowToolbar = true;

        private void OnMoreCommandsClick(object sender, RoutedEventArgs e)
        {
            CaptureContextualPathVertex(null, null);
            mainContextMenu.PlacementTarget = moreCommandsButton;
            mainContextMenu.Placement = PlacementMode.Bottom;
            mainContextMenu.IsOpen = true;
        }

        private void OnContextMenuClosed(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(mainContextMenu.PlacementTarget, moreCommandsButton))
            {
                mainContextMenu.ClearValue(ContextMenu.PlacementTargetProperty);
                mainContextMenu.ClearValue(ContextMenu.PlacementProperty);
            }
        }

        private void OnToolbarHideClick(object sender, RoutedEventArgs e)
        {
            ShowToolbar = false;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _roiListView ??= new ListCollectionView(ViewerState.AllRois);
            roiListBox.ItemsSource = _roiListView;
            UpdateStatusBar();
            _controlComposition.SessionController.StartAutoSave();
            _externalImageSourceBindingController.Refresh();
            _imageSourceController.HandleLoaded();
        }

        private async void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _statusHintTimer?.Stop();
            _zoomBadgeTimer?.Stop();
            _viewportOverlayRefreshScheduler.StopScheduling();
            _analysisRefreshScheduler.StopScheduling();
            _controlComposition.SessionController.StopAutoSave();
            _analysisState.DisposeAnalysisWork();
            _infoPanelStatisticsScheduler.Cancel();
            await RunShutdownOperationAsync(
                "Drain autosave during unload",
                () => _controlComposition.SessionController.DrainAutoSaveAsync());
        }

        private static IEnumerable<MenuItem> EnumerateMenuItems(ItemsControl itemsControl)
        {
            foreach (var item in itemsControl.Items)
            {
                if (item is not MenuItem menuItem)
                {
                    continue;
                }

                yield return menuItem;

                foreach (var child in EnumerateMenuItems(menuItem))
                {
                    yield return child;
                }
            }
        }

        private void RefreshRoiDrawingMenuItems()
        {
            drawRoiMenuItem.Items.Clear();
            roiOperationsMenuItem.Items.Remove(gradientDetectMenuItem);

            foreach (var tool in AvailableDrawingTools)
            {
                ImageViewerDynamicMenuItem menuDescriptor = ImageViewerDynamicMenuItem.FromRoiTool(tool);
                MenuItem menuItem = CreateDynamicMenuItem(menuDescriptor);

                menuItem.Click += OnRoiDrawingToolClick;
                if (menuDescriptor.Group != ImageViewerDynamicMenuGroup.Measurement)
                {
                    drawRoiMenuItem.Items.Add(menuItem);
                }
            }

            PopulateMeasurementMenu(AvailableDrawingTools);

            drawRoiMenuItem.Visibility = drawRoiMenuItem.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            measureMenuItem.Visibility = measureMenuItem.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyMenuItemContentAlignment(drawRoiMenuItem);
            ApplyMenuItemContentAlignment(measureMenuItem);
            RefreshToolbarQuickTools();
        }

        /// <summary>
        /// 将最常用的少量 ROI 工具放到可见工具栏，完整工具集仍保留在右键菜单中。
        /// </summary>
        private void RefreshToolbarQuickTools()
        {
            quickToolsPanel.Children.Clear();

            RoiToolDescriptor? tool = AvailableDrawingTools.FirstOrDefault(
                tool => ReferenceEquals(tool.DrawController, BuiltInDrawControllers.LineMeasure));
            if (tool is not null)
            {
                var button = new ToggleButton
                {
                    ToolTip = tool.Header,
                    Tag = new ImageViewerRoiToolMenuTag(tool.Header, tool.Activate),
                    Style = (Style)FindResource("ViewerToolbarToggleButtonStyle"),
                    Margin = new Thickness(0, 2, 5, 2),
                    Padding = new Thickness(8, 5, 8, 5),
                    MinHeight = 28,
                    IsChecked = IsToolInteractionActive && string.Equals(tool.Header, ActiveToolName, StringComparison.Ordinal)
                };
                button.Content = tool.Header;
                button.SetBinding(IsEnabledProperty, new Binding("MenuState.View.FitToViewEnabled") { Source = this });
                button.Click += OnToolbarRoiToolClick;
                quickToolsPanel.Children.Add(button);
            }
        }

        private static MenuItem CreateDynamicMenuItem(ImageViewerDynamicMenuItem item)
        {
            var menuItem = new MenuItem
            {
                Header = item.Header,
                ToolTip = item.ToolTip,
                IsEnabled = item.IsEnabled,
                Tag = item.Tag
            };

            if (item.CreateIcon != null)
            {
                menuItem.Icon = item.CreateIcon();
            }

            return menuItem;
        }

        private static void ApplyMenuItemContentAlignment(ItemsControl itemsControl)
        {
            foreach (var menuItem in EnumerateMenuItems(itemsControl))
            {
                menuItem.HorizontalContentAlignment = HorizontalAlignment.Left;
                menuItem.VerticalContentAlignment = VerticalAlignment.Center;
            }
        }

        private void OnRoiDrawingToolClick(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: ImageViewerRoiToolMenuTag tool } menuItem)
            {
                tool.Activate(this);
                SetActiveToolName(tool.ToolName);
                ShowStatusHint(UiText.Format("StatusToolActivated", tool.ToolName));
            }
        }

        private void OnToolbarRoiToolClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: ImageViewerRoiToolMenuTag tool })
            {
                tool.Activate(this);
                SetActiveToolName(tool.ToolName);
                ShowStatusHint(UiText.Format("StatusToolActivated", tool.ToolName));
            }
        }

        private static bool TryGetTaggedCommand<TCommand>(object sender, out TCommand command)
            where TCommand : struct, Enum
        {
            if (sender is FrameworkElement { Tag: IImageViewerMenuCommandTag<TCommand> tag })
            {
                command = tag.Command;
                return true;
            }

            command = default;
            return false;
        }

        private async void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _featureMenuCommandController.CancelBatchAnalysisExport())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && IsToolInteractionActive)
            {
                e.Handled = true;
                ExitCurrentMode();
                ShowStatusHint(UiText.Get("StatusDrawCancelled"));
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
            {
                e.Handled = true;
                await RunUiOperationAsync(
                    "打开图像快捷键",
                    () => _fileMenuCommandController.ExecuteAsync(ImageViewerFileMenuCommand.OpenImage));
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                e.Handled = true;
                await RunUiOperationAsync(
                    "保存会话快捷键",
                    () => _fileMenuCommandController.ExecuteAsync(ImageViewerFileMenuCommand.SaveSession));
                return;
            }

            try
            {
                _interactionController.HandleKeyDown(e);
            }
            catch (Exception ex)
            {
                ReportUiOperationFailure("键盘快捷键处理", ex);
            }
        }

        private void OnActiveToolCancelClick(object sender, RoutedEventArgs e)
        {
            if (!IsToolInteractionActive)
            {
                return;
            }

            ExitCurrentMode();
            ShowStatusHint(UiText.Get("StatusDrawCancelled"));
        }

        private void OnQuickCircularCaliperSettingsClick(object sender, RoutedEventArgs e)
        {
            if (ViewerState.SelectedRoi is not CircularCaliperMeasureRoi circularCaliper)
            {
                return;
            }

            _dialogWorkflowService.ShowCaliperSettings(circularCaliper);
            _roiSelectionStateController.RefreshPropertyPanel();
        }

        private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e) => _interactionController.HandleMouseRightButtonDown(e);


        private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomAtViewportCenter(1.25);

        private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomAtViewportCenter(0.8);

        /// <summary>Zooms around the drawable viewport center by a positive, finite factor.</summary>
        public void ZoomAtViewportCenter(double factor)
        {
            if (!double.IsFinite(factor) || factor <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(factor), factor, "Zoom factor must be positive and finite.");
            }

            ImageViewerViewportState state = _controlComposition.ViewportController.CurrentState;
            if (state.Scale <= 0 || rootGrid.ActualWidth <= 0 || rootGrid.ActualHeight <= 0)
            {
                return;
            }

            Point viewportCenter = new(rootGrid.ActualWidth / 2, rootGrid.ActualHeight / 2);
            Point imagePoint = new(
                (viewportCenter.X - state.TranslateX) / state.Scale,
                (viewportCenter.Y - state.TranslateY) / state.Scale);
            _controlComposition.ViewportController.ZoomAt(imagePoint, factor);
        }

        internal void UpdateContextMenuState()
        {
            _contextMenuController.UpdateState();
            ApplyMenuAvailabilityHints();
        }

        public Task ShowOpenImageDialogAsync() => FileImageWorkflow.OpenImageAsync();

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Use ShowOpenImageDialogAsync() instead.", false)]
        public Task OpenImageAsync() => ShowOpenImageDialogAsync();

        private void OnMouseWheel(object sender, MouseWheelEventArgs e) => _interactionController.HandleMouseWheel(e);

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            _interactionController.HandleMouseDown(e);
        }

        private void OnMouseMove(object sender, MouseEventArgs e) => _interactionController.HandleMouseMove(e);

        private void OnMouseUp(object sender, MouseButtonEventArgs e) => _interactionController.HandleMouseUp(e);
    }
}
