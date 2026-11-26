using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Controls;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Smoke")]
    [Trait("Category", "Wpf")]
    public class ImageViewerUsabilitySmokeTests
    {
        [Fact]
        public void ShowStatusHint_DisplaysMessageAndKind()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var statusBorder = viewer.statusHintBorder;
                var statusText = viewer.statusHintTextBlock;

                viewer.ShowStatusHint("测试提示", StatusHintKind.Success, durationMs: 500);
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, statusBorder.Visibility);
                Assert.Equal("测试提示", statusText.Text);

                viewer.DismissStatusHint();
                WpfTestRunner.DrainDispatcher();
                Assert.Equal(Visibility.Collapsed, statusBorder.Visibility);
            });
        }

        [Fact]
        public void ShowStatusHint_IgnoresEmptyMessage()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var statusBorder = viewer.statusHintBorder;

                viewer.ShowStatusHint(string.Empty);
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Collapsed, statusBorder.Visibility);
            });
        }

        [Fact]
        public void MenuSearch_MatchesShortcutGestureText()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var searchBox = viewer.menuSearchBox;
                var noResultsText = viewer.menuSearchNoResultsText;

                searchBox.Text = "Ctrl+Shift+R";
                WpfTestRunner.DrainDispatcher();

                var rotateLeft = FindMenuItem(viewer, ImageViewerViewMenuTags.RotateLeft);
                Assert.NotNull(rotateLeft);
                Assert.Equal(Visibility.Visible, rotateLeft!.Visibility);
                Assert.Equal(Visibility.Collapsed, noResultsText.Visibility);
            });
        }

        [Fact]
        public void MenuSearch_MatchesExportKeywordsByTooltipOrHeader()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var searchBox = viewer.menuSearchBox;
                var noResultsText = viewer.menuSearchNoResultsText;

                searchBox.Text = "PNG";
                WpfTestRunner.DrainDispatcher();

                var exportSnapshot = viewer.exportSnapshotMenuItem;
                Assert.Equal(Visibility.Visible, exportSnapshot.Visibility);
                Assert.Equal(Visibility.Collapsed, noResultsText.Visibility);
            });
        }

        [Fact]
        public void MenuSearch_MultipleKeywordsRequireAllWords()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var searchBox = viewer.menuSearchBox;
                var exportSnapshot = viewer.exportSnapshotMenuItem;

                searchBox.Text = "导出 PNG";
                WpfTestRunner.DrainDispatcher();
                Assert.Contains(viewer.mainContextMenu.Items.OfType<MenuItem>(), item => item.Tag is MenuSearchResultTag tag && ReferenceEquals(tag.Original, exportSnapshot));

                searchBox.Text = "导出 缩放";
                WpfTestRunner.DrainDispatcher();
                Assert.DoesNotContain(viewer.mainContextMenu.Items.OfType<MenuItem>(), item => item.Tag is MenuSearchResultTag tag && ReferenceEquals(tag.Original, exportSnapshot));
            });
        }

        [Fact]
        public void MenuSearch_SeparatorIgnoredShortcut()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var searchBox = viewer.menuSearchBox;

                searchBox.Text = "CtrlShiftR";
                WpfTestRunner.DrainDispatcher();

                var rotateLeft = FindMenuItem(viewer, ImageViewerViewMenuTags.RotateLeft);
                Assert.NotNull(rotateLeft);
                Assert.Equal(Visibility.Visible, rotateLeft!.Visibility);
            });
        }

        [Fact]
        public void MenuSearch_ShowsMatchCountWhenResultsFound()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var searchBox = viewer.menuSearchBox;
                var matchCountText = viewer.menuSearchMatchCountText;
                var noResultsText = viewer.menuSearchNoResultsText;

                searchBox.Text = "PNG";
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, matchCountText.Visibility);
                Assert.StartsWith("找到 ", matchCountText.Text);
                Assert.Equal(Visibility.Collapsed, noResultsText.Visibility);
            });
        }

        [Fact]
        public void ShowZoomBadge_DisplaysNearCursorAndDismisses()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var zoomBadge = viewer.zoomBadgeBorder;
                var zoomText = viewer.zoomBadgeTextBlock;

                viewer.ShowZoomBadge("缩放：150%", new Point(40, 30));
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, zoomBadge.Visibility);
                Assert.Equal("缩放：150%", zoomText.Text);

                viewer.DismissZoomBadge();
                WpfTestRunner.DrainDispatcher();
                Assert.Equal(Visibility.Collapsed, zoomBadge.Visibility);
            });
        }

        [Fact]
        public void ShowToolbar_DefaultsVisibleAndCanBeHiddenAndRestored()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var toolbarPanel = viewer.toolbarPanel;
                var toolbarMenuItem = viewer.showToolbarMenuItem;

                Assert.True(viewer.ShowToolbar);
                Assert.Equal(Visibility.Visible, toolbarPanel.Visibility);

                WpfTestRunner.InvokePrivate(viewer, "OnViewCommandMenuClick", new MenuItem { Tag = ImageViewerViewMenuTags.ToggleToolbar }, new RoutedEventArgs());
                WpfTestRunner.DrainDispatcher();

                Assert.False(viewer.ShowToolbar);
                Assert.Equal(Visibility.Collapsed, toolbarPanel.Visibility);
                Assert.False(toolbarMenuItem.IsChecked);

                viewer.toolbarCompactButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfTestRunner.DrainDispatcher();
                Assert.True(toolbarMenuItem.IsChecked);

                viewer.toolbarHideButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfTestRunner.DrainDispatcher();

                Assert.False(viewer.ShowToolbar);
                Assert.Equal(Visibility.Collapsed, toolbarPanel.Visibility);
                Assert.False(toolbarMenuItem.IsChecked);
                Assert.Equal(Visibility.Visible, viewer.toolbarCompactButton.Visibility);
                Assert.Equal(UiText.Get("ToolbarRestoreHint"), viewer.toolbarCompactButton.ToolTip);

                viewer.toolbarCompactButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfTestRunner.DrainDispatcher();

                Assert.True(viewer.ShowToolbar);
                Assert.Equal(Visibility.Visible, toolbarPanel.Visibility);
            });
        }

        [Fact]
        public void DockedLayout_LeavesRoomForCanvasAndKeepsToolbarOnNarrowWindows()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer
                {
                    ShowToolbar = true
                };
                var window = new Window
                {
                    Width = 520,
                    Height = 380,
                    Content = viewer
                };

                try
                {
                    window.Show();
                    WpfTestRunner.DrainDispatcher();
                    window.UpdateLayout();

                    var sidebar = Assert.IsType<Grid>(viewer.FindName("rightOverlayPanel"));
                    Assert.True(viewer.toolbarPanel.IsVisible);
                    Assert.False(viewer.toolbarCompactButton.IsVisible);
                    Assert.False(sidebar.IsVisible);

                    viewer.rightOverlayToggleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    window.UpdateLayout();
                    Assert.True(sidebar.IsVisible);

                    window.Width = 1100;
                    window.Height = 720;
                    window.UpdateLayout();
                    WpfTestRunner.DrainDispatcher();

                    Rect canvas = viewer.rootGrid.TransformToAncestor(viewer).TransformBounds(new Rect(viewer.rootGrid.RenderSize));
                    Rect toolbar = viewer.toolbarPanel.TransformToAncestor(viewer).TransformBounds(new Rect(viewer.toolbarPanel.RenderSize));
                    Rect side = sidebar.TransformToAncestor(viewer).TransformBounds(new Rect(sidebar.RenderSize));
                    Rect status = viewer.statusBarBorder.TransformToAncestor(viewer).TransformBounds(new Rect(viewer.statusBarBorder.RenderSize));
                    Assert.True(toolbar.Bottom <= canvas.Top);
                    Assert.True(canvas.Right <= side.Left);
                    Assert.True(side.Bottom <= status.Top);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void MeasurementResultAndRoiList_AreVisibleByDefault()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                WpfTestRunner.DrainDispatcher();

                Assert.True(viewer.ShowInfoPanel);
                Assert.True(viewer.ShowRoiList);
                Assert.Equal(Visibility.Visible, viewer.infoPanel.Visibility);
                Assert.Equal(Visibility.Visible, viewer.roiListPanel.Visibility);
            });
        }

        [Fact]
        public void Toolbar_ExposesPrimaryWorkflowActions()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer
                {
                    ShowToolbar = true
                };
                WpfTestRunner.DrainDispatcher();

                var buttons = FindVisualDescendants<Button>(viewer.toolbarPanel);
                Button saveSession = Assert.Single(buttons.Where(button => Equals(button.Content, UiText.Get("ToolbarSaveSession"))));
                Assert.Contains(buttons, button => Equals(button.Content, UiText.Get("MenuOpenImageFile")));
                Assert.Contains(buttons, button => Equals(button.Content, UiText.Get("MenuFitToView")));
                Assert.Contains(buttons, button => Equals(button.Content, UiText.Get("ToolbarExportCsv")));
                Assert.Equal(UiText.Get("ToolLineMeasure"), Assert.IsType<ToggleButton>(Assert.Single(viewer.quickToolsPanel.Children.Cast<object>())).Content);
                Assert.False(saveSession.IsEnabled);

                viewer.ImageSource = CreateBitmap(9);
                WpfTestRunner.DrainDispatcher();
                Assert.True(saveSession.IsEnabled);
            });
        }

        [Fact]
        public void ActiveToolIndicator_RemainsVisibleUntilDrawModeExits()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();

                viewer.StartLineMeasureMode();
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(UiText.Get("ToolLineMeasure"), viewer.ActiveToolName);
                Assert.Equal(UiText.Get("ToolLineMeasure"), viewer.activeToolNameTextBlock.Text);
                Assert.Equal(Visibility.Visible, viewer.activeToolBanner.Visibility);

                viewer.ExitCurrentMode();
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(string.Empty, viewer.ActiveToolName);
                Assert.Equal(Visibility.Collapsed, viewer.activeToolBanner.Visibility);
            });
        }

        [Fact]
        public void InteractionModeIndicator_TracksBrowseDrawAndEditStates()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(UiText.Get("InteractionModeBrowse"), viewer.InteractionModeText);

                viewer.ViewerState.SelectedRoi = new CircleRoi
                {
                    Center = new PointD(4, 4),
                    Radius = 2
                };
                WpfTestRunner.DrainDispatcher();
                Assert.Equal(UiText.Get("InteractionModeEdit"), viewer.InteractionModeText);

                viewer.StartLineMeasureMode();
                WpfTestRunner.DrainDispatcher();
                Assert.Equal(UiText.Get("InteractionModeDraw"), viewer.InteractionModeText);

                viewer.ExitCurrentMode();
                viewer.ViewerState.SelectedRoi = null;
                WpfTestRunner.DrainDispatcher();
                Assert.Equal(UiText.Get("InteractionModeBrowse"), viewer.InteractionModeText);
            });
        }

        [Fact]
        public void UnsavedMarker_TracksDocumentChangesAndCleanBaseline()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                viewer.SetImage(CreateBitmap(9));
                viewer.MarkDocumentClean();
                Assert.False(viewer.IsDirty);

                Assert.True(viewer.AddRoi(new CircleRoi
                {
                    Center = new PointD(4, 4),
                    Radius = 2
                }));
                Assert.True(viewer.IsDirty);

                viewer.MarkDocumentClean();
                Assert.False(viewer.IsDirty);
            });
        }

        [Fact]
        public void BrowsingKeepsDocumentCleanButCalibrationStillRequiresSaving()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer { ImageSource = CreateBitmap(9) };
                var window = new Window { Width = 900, Height = 620, Content = viewer };
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    viewer.MarkDocumentClean();
                    double originalScale = viewer.Scale;
                    viewer.ZoomAtViewportCenter(1.25);
                    Assert.True(viewer.Scale > originalScale);
                    viewer.rightOverlayToggleButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    window.UpdateLayout();
                    viewer.FitImageToView();
                    Assert.False(viewer.IsDirty);

                    viewer.PixelSize = 0.5;
                    viewer.PhysicalUnit = "mm";
                    Assert.True(viewer.IsDirty);
                }
                finally
                {
                    window.Close();
                }
            });
        }

        [Fact]
        public void ToolbarQuickTool_StaysHighlightedWhileActive()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer
                {
                    ImageSource = CreateBitmap(9)
                };
                WpfTestRunner.DrainDispatcher();

                ToggleButton toolButton = Assert.IsType<ToggleButton>(viewer.quickToolsPanel.Children[0]);
                string toolName = Assert.IsType<string>(toolButton.ToolTip);
                toolButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(toolName, viewer.ActiveToolName);
                Assert.True(toolButton.IsChecked);

                viewer.ExitCurrentMode();
                WpfTestRunner.DrainDispatcher();
                Assert.False(toolButton.IsChecked);
            });
        }

        [Fact]
        public void CaliperDiagnostics_AreHiddenByDefaultAndCanBeEnabled()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                WpfTestRunner.DrainDispatcher();

                Assert.False(viewer.ShowCaliperScores);
                Assert.False(viewer.showCaliperScoresMenuItem.IsChecked);

                viewer.ShowCaliperScores = true;
                WpfTestRunner.InvokePrivate(viewer, "UpdateContextMenuState");
                WpfTestRunner.DrainDispatcher();

                Assert.True(viewer.showCaliperScoresMenuItem.IsChecked);
            });
        }

        [Fact]
        public void StatusBar_WithoutImage_ShowsNoDataLoaded()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var statusBorder = viewer.statusBarBorder;
                var statusText = viewer.statusBarTextBlock;

                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, statusBorder.Visibility);
                Assert.Equal(UiText.Get("StatusNoDataLoaded"), statusText.Text);
            });
        }

        [Fact]
        public void StatusBar_WithImage_ShowsDimensionsAndPath()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var statusBorder = viewer.statusBarBorder;
                var statusText = viewer.statusBarTextBlock;

                viewer.ImageSource = CreateBitmap(9);
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, statusBorder.Visibility);
                string expected = UiText.Format("StatusBarImageSizeOnly", 2, 2);
                Assert.Equal(expected, statusText.Text);
            });
        }

        [Fact]
        public void StatusBar_WithPathImage_ShowsFileNameAndOffersFullPath()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var statusText = viewer.statusBarTextBlock;

                string path = CreateTempPngPath();
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new System.Uri(path, System.UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.None;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    viewer.ImageSource = bitmap;
                    WpfTestRunner.DrainDispatcher();

                    Assert.Contains(System.IO.Path.GetFileName(path), statusText.Text);
                    Assert.DoesNotContain(System.IO.Path.GetFullPath(path), statusText.Text);
                    Assert.Equal(System.IO.Path.GetFullPath(path), statusText.ToolTip);
                    Assert.Contains("2 × 2", statusText.Text);
                }
                finally
                {
                    System.IO.File.Delete(path);
                }
            });
        }

        [Fact]
        public void CalibrationEntry_ExplainsMissingReferenceAndEnablesWhenLineIsSelected()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer { ImageSource = CreateBitmap(9) };
                var button = Assert.IsType<Button>(viewer.FindName("toolbarCalibrateButton"));
                var menuItem = viewer.calibratePixelsMenuItem;
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, menuItem.Visibility);
                Assert.False(menuItem.IsEnabled);
                Assert.False(button.IsEnabled);
                Assert.Equal(UiText.Get("MenuNeedCalibrationLine"), button.ToolTip);
                Assert.Equal(UiText.Get("CalibrationBadgePixels"), viewer.calibrationBadgeTextBlock.Text);

                var line = new LineMeasureRoi { P1 = new PointD(0, 0), P2 = new PointD(1, 0) };
                Assert.True(viewer.AddRoi(line));
                viewer.ViewerState.SelectedRoi = null;
                viewer.MarkDocumentClean();
                viewer.ViewerState.SelectedRoi = line;
                WpfTestRunner.DrainDispatcher();
                Assert.True(button.IsEnabled);
                Assert.True(menuItem.IsEnabled);
                Assert.False(viewer.IsDirty);
                Assert.Null(line.Tolerance);

                var editorHost = Assert.IsType<ContentControl>(viewer.FindName("roiPropertyEditorHost"));
                var editor = Assert.IsAssignableFrom<FrameworkElement>(editorHost.Content);
                TextBox nominal = Assert.Single(FindVisualDescendants<TextBox>(editor)
                    .Where(field => AutomationProperties.GetName(field) == UiText.Get("EditorToleranceNominal")));
                nominal.Text = "40";
                Assert.Equal(40, line.Tolerance?.Nominal);
                Assert.True(viewer.IsDirty);
            });
        }

        [Fact]
        public void CalibrationIndicator_IsPersistentAndUpdatesWithCalibration()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Collapsed, viewer.calibrationBadge.Visibility);

                viewer.PixelSize = 0.5;
                viewer.PhysicalUnit = "mm";
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, viewer.calibrationBadge.Visibility);
                Assert.Contains(UiText.FormatInvariant("CalibrationBadgeText", 0.5, "mm"), viewer.calibrationBadgeTextBlock.Text);
                Assert.Contains(UiText.Get("CalibrationBadgeScaleOnlySuffix"), viewer.calibrationBadgeTextBlock.Text);
                Assert.Contains(UiText.Get("CalibrationBadgeTooltipNoDistortion"), (string)viewer.calibrationBadgeTextBlock.ToolTip);

                viewer.PixelSize = 1.0;
                viewer.PhysicalUnit = "px";
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Collapsed, viewer.calibrationBadge.Visibility);
            });
        }

        [Fact]
        public void LineLabel_UsesTheSameCalibratedLengthAsInfoAndCsv_AndSuppressesFailedCorrection()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer
                {
                    ImageSource = CreateUniformBitmap(160, 120, 0),
                    PixelSize = 0.5,
                    PhysicalUnit = "mm",
                    Calibration = new CameraCalibration { NormalizationRadius = 100, MeasurementUncertaintyPixels = 0.2 }
                };
                var line = new LineMeasureRoi { P1 = new PointD(20, 0), P2 = new PointD(100, 0) };
                Assert.True(viewer.AddRoi(line));
                var overlay = Assert.IsType<Canvas>(viewer.FindName("screenOverlayCanvas"));
                string Label()
                {
                    viewer.DrawRois(immediate: true);
                    return Assert.Single(overlay.Children.OfType<TextBlock>()).Text;
                }

                Assert.Contains("40.00 mm", Label());
                Assert.Contains(UiText.Get("CalibrationBadgeScaleOnlySuffix"), viewer.calibrationBadgeTextBlock.Text);
                Assert.DoesNotContain(UiText.Get("CalibrationBadgeDistortionSuffix"), viewer.calibrationBadgeTextBlock.Text);

                viewer.Calibration = new CameraCalibration { NormalizationRadius = 100, K1 = 0.35 };
                double expected = viewer.Calibration.CorrectPixelLength(line.P1, line.P2) * 0.5;
                Assert.NotEqual(40.0, expected, precision: 2);
                string formatted = FormattableString.Invariant($"{expected:F2} mm");
                Assert.Contains(formatted, Label());
                Assert.Contains(formatted, RoiInfoService.BuildInfo(line, null, 0.5, "mm", RoiPluginRegistry.CreateBuiltIn(), calibration: viewer.Calibration));
                Assert.Contains($"Length={formatted}", RoiAnalysisExportService.BuildCsv([line], null, 0.5, "mm", viewer.Calibration));
                Assert.Contains(UiText.Get("CalibrationBadgeDistortionSuffix"), viewer.calibrationBadgeTextBlock.Text);

                viewer.Calibration = new CameraCalibration { NormalizationRadius = 100, K1 = -1 };
                Assert.Contains(UiText.Get("MeasurementUnavailable"), Label());
                string failedCsv = RoiAnalysisExportService.BuildCsv([line], null, 0.5, "mm", viewer.Calibration);
                Assert.Contains("NotEvaluable", failedCsv);
                Assert.DoesNotContain("Length=", failedCsv);

                viewer.Calibration = new CameraCalibration { NormalizationRadius = 0, K1 = 0.1 };
                Assert.Contains(UiText.Get("MeasurementUnavailable"), Label());
                Assert.Contains(UiText.Get("CalibrationBadgeInvalidDistortionSuffix"), viewer.calibrationBadgeTextBlock.Text);
            });
        }

        [Fact]
        public void RotatedAndMirroredImage_KeepsSelectedLineHandlesAndLabelOnImageCoordinates()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer { ImageSource = CreateUniformBitmap(160, 100, 0) };
                var window = new Window { Width = 900, Height = 620, Content = viewer };
                try
                {
                    window.Show();
                    WpfTestRunner.DrainDispatcher();
                    viewer.FitImageToView();
                    var line = new LineMeasureRoi { P1 = new PointD(35, 25), P2 = new PointD(95, 40) };
                    Assert.True(viewer.AddRoi(line));
                    var overlay = Assert.IsType<Canvas>(viewer.FindName("screenOverlayCanvas"));
                    Point imageCenter = new(80, 50);
                    Point fixedCenter = viewer.imageContainer.TranslatePoint(imageCenter, overlay);

                    viewer.RotateImageRight();
                    viewer.FlipImageHorizontal();
                    WpfTestRunner.DrainDispatcher();
                    viewer.DrawRois(immediate: true);
                    AssertClose(fixedCenter, viewer.imageContainer.TranslatePoint(imageCenter, overlay));

                    var handle = Assert.Single(overlay.Children.OfType<System.Windows.Shapes.Shape>().Where(shape => Equals(shape.Tag, ResizeHandle.P1)));
                    Point handleCenter = new(Canvas.GetLeft(handle) + handle.Width / 2, Canvas.GetTop(handle) + handle.Height / 2);
                    Point first = new(line.P1.X, line.P1.Y);
                    AssertClose(viewer.imageContainer.TranslatePoint(first, overlay), handleCenter);
                    Assert.Equal(ResizeHandle.P1, viewer.GetHandleAt(overlay.TranslatePoint(handleCenter, viewer.imageContainer)));

                    TextBlock label = Assert.Single(overlay.Children.OfType<TextBlock>());
                    AssertClose(viewer.imageContainer.TranslatePoint(new Point(65, 32.5), overlay),
                        new Point(Canvas.GetLeft(label) + label.DesiredSize.Width / 2, Canvas.GetTop(label) + label.DesiredSize.Height / 2));
                }
                finally
                {
                    window.Close();
                }
            });

            static void AssertClose(Point expected, Point actual)
                => Assert.InRange((expected - actual).Length, 0, 1);
        }

        /// <summary>
        /// ①-1 守卫：标定精度元数据（重投影 RMS / 不确定度 / 标定标识）必须在状态栏徽标上可见。
        /// Chinese: 此前这些字段没有任何 UI 出口，用户无法判断标定是否满足当前测量要求。
        /// </summary>
        [Fact]
        public void CalibrationIndicator_SurfacesAccuracyMetadataFromCalibration()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                WpfTestRunner.DrainDispatcher();

                viewer.PixelSize = 0.5;
                viewer.PhysicalUnit = "mm";
                viewer.Calibration = new CameraCalibration
                {
                    K1 = 0.01,
                    K2 = -0.002,
                    K3 = 0.0003,
                    TangentialP1 = 0.00004,
                    ReprojectionErrorRms = 0.08,
                    MeasurementUncertaintyPixels = 0.2,
                    CalibrationId = "CERT-X",
                    CalibratedAtUtc = new DateTimeOffset(2026, 3, 1, 8, 30, 0, TimeSpan.Zero)
                };
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, viewer.calibrationBadge.Visibility);

                // 徽标文本：不确定度优先（它直接对应"测出来能准到多少"），按 PixelSize 换算为物理单位
                Assert.Contains(UiText.FormatInvariant("CalibrationBadgeText", 0.5, "mm"), viewer.calibrationBadgeTextBlock.Text);
                Assert.Contains(UiText.FormatInvariant("CalibrationBadgeUncertaintySuffix", 0.1, "mm"), viewer.calibrationBadgeTextBlock.Text);

                // ToolTip：完整精度档案
                string tooltip = (string)viewer.calibrationBadgeTextBlock.ToolTip;
                Assert.Contains("CERT-X", tooltip);
                Assert.Contains("0.08", tooltip);
                Assert.Contains("0.2", tooltip);
                Assert.Contains(UiText.FormatInvariant("CalibrationBadgeTooltipDistortionExtended", 0.01, -0.002, 0.0003, 0.00004, 0.0), tooltip);
            });
        }

        /// <summary>
        /// 只有重投影 RMS、没有不确定度时，徽标也必须给出可读精度而非静默丢弃。
        /// </summary>
        [Fact]
        public void CalibrationIndicator_FallsBackToReprojectionErrorWhenUncertaintyMissing()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                WpfTestRunner.DrainDispatcher();

                viewer.PixelSize = 0.5;
                viewer.PhysicalUnit = "mm";
                viewer.Calibration = new CameraCalibration
                {
                    K1 = 0.01,
                    ReprojectionErrorRms = 0.08
                };
                WpfTestRunner.DrainDispatcher();

                Assert.Contains(
                    UiText.FormatInvariant("CalibrationBadgeReprojectionSuffix", 0.04, "mm"),
                    viewer.calibrationBadgeTextBlock.Text);
            });
        }

        [Fact]
        public void CircularCaliperSelection_ExposesDirectSettingsEntry()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var circularCaliper = new CircularCaliperMeasureRoi
                {
                    Center = new PointD(16, 16),
                    Radius = 8
                };

                viewer.ViewerState.SelectedRoi = circularCaliper;
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, viewer.roiPropertyPanel.Visibility);
                Assert.Equal(Visibility.Visible, viewer.circularCaliperSettingsButton.Visibility);
                Assert.Equal(UiText.Get("QuickCircularCaliperSettings"), viewer.circularCaliperSettingsButton.Content);
            });
        }

        [Fact]
        public void Automation_SetsAccessibleNamesOnCoreElements()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var imageElement = viewer.image;
                var roiList = viewer.roiListBox;
                TextBlock? zoomLevel = FindZoomLevelTextBlock(viewer);

                Assert.Equal(UiText.Get("ViewerAccessibleName"), AutomationProperties.GetName(viewer));
                Assert.Equal(UiText.Get("ImageDisplayAreaName"), AutomationProperties.GetName(imageElement));
                Assert.Equal(UiText.Get("RoiListAccessibleName"), AutomationProperties.GetName(roiList));
                Assert.NotNull(zoomLevel);
                Assert.Equal(UiText.Get("ZoomLevelDisplayAccessibleName"), AutomationProperties.GetName(zoomLevel));
            });
        }

        [Fact]
        public void ShowStatusHint_Success_ShowsSuccessIcon()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var icon = viewer.statusHintIconTextBlock;

                viewer.ShowStatusHint("成功", StatusHintKind.Success, durationMs: 500);
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, icon.Visibility);
                Assert.Equal("✓", icon.Text);

                viewer.DismissStatusHint();
            });
        }

        [Fact]
        public void ShowStatusHint_Error_ShowsErrorIcon()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var icon = viewer.statusHintIconTextBlock;
                var text = viewer.statusHintTextBlock;

                viewer.ShowStatusHint("失败", StatusHintKind.Error, durationMs: 500);
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, icon.Visibility);
                Assert.Equal("⚠", icon.Text);
                Assert.Equal("失败", text.Text);
                Assert.Same(viewer.FindResource("ViewerErrorSoftBrush"), viewer.statusHintBorder.Background);
                Assert.Same(viewer.FindResource("ViewerErrorBorderBrush"), viewer.statusHintBorder.BorderBrush);

                viewer.DismissStatusHint();
            });
        }

        [Fact]
        public void AutomaticCircleMeasure_WhenNoCircleFound_ShowsErrorStatusHint()
        {
            WpfTestRunner.RunAsync(async () =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                BitmapSource image = CreateUniformBitmap(64, 64, 128);
                viewer.SetImage(image);
                Task prepare = Assert.IsAssignableFrom<Task>(WpfTestRunner.InvokePrivate(viewer, "PrepareAnalysisResourcesAsync", image));
                await prepare;
                WpfTestRunner.DrainDispatcher();

                object?[] arguments = [new Point(32, 32), null];
                object? created = WpfTestRunner.InvokePrivate(viewer, "TryCreateAutomaticCircle", arguments);

                Assert.Equal(false, created);
                WpfTestRunner.DrainDispatcher();
                Assert.Equal(UiText.Get("StatusAutomaticCircleNotFound"), viewer.statusHintTextBlock.Text);
                Assert.Equal(Visibility.Visible, viewer.statusHintBorder.Visibility);

                viewer.DismissStatusHint();
            });
        }

        [Fact]
        public void MenuSearch_CountsLeafCommandsNotGroups()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var circle = new CircleRoi { Center = new PointD(1, 1), Radius = 1 };
                Assert.True(viewer.AddRoi(circle));
                viewer.ViewerState.SelectedRoi = circle;
                viewer.UpdateContextMenuState();
                WpfTestRunner.DrainDispatcher();
                viewer.menuSearchBox.Text = "Ctrl+Z";
                WpfTestRunner.DrainDispatcher();

                Assert.Contains(viewer.mainContextMenu.Items.OfType<MenuItem>(), item => item.Tag is MenuSearchResultTag tag && ReferenceEquals(tag.Original, viewer.undoMenuItem));
                // 撤销、删除、清空三项的 ToolTip/InputGestureText 均提及 Ctrl+Z，应按叶子命令计数为 3
                Assert.StartsWith("找到 3", viewer.menuSearchMatchCountText.Text);
            });
        }

        private static ImageSource CreateBitmap(byte value)
        {
            return BitmapSource.Create(
                2,
                2,
                96,
                96,
                PixelFormats.Gray8,
                null,
                new[] { value, value, value, value },
                2);
        }

        private static BitmapSource CreateUniformBitmap(int pixelWidth, int pixelHeight, byte value)
        {
            byte[] pixels = new byte[pixelWidth * pixelHeight];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = value;
            }

            return BitmapSource.Create(
                pixelWidth,
                pixelHeight,
                96,
                96,
                PixelFormats.Gray8,
                null,
                pixels,
                pixelWidth);
        }

        private static string CreateTempPngPath()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"iv-statusbar-{System.Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create((BitmapSource)CreateBitmap(9)));
            using var stream = System.IO.File.Create(path);
            encoder.Save(stream);
            return path;
        }

        private static TextBlock? FindZoomLevelTextBlock(ImageViewer.Controls.ImageViewer viewer)
        {
            var toolbarPanel = viewer.toolbarPanel;
            return FindVisualDescendants<TextBlock>(toolbarPanel).FirstOrDefault(text => ReferenceEquals(AutomationProperties.GetName(text), UiText.Get("ZoomLevelDisplayAccessibleName")));
        }

        private static System.Collections.Generic.IReadOnlyList<T> FindVisualDescendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            var matches = new System.Collections.Generic.List<T>();
            Traverse(root, matches);
            return matches;
        }

        private static void Traverse<T>(DependencyObject root, System.Collections.Generic.ICollection<T> matches)
            where T : DependencyObject
        {
            if (root is T typed)
            {
                matches.Add(typed);
            }

            int childCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                Traverse(System.Windows.Media.VisualTreeHelper.GetChild(root, index), matches);
            }
        }

        private static MenuItem? FindMenuItem(ImageViewer.Controls.ImageViewer viewer, ImageViewerViewMenuCommandTag tag)
        {
            var contextMenu = viewer.mainContextMenu;
            return FindMenuItem(contextMenu.Items.OfType<MenuItem>(), tag);
        }

        private static MenuItem? FindMenuItem(System.Collections.Generic.IEnumerable<MenuItem> menuItems, ImageViewerViewMenuCommandTag tag)
        {
            foreach (MenuItem menuItem in menuItems)
            {
                if (ReferenceEquals(menuItem.Tag, tag))
                {
                    return menuItem;
                }

                MenuItem? child = FindMenuItem(menuItem.Items.OfType<MenuItem>(), tag);
                if (child != null)
                {
                    return child;
                }
            }

            return null;
        }
    }
}
