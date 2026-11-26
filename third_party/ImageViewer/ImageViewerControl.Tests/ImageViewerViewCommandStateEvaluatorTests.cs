using ImageViewer.Controls;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerViewCommandStateEvaluatorTests
    {
        [Fact]
        public void Evaluate_WithoutImage_DisablesViewportCommandsAndPreservesToggleChecks()
        {
            ImageViewerViewCommandMenuState state = ImageViewerViewCommandStateEvaluator.Evaluate(
                new ImageViewerViewCommandStateInput(
                    HasImage: false,
                    HasSelection: false,
                    ShowPixelGrid: true,
                    ShowCrosshair: false,
                    ShowCaliperScores: true,
                    ShowInfoPanel: false,
                    ShowHistogram: true,
                    ShowProfile: false,
                    ShowScaleBar: true,
                    ShowRoiList: false,
                    ShowToolbar: true,
                    ShowSnapGrid: true,
                    EnableSnapToGrid: false));

            Assert.Equal(
                new ImageViewerViewCommandMenuState(
                    FitToViewEnabled: false,
                    ActualSizeEnabled: false,
                    ResetViewEnabled: false,
                    ZoomToSelectionEnabled: false,
                    ShowPixelGridChecked: true,
                    ShowCrosshairChecked: false,
                    ShowCaliperScoresChecked: true,
                    ShowInfoPanelChecked: false,
                    ShowHistogramChecked: true,
                    ShowProfileChecked: false,
                    ShowScaleBarChecked: true,
                    ShowRoiListChecked: false,
                    ShowToolbarChecked: true,
                    ShowSnapGridChecked: true,
                    EnableSnapToGridChecked: false),
                state);
        }

        [Fact]
        public void Evaluate_WithImageAndSelection_EnablesAllViewCommands()
        {
            ImageViewerViewCommandMenuState state = ImageViewerViewCommandStateEvaluator.Evaluate(
                new ImageViewerViewCommandStateInput(
                    HasImage: true,
                    HasSelection: true,
                    ShowPixelGrid: false,
                    ShowCrosshair: false,
                    ShowCaliperScores: false,
                    ShowInfoPanel: false,
                    ShowHistogram: false,
                    ShowProfile: false,
                    ShowScaleBar: false,
                    ShowRoiList: false,
                    ShowToolbar: false,
                    ShowSnapGrid: false,
                    EnableSnapToGrid: false));

            Assert.True(state.FitToViewEnabled);
            Assert.True(state.ActualSizeEnabled);
            Assert.True(state.ResetViewEnabled);
            Assert.True(state.ZoomToSelectionEnabled);
        }

        [Fact]
        public void Evaluate_WithImageAndNoSelection_DisablesZoomOnly()
        {
            ImageViewerViewCommandMenuState state = ImageViewerViewCommandStateEvaluator.Evaluate(
                new ImageViewerViewCommandStateInput(
                    HasImage: true,
                    HasSelection: false,
                    ShowPixelGrid: false,
                    ShowCrosshair: false,
                    ShowCaliperScores: false,
                    ShowInfoPanel: false,
                    ShowHistogram: false,
                    ShowProfile: false,
                    ShowScaleBar: false,
                    ShowRoiList: false,
                    ShowToolbar: false,
                    ShowSnapGrid: false,
                    EnableSnapToGrid: false));

            Assert.True(state.FitToViewEnabled);
            Assert.True(state.ActualSizeEnabled);
            Assert.True(state.ResetViewEnabled);
            Assert.False(state.ZoomToSelectionEnabled);
        }
    }
}