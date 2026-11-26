using System.Linq;
using System.Windows.Controls;
using ImageViewer.Controls;
using ImageViewer.Localization;
using ImageViewer.Plugins;
using Xunit;

namespace ImageViewerControl.Tests;

[Collection(WpfTestCollection.Name)]
[Trait("Category", "Smoke")]
[Trait("Category", "Wpf")]
public sealed class MeasurementMenuStructureTests
{
    [Fact]
    public void FrequentCommandsRemainAtTheTopLevelAndSelectedShortcutsKeepTheirCommands()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            MenuItem[] rootItems = viewer.mainContextMenu.Items.OfType<MenuItem>().ToArray();

            Assert.Contains(viewer.openImageMenuItem, rootItems);
            Assert.Contains(viewer.saveSessionMenuItem, rootItems);
            Assert.Contains(viewer.undoMenuItem, rootItems);
            Assert.Contains(viewer.redoMenuItem, rootItems);
            Assert.Contains(viewer.fitToViewMenuItem, rootItems);
            Assert.Contains(viewer.actualSizeMenuItem, rootItems);
            Assert.Contains(viewer.resetViewMenuItem, rootItems);
            Assert.DoesNotContain(rootItems, item => Equals(item.Header, UiText.Get("MenuPrimaryActions")));

            MenuItem[] selectedShortcuts = viewer.contextRoiShortcutsMenuItem.Items.OfType<MenuItem>().ToArray();
            Assert.Contains(viewer.editPropertiesMenuItem, rootItems);
            Assert.Contains(viewer.setLabelMenuItem, selectedShortcuts);
            Assert.Contains(viewer.setColorMenuItem, selectedShortcuts);
            Assert.Contains(viewer.editCaliperSettingsMenuItem, selectedShortcuts);
            Assert.Contains(viewer.zoomToSelectionMenuItem, selectedShortcuts);
        });
    }

    [Fact]
    public void ImageDisplayAndPerformanceCommandsAreSeparated()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            MenuItem imageDisplay = FindRootMenu(viewer, UiText.Get("MenuImageDisplay"));
            MenuItem performance = FindRootMenu(viewer, UiText.Get("MenuPerformance"));
            MenuItem imageAdjustment = imageDisplay.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, UiText.Get("MenuImageAdjustment")));
            MenuItem displayOverlays = imageDisplay.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, UiText.Get("MenuDisplayOverlays")));
            MenuItem advancedPerformance = performance.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, UiText.Get("MenuAdvancedPerformance")));
            MenuItem pseudoColorStrategy = advancedPerformance.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, UiText.Get("MenuPseudoColorStrategy")));

            Assert.Contains(viewer.pseudoColorMenuItem, imageAdjustment.Items.OfType<MenuItem>());
            Assert.Contains(viewer.showHistogramMenuItem, displayOverlays.Items.OfType<MenuItem>());
            Assert.Contains(viewer.pauseRealtimeHistogramMenuItem, displayOverlays.Items.OfType<MenuItem>());
            Assert.Contains(viewer.showProfileMenuItem, displayOverlays.Items.OfType<MenuItem>());
            Assert.Contains(viewer.pauseRealtimeProfileMenuItem, displayOverlays.Items.OfType<MenuItem>());
            Assert.Contains(viewer.showToolbarMenuItem, displayOverlays.Items.OfType<MenuItem>());
            Assert.Contains(viewer.showRoiListMenuItem, displayOverlays.Items.OfType<MenuItem>());
            Assert.Contains(viewer.enableImagePyramidMenuItem, performance.Items.OfType<MenuItem>());
            Assert.Contains(viewer.showRenderStatusMenuItem, performance.Items.OfType<MenuItem>());
            Assert.Contains(viewer.preferShaderPseudoColorMenuItem, pseudoColorStrategy.Items.OfType<MenuItem>());
            Assert.DoesNotContain(performance.Items.OfType<MenuItem>(), item => ReferenceEquals(item, viewer.pseudoColorMenuItem));
        });
    }

    [Fact]
    public void MenuTaxonomyKeepsRelatedCommandsTogether()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            MenuItem fileMenu = FindRootMenu(viewer, UiText.Get("MenuFileAndProjects"));
            MenuItem measureMenu = FindRootMenu(viewer, UiText.Get("MenuMeasure"));
            MenuItem analysisMenu = FindRootMenu(viewer, UiText.Get("MenuAnalysisAndExport"));
            MenuItem performance = FindRootMenu(viewer, UiText.Get("MenuPerformance"));

            // Calibration lives with file-level setup, not with ROI editing.
            MenuItem calibration = fileMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, UiText.Get("MenuCalibration")));
            Assert.Contains(viewer.calibratePixelsMenuItem, calibration.Items.OfType<MenuItem>());
            Assert.Contains(viewer.checkCalibrationReadinessMenuItem, calibration.Items.OfType<MenuItem>());
            Assert.DoesNotContain(
                viewer.roiOperationsMenuItem.Items.OfType<MenuItem>(),
                item => ReferenceEquals(item, viewer.calibratePixelsMenuItem));

            // Recipe workflow commands belong to the measurement menu.
            MenuItem recipe = measureMenu.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, UiText.Get("MenuRecipe")));
            Assert.Contains(viewer.saveRecipeMenuItem, recipe.Items.OfType<MenuItem>());
            Assert.Contains(viewer.loadRecipeMenuItem, recipe.Items.OfType<MenuItem>());
            Assert.DoesNotContain(fileMenu.Items.OfType<MenuItem>(), item => ReferenceEquals(item, viewer.saveRecipeMenuItem));

            // Advanced measurements are a measurement entry, not an export command.
            Assert.Contains(viewer.advancedMeasurementsMenuItem, measureMenu.Items.OfType<MenuItem>());
            Assert.DoesNotContain(analysisMenu.Items.OfType<MenuItem>(), item => ReferenceEquals(item, viewer.advancedMeasurementsMenuItem));

            // The four zoom commands sit together at the top level.
            MenuItem[] rootItems = viewer.mainContextMenu.Items.OfType<MenuItem>().ToArray();
            Assert.Contains(viewer.fitToViewMenuItem, rootItems);
            Assert.Contains(viewer.fillToViewMenuItem, rootItems);
            Assert.Contains(viewer.actualSizeMenuItem, rootItems);
            Assert.Contains(viewer.resetViewMenuItem, rootItems);

            // Cache maintenance is separate from rendering switches.
            MenuItem cacheMaintenance = performance.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, UiText.Get("MenuCacheMaintenance")));
            Assert.Contains(viewer.clearPyramidCacheMenuItem, cacheMaintenance.Items.OfType<MenuItem>());
            Assert.Contains(viewer.clearAnalysisCacheMenuItem, cacheMaintenance.Items.OfType<MenuItem>());
        });
    }

    [Fact]
    public void MeasurementMenuHasFiveTaskGroupsAndEveryAdvancedDescriptor()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            WpfTestRunner.InvokePrivate(viewer, "PopulateMeasurementMenu", viewer.AvailableDrawingTools);
            WpfTestRunner.InvokePrivate(viewer, "PopulateMeasurementMenu", viewer.AvailableDrawingTools);

            string[] groupKeys =
            [
                "MenuMeasurementBasic",
                "MenuMeasurementEdge",
                "MenuMeasurementGeometry",
                "MenuMeasurementShape",
                "MenuMeasurementArray"
            ];
            MenuItem[] groups = viewer.measureMenuItem.Items.OfType<MenuItem>().ToArray();
            foreach (string groupKey in groupKeys)
            {
                Assert.Contains(groups, item => Equals(item.Header, UiText.Get(groupKey)));
            }

            MenuItem[] advancedItems = Flatten(viewer.measureMenuItem).Where(item => item.Tag is ImageViewerAdvancedMeasurementMenuTag).ToArray();
            Assert.Equal(AdvancedMeasurementMenuCatalog.All.Count, advancedItems.Length);
            Assert.Equal(AdvancedMeasurementMenuCatalog.All.Count, advancedItems.Select(item => ((ImageViewerAdvancedMeasurementMenuTag)item.Tag).Kind).Distinct().Count());
            Assert.All(advancedItems.Where(item => !item.IsEnabled), item => Assert.False(string.IsNullOrWhiteSpace(item.ToolTip?.ToString())));
            Assert.Contains(groups.SelectMany(group => group.Items.OfType<MenuItem>()), item => ReferenceEquals(item, viewer.gradientDetectMenuItem));
        });
    }

    [Fact]
    public void BlobAnalysisIsDetectedAsMeasurementAndSitsInTheEdgeGroup()
    {
        WpfTestRunner.Run(() =>
        {
            using var viewer = new ImageViewer.Controls.ImageViewer();
            WpfTestRunner.InvokePrivate(viewer, "PopulateMeasurementMenu", viewer.AvailableDrawingTools);

            string blobHeader = UiText.Get("ToolBlobAnalysis");
            RoiToolDescriptor blobTool = Assert.Single(
                viewer.AvailableDrawingTools.Where(tool => string.Equals(tool.Header, blobHeader, System.StringComparison.Ordinal)));
            Assert.True(blobTool.IsMeasurement);

            MenuItem edgeGroup = viewer.measureMenuItem.Items.OfType<MenuItem>()
                .Single(item => Equals(item.Header, UiText.Get("MenuMeasurementEdge")));
            Assert.Contains(edgeGroup.Items.OfType<MenuItem>(), item => Equals(item.Header, blobHeader));

            // Detection tools must no longer clutter the drawing menu.
            Assert.DoesNotContain(viewer.drawRoiMenuItem.Items.OfType<MenuItem>(), item => Equals(item.Header, blobHeader));
        });
    }

    private static MenuItem FindRootMenu(ImageViewer.Controls.ImageViewer viewer, string header)
        => viewer.mainContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));

    private static IEnumerable<MenuItem> Flatten(ItemsControl items)
    {
        foreach (object item in items.Items)
        {
            if (item is not MenuItem menuItem)
            {
                continue;
            }

            yield return menuItem;
            foreach (MenuItem nested in Flatten(menuItem))
            {
                yield return nested;
            }
        }
    }
}
