using System.Threading.Tasks;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    [Collection(WpfTestCollection.Name)]
    public class ImageViewerCommandHostAdapterTests
    {
        [Fact]
        public void ViewCommandController_ToggleInfoPanel_UpdatesViewerProperty()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                ImageViewerViewCommandController controller = GetComposition(viewer).ViewCommandController;
                bool initial = viewer.ShowInfoPanel;

                controller.Execute(ImageViewerViewCommand.ToggleInfoPanel);

                Assert.NotEqual(initial, viewer.ShowInfoPanel);
            });
        }

        [Fact]
        public void AnalysisCommandHostAdapter_CommandsUpdateViewerState()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                ImageViewerControlComposition composition = GetComposition(viewer);
                ImageViewerAnalysisState analysisState = viewer._analysisState;
                int updateRenderedImageCount = 0;
                int smartDisplaySuggestionCount = 0;
                var controller = new ImageViewerAnalysisCommandController(
                    new ImageViewerAnalysisCommandHostAdapter(
                        new ImageViewerAnalysisOptionsAdapter(viewer),
                        new CountingAnalysisOperations(
                            viewer,
                            () => updateRenderedImageCount++,
                            () => smartDisplaySuggestionCount++,
                            analysisState,
                            composition.AnalysisController)));
                bool initialGpuRendering = viewer.EnableGpuRendering;

                controller.Execute(ImageViewerAnalysisCommand.ToggleGpuRendering);
                controller.Execute(ImageViewerAnalysisCommand.SetPseudoColorPaletteHot);
                controller.Execute(ImageViewerAnalysisCommand.ShowSmartDisplaySuggestion);

                Assert.NotEqual(initialGpuRendering, viewer.EnableGpuRendering);
                Assert.Equal(1, updateRenderedImageCount);
                Assert.Equal(PseudoColorPalette.Hot, viewer.PseudoColorPalette);
                Assert.Equal(1, smartDisplaySuggestionCount);
            });
        }

        [Fact]
        public void RoiMenuCommandHostAdapter_EditProperties_UsesSelectedRoi()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                ImageViewerControlComposition composition = GetComposition(viewer);
                var selectedRoi = new CircleRoi();
                RoiBase? shownRoi = null;
                var controller = new ImageViewerRoiMenuCommandController(
                    new ImageViewerRoiMenuCommandHostAdapter(
                        new ImageViewerRoiSelectionCapability(viewer),
                        new TestRoiEditingCapability(roi => shownRoi = roi, _ => { }),
                        new NoOpContextMenuCapability()));

                viewer.ViewerState.SelectedRoi = selectedRoi;
                controller.Execute(ImageViewerRoiMenuCommand.EditProperties);

                Assert.Same(selectedRoi, shownRoi);
            });
        }

        [Fact]
        public void RoiMenuCommandHostAdapter_EditCaliperSettings_UsesSelectedRoi()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                ImageViewerControlComposition composition = GetComposition(viewer);
                var selectedRoi = new LineCaliperMeasureRoi();
                RoiBase? shownRoi = null;
                var controller = new ImageViewerRoiMenuCommandController(
                    new ImageViewerRoiMenuCommandHostAdapter(
                        new ImageViewerRoiSelectionCapability(viewer),
                        new TestRoiEditingCapability(_ => { }, roi => shownRoi = roi),
                        new NoOpContextMenuCapability()));

                viewer.ViewerState.SelectedRoi = selectedRoi;
                controller.Execute(ImageViewerRoiMenuCommand.EditCaliperSettings);

                Assert.Same(selectedRoi, shownRoi);
            });
        }

        [Fact]
        public void FileMenuCommandHostAdapter_ToggleAutoSave_UsesSessionController()
        {
            WpfTestRunner.Run(() =>
            {
                using var viewer = new ImageViewer.Controls.ImageViewer();
                ImageViewerControlComposition composition = GetComposition(viewer);
                var controller = new ImageViewerFileMenuCommandController(
                    new ImageViewerFileMenuCommandHostAdapter(
                        new ImageViewerFileDialogCapability(composition.DialogWorkflowService.OpenImageAsync),
                        new ImageViewerFileOperationsCapability(composition.SessionController, composition.RoiPersistenceController),
                        new NoOpContextMenuCapability()));
                bool initial = composition.SessionController.IsAutoSaveEnabled;

                controller.ExecuteAsync(ImageViewerFileMenuCommand.ToggleAutoSave).GetAwaiter().GetResult();

                Assert.NotEqual(initial, composition.SessionController.IsAutoSaveEnabled);
            });
        }

        private static ImageViewerControlComposition GetComposition(ImageViewer.Controls.ImageViewer viewer)
        {
            return viewer._controlComposition;
        }

        private sealed class CountingAnalysisOperations : IImageViewerAnalysisOperations
        {
            private readonly ImageViewer.Controls.ImageViewer _viewer;
            private readonly Action _onUpdateRenderedImage;
            private readonly Action _onShowSuggestion;
            private readonly ImageViewerAnalysisState _analysisState;
            private readonly ImageViewerAnalysisCoordinator _coordinator;

            public CountingAnalysisOperations(
                ImageViewer.Controls.ImageViewer viewer,
                Action onUpdateRenderedImage,
                Action onShowSuggestion,
                ImageViewerAnalysisState analysisState,
                ImageViewerAnalysisCoordinator coordinator)
            {
                _viewer = viewer;
                _onUpdateRenderedImage = onUpdateRenderedImage;
                _onShowSuggestion = onShowSuggestion;
                _analysisState = analysisState;
                _coordinator = coordinator;
            }

            public void UpdateRenderedImage() => _onUpdateRenderedImage();
            public void RefreshAnalysis() => _coordinator.HandleRefreshAnalysisRequested();
            public void ClearAnalysisCache() => _coordinator.HandleClearAnalysisCacheRequested();
            public void ResetPyramidToBaseLevel() => _analysisState.ResetPyramidToBaseLevel();
            public void RebuildPyramidIfNeeded() { }
            public void SetPseudoColorPalette(PseudoColorPalette palette) => _viewer.PseudoColorPalette = palette;
            public void ShowSmartDisplaySuggestion() => _onShowSuggestion();
            public void ShowDisplayAdjustment() { }
            public void ResetDisplayAdjustment() { }
            public void ShowRenderStatus() { }
        }

        private sealed class NoOpContextMenuCapability : IImageViewerContextMenuCapability
        {
            public void UpdateContextMenuState() { }
        }

        private sealed class TestRoiEditingCapability : IImageViewerRoiEditingCapability
        {
            private readonly Action<RoiBase> _showProperties;
            private readonly Action<RoiBase> _showCaliperSettings;

            public TestRoiEditingCapability(Action<RoiBase> showProperties, Action<RoiBase> showCaliperSettings)
            {
                _showProperties = showProperties;
                _showCaliperSettings = showCaliperSettings;
            }

            public void Undo() { }
            public void Redo() { }
            public void DeleteSelected() { }
            public void ClearAll() { }
            public void SetSelectedLabel() { }
            public void SetSelectedColor(RoiColor color) { }
            public void CalibrateSelectedRoi() { }
            public void ShowProperties(RoiBase roi) => _showProperties(roi);
            public void ShowCaliperSettings(RoiBase roi) => _showCaliperSettings(roi);
        }
    }
}
