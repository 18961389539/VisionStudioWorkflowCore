using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImageViewer.Controls;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewerDemo;
using ImageViewerDemo.Localization;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    [Trait("Category", "Integration")]
    [Trait("Category", "Wpf")]
    public class ImageViewerUiIntegrationTests
    {
        [Fact]
        public void ContextMenu_GroupsAdvancedCommandsIntoNestedMenus()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var contextMenu = viewer.mainContextMenu;

                MenuItem analysisMenu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuAnalysisAndExport")));
                MenuItem performanceMenu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuPerformance")));
                MenuItem displayMenu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuImageDisplay")));

                Assert.Contains(analysisMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuAnalysisRun")));
                MenuItem advancedPerformance = performanceMenu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, UiText.Get("MenuAdvancedPerformance")));
                Assert.Contains(advancedPerformance.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuPseudoColorStrategy")));
                Assert.Contains(displayMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuImageAdjustment")));
                Assert.Contains(displayMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuDisplayOverlays")));
            });
        }

        [Fact]
        public void ContextMenu_GroupsRoiAndMeasurementCommandsIntoOneSection()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                var contextMenu = viewer.mainContextMenu;

                MenuItem drawMenu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuDraw")));
                MenuItem measureMenu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuMeasure")));
                MenuItem roiMenu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuRoiOperations")));
                MenuItem currentObjectMenu = contextMenu.Items.OfType<MenuItem>().Single(item => item.Name == "contextRoiShortcutsMenuItem");
                MenuItem edgeMenu = measureMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuMeasurementEdge")));
                MenuItem shapeMenu = measureMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuMeasurementShape")));

                MenuItem displayMenu = contextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuImageDisplay")));
                MenuItem overlaysMenu = displayMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiText.Get("MenuDisplayOverlays")));

                Assert.Contains(drawMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("ToolRotatedRect")));
                Assert.Contains(overlaysMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuShowRoiList")));
                Assert.Contains(currentObjectMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuZoomToSelectedRoi")));
                Assert.Contains(edgeMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuGradientAlignCaliper")));
                Assert.Contains(shapeMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("ToolFittedEllipse")));
                Assert.DoesNotContain(roiMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuGradientAlignCaliper")));
                Assert.DoesNotContain(roiMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("MenuDraw")));
            });
        }

        [Theory]
        [InlineData("line-measure", "LineMeasureCaliperDialogTitle")]
        [InlineData("line", "LineCaliperDialogTitle")]
        [InlineData("circular", "CircularCaliperDialogTitle")]
        public void CaliperDialogs_CollapseAdvancedParameters(string dialogKind, string titleKey)
        {
            WpfTestRunner.Run(() =>
            {
                Window dialog = CreateCaliperDialog(dialogKind);
                try
                {
                    dialog.Show();
                    WpfTestRunner.DrainDispatcher();

                    Expander expander = FindVisualDescendants<Expander>(dialog).Single();
                    Button previewButton = FindVisualDescendants<Button>(dialog).Single(button => Equals(button.Content, UiText.Get("DialogPreviewNow")));

                    Assert.Equal(UiText.Get(titleKey), dialog.Title);
                    Assert.Equal(UiText.Get("DialogAdvancedParameters"), expander.Header);
                    Assert.False(expander.IsExpanded);
                    Assert.True(previewButton.IsVisible);
                }
                finally
                {
                    dialog.Close();
                    WpfTestRunner.DrainDispatcher();
                }
            });
        }

        [Theory]
        [InlineData("line-measure", 3)]
        [InlineData("line", 6)]
        [InlineData("circular", 6)]
        public void CaliperDialogs_LivePreviewRequiresValidSigmaAndToolSpecificCount(string dialogKind, int minimumCount)
        {
            WpfTestRunner.Run(() =>
            {
                var previews = new List<RoiBase>();
                Window dialog = CreateCaliperDialog(dialogKind, previews.Add);
                try
                {
                    var root = (Panel)((ScrollViewer)dialog.Content).Content;
                    var advanced = (Panel)root.Children.OfType<Expander>().Single().Content;
                    TextBox count = FindCaliperField(root.Children.OfType<StackPanel>().First(), "CaliperFieldCount");
                    TextBox sigma = FindCaliperField(advanced, "CaliperFieldEdgeSigma");

                    sigma.Text = "0.49";
                    count.Text = (minimumCount - 1).ToString(CultureInfo.InvariantCulture);
                    sigma.Text = "0.5";
                    Assert.Empty(previews);

                    count.Text = minimumCount.ToString(CultureInfo.InvariantCulture);
                    RoiBase preview = Assert.Single(previews);
                    var values = preview switch
                    {
                        CaliperMeasureRoi dual => (dual.CaliperCount, dual.CaliperEdgeSigma),
                        ISingleEdgeCaliperRoi single => (single.CaliperCount, single.CaliperEdgeSigma),
                        _ => throw new InvalidOperationException("Expected a caliper ROI.")
                    };
                    Assert.Equal((minimumCount, 0.5), values);

                    sigma.Text = "5.01";
                    Assert.Single(previews);
                }
                finally
                {
                    dialog.Close();
                }
            });
        }

        [Fact]
        public void RoiDisplayName_UsesLocalizedTypeNames()
        {
            var roi = new CircleRoi();

            Assert.Equal(UiText.Get("RoiDisplayCircle"), roi.DisplayName);

            roi.Label = "检查点";

            Assert.Equal($"{UiText.Get("RoiDisplayCircle")}: 检查点", roi.DisplayName);
        }

        [Fact]
        public void DemoMainWindow_UsesLocalizedEntryPointCopy()
        {
            WpfTestRunner.Run(() =>
            {
                var window = new MainWindow();
                try
                {
                    window.Show();
                    WpfTestRunner.DrainDispatcher();

                    Assert.Equal(DemoText.Get("WindowTitle"), window.Title);
                    Assert.Contains(FindVisualDescendants<TextBlock>(window), textBlock => textBlock.Text == DemoText.Get("WorkspaceTitle"));
                    Assert.Contains(FindVisualDescendants<Button>(window), button => Equals(button.Content, DemoText.Get("OpenImageButton")));
                }
                finally
                {
                    window.Close();
                    WpfTestRunner.DrainDispatcher();
                }
            });
        }

        private static TextBox FindCaliperField(Panel panel, string labelKey)
        {
            TextBlock label = panel.Children.OfType<TextBlock>().Single(item => item.Text == UiText.Get(labelKey));
            return (TextBox)panel.Children[panel.Children.IndexOf(label) + 1];
        }

        private static Window CreateCaliperDialog(string dialogKind, Action<RoiBase>? preview = null)
        {
            return dialogKind switch
            {
                "line-measure" => new LineMeasureCaliperSettingsDialog(new CaliperMeasureRoi(), preview),
                "line" => new LineCaliperSettingsDialog(new LineCaliperMeasureRoi(), preview),
                "circular" => new CircularCaliperSettingsDialog(new CircularCaliperMeasureRoi(), preview),
                _ => throw new ArgumentOutOfRangeException(nameof(dialogKind), dialogKind, null)
            };
        }

        private static IReadOnlyList<T> FindVisualDescendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            var matches = new List<T>();
            Traverse(root, matches);
            return matches;
        }

        private static void Traverse<T>(DependencyObject root, ICollection<T> matches)
            where T : DependencyObject
        {
            if (root is T typed)
            {
                matches.Add(typed);
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                Traverse(VisualTreeHelper.GetChild(root, index), matches);
            }
        }
    }
}
