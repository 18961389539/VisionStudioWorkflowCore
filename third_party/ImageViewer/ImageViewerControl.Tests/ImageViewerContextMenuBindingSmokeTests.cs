using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Controls;
using ImageViewer.Localization;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Smoke")]
    [Trait("Category", "Wpf")]
    public class ImageViewerContextMenuBindingSmokeTests
    {
        [Fact]
        public void ViewCommandClick_UpdatesBoundMenuCheckState()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var menuItem = viewer.showInfoPanelMenuItem;
                viewer.ShowInfoPanel = false;

                WpfTestRunner.InvokePrivate(viewer, "OnViewCommandMenuClick", new MenuItem { Tag = ImageViewerViewMenuTags.ToggleInfoPanel }, new RoutedEventArgs());
                WpfTestRunner.DrainDispatcher();

                Assert.True(viewer.ShowInfoPanel);
                Assert.True(menuItem.IsChecked);
            });
        }

        [Fact]
        public void AnalysisCommandClick_UpdatesPseudoColorBinding()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var pseudoColorMenu = viewer.pseudoColorMenuItem;

                WpfTestRunner.InvokePrivate(viewer, "OnAnalysisCommandMenuClick", new MenuItem { Tag = ImageViewerAnalysisMenuTags.SetPseudoColorPaletteHot }, new RoutedEventArgs());
                WpfTestRunner.DrainDispatcher();

                MenuItem hotItem = pseudoColorMenu.Items.OfType<MenuItem>().Single(item => ReferenceEquals(item.Tag, ImageViewerAnalysisMenuTags.SetPseudoColorPaletteHot));
                MenuItem noneItem = pseudoColorMenu.Items.OfType<MenuItem>().Single(item => ReferenceEquals(item.Tag, ImageViewerAnalysisMenuTags.SetPseudoColorPaletteNone));

                Assert.Equal(ImageViewer.Services.PseudoColorPalette.Hot, viewer.PseudoColorPalette);
                Assert.True(hotItem.IsChecked);
                Assert.False(noneItem.IsChecked);
            });
        }

        [Fact]
        public void XamlMenuItems_UseStronglyTypedCommandTags()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var viewMenuItem = viewer.showInfoPanelMenuItem;
                var analysisMenuItem = viewer.showRenderStatusMenuItem;
                var roiMenuItem = viewer.undoMenuItem;
                var fileMenuItem = viewer.saveSessionMenuItem;
                var featureMenuItem = viewer.exportSnapshotMenuItem;

                Assert.IsType<ImageViewerViewMenuCommandTag>(viewMenuItem.Tag);
                Assert.IsType<ImageViewerAnalysisMenuCommandTag>(analysisMenuItem.Tag);
                Assert.IsType<ImageViewerRoiMenuCommandTag>(roiMenuItem.Tag);
                Assert.IsType<ImageViewerFileMenuCommandTag>(fileMenuItem.Tag);
                Assert.IsType<ImageViewerFeatureMenuCommandTag>(featureMenuItem.Tag);
                Assert.DoesNotContain(new[] { viewMenuItem.Tag, analysisMenuItem.Tag, roiMenuItem.Tag, fileMenuItem.Tag, featureMenuItem.Tag }, tag => tag is string);
            });
        }

        [Fact]
        public void DynamicMenuItems_UseStronglyTypedTags()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var drawMenuItem = viewer.drawRoiMenuItem;

                WpfTestRunner.InvokePrivate(viewer, "RefreshRoiDrawingMenuItems");
                WpfTestRunner.InvokePrivate(viewer, "UpdateContextMenuState");
                WpfTestRunner.DrainDispatcher();

                MenuItem firstDrawingTool = drawMenuItem.Items.OfType<MenuItem>().First();
                var menuState = Assert.IsType<ImageViewerMenuStateSnapshot>(viewer.MenuState);

                Assert.IsType<ImageViewerRoiToolMenuTag>(firstDrawingTool.Tag);
                Assert.All(menuState.File.RecentProjects, item => Assert.IsType<ImageViewerRecentProjectMenuTag>(item.Tag));
            });
        }

        [Fact]
        public void ContextMenuStateBinding_EnablesImageCommandsWhenImageLoaded()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var fitToViewMenuItem = viewer.fitToViewMenuItem;
                var exportSnapshotMenuItem = viewer.exportSnapshotMenuItem;

                WpfTestRunner.InvokePrivate(viewer, "UpdateContextMenuState");
                WpfTestRunner.DrainDispatcher();

                Assert.False(fitToViewMenuItem.IsEnabled);
                Assert.False(exportSnapshotMenuItem.IsEnabled);

                viewer.SetImage(CreateBitmap());
                WpfTestRunner.InvokePrivate(viewer, "UpdateContextMenuState");
                WpfTestRunner.DrainDispatcher();

                Assert.True(fitToViewMenuItem.IsEnabled);
                Assert.True(exportSnapshotMenuItem.IsEnabled);
            });
        }

        [Fact]
        public void ViewMenuItems_ShowImplementedKeyboardShortcuts()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var resetViewMenuItem = viewer.resetViewMenuItem;
                var openImageMenuItem = viewer.openImageMenuItem;

                Assert.Equal("Ctrl+O", openImageMenuItem.InputGestureText);
                Assert.Equal("Ctrl+0", resetViewMenuItem.InputGestureText);
                Assert.Equal("Ctrl+F", FindMenuItem(viewer, ImageViewerViewMenuTags.FitToView).InputGestureText);
                Assert.Equal("Ctrl+Shift+R", FindMenuItem(viewer, ImageViewerViewMenuTags.RotateLeft).InputGestureText);
                Assert.Equal("Ctrl+R", FindMenuItem(viewer, ImageViewerViewMenuTags.RotateRight).InputGestureText);
                Assert.Equal("Ctrl+H", FindMenuItem(viewer, ImageViewerViewMenuTags.FlipHorizontal).InputGestureText);
                Assert.Equal("Ctrl+Shift+H", FindMenuItem(viewer, ImageViewerViewMenuTags.FlipVertical).InputGestureText);
            });
        }

        [Fact]
        public void MenuSearch_NoMatchingCommand_ShowsFeedbackAndEscClearsSearch()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var searchBox = viewer.menuSearchBox;
                var noResultsText = viewer.menuSearchNoResultsText;

                searchBox.Text = "no-such-command";
                WpfTestRunner.DrainDispatcher();

                Assert.Equal(Visibility.Visible, noResultsText.Visibility);

                WpfTestRunner.InvokePrivate(viewer, "ClearMenuSearchOrClose");

                Assert.Equal(string.Empty, searchBox.Text);
                Assert.Equal(Visibility.Collapsed, noResultsText.Visibility);
            });
        }

        [Fact]
        public void StatusFeedback_ExposesAccessibleNamesAndLiveAnnouncements()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var dismissButton = viewer.diagnosticErrorDismissButton;
                var diagnosticText = viewer.diagnosticErrorTextBlock;
                var diagnosticIcon = viewer.diagnosticErrorIconTextBlock;
                var loadStatus = viewer.imageLoadStatusTextBlock;

                Assert.Equal(UiText.Get("MenuDismissError"), AutomationProperties.GetName(dismissButton));
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(diagnosticText));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(loadStatus));
                Assert.Equal("⚠", diagnosticIcon.Text);
                Assert.Same(viewer.FindResource("ViewerErrorSoftBrush"), viewer.diagnosticErrorBorder.Background);
                Assert.Same(viewer.FindResource("ViewerErrorBorderBrush"), viewer.diagnosticErrorBorder.BorderBrush);
            });
        }

        [Fact]
        public void DestructiveRoiCommands_AdvertiseUndoRecovery()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var deleteMenuItem = viewer.deleteRoiMenuItem;
                var clearAllMenuItem = viewer.clearAllMenuItem;

                Assert.Equal(UiText.Get("RoiDestructiveActionUndoHint"), deleteMenuItem.ToolTip);
                Assert.Equal(UiText.Get("RoiClearAllUndoHint"), clearAllMenuItem.ToolTip);
            });
        }

        private static MenuItem FindMenuItem(ImageViewer.Controls.ImageViewer viewer, ImageViewerViewMenuCommandTag tag)
        {
            var contextMenu = viewer.mainContextMenu;
            return FindMenuItem(contextMenu.Items.OfType<MenuItem>(), tag)
                ?? throw new Xunit.Sdk.XunitException("Menu item was not found.");
        }

        private static MenuItem? FindMenuItem(IEnumerable<MenuItem> menuItems, ImageViewerViewMenuCommandTag tag)
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

        private static BitmapSource CreateBitmap()
        {
            return BitmapSource.Create(
                pixelWidth: 1,
                pixelHeight: 1,
                dpiX: 96,
                dpiY: 96,
                pixelFormat: PixelFormats.Gray8,
                palette: null,
                pixels: new byte[] { 0 },
                stride: 1);
        }
    }
}
