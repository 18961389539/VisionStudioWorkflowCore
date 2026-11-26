using ImageViewer.Controls;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class ImageViewerContextMenuStateEvaluatorTests
    {
        [Fact]
        public void Evaluate_WithoutSelectionOrContent_DisablesRoiAndSessionActions()
        {
            ImageViewerContextMenuState state = ImageViewerContextMenuStateEvaluator.Evaluate(
                new ImageViewerContextMenuStateInput(
                    CanUndo: false,
                    CanRedo: false,
                    HasSelection: false,
                    HasRois: false,
                    HasDrawingTools: false,
                    HasEditableProperties: false,
                    CanCalibratePixels: false,
                    CanEditCaliperSettings: false,
                    CanRunGradientDetection: false,
                        HasImage: false));

            Assert.False(state.UndoEnabled);
            Assert.False(state.DeleteSelectedEnabled);
            Assert.False(state.EditPropertiesEnabled);
            Assert.False(state.ExportSnapshotEnabled);
                    Assert.False(state.ExportAnalysisCsvEnabled);
        }

        [Fact]
        public void Evaluate_WithSelectedCaliperCapabilities_EnablesRoiActions()
        {
            ImageViewerContextMenuState state = ImageViewerContextMenuStateEvaluator.Evaluate(
                new ImageViewerContextMenuStateInput(
                    CanUndo: true,
                    CanRedo: true,
                    HasSelection: true,
                    HasRois: true,
                    HasDrawingTools: true,
                    HasEditableProperties: true,
                    CanCalibratePixels: true,
                    CanEditCaliperSettings: true,
                    CanRunGradientDetection: true,
                    HasImage: true));

            Assert.True(state.UndoEnabled);
            Assert.True(state.RedoEnabled);
            Assert.True(state.DeleteSelectedEnabled);
            Assert.True(state.ClearAllEnabled);
            Assert.True(state.DrawRoiEnabled);
            Assert.True(state.EditPropertiesEnabled);
            Assert.True(state.SetLabelEnabled);
            Assert.True(state.SetColorEnabled);
            Assert.True(state.CalibratePixelsEnabled);
            Assert.True(state.EditCaliperSettingsEnabled);
            Assert.True(state.GradientDetectEnabled);
        }

        [Fact]
        public void Evaluate_WithImageAndRois_EnablesContentActions()
        {
            ImageViewerContextMenuState state = ImageViewerContextMenuStateEvaluator.Evaluate(
                new ImageViewerContextMenuStateInput(
                    CanUndo: false,
                    CanRedo: false,
                    HasSelection: false,
                    HasRois: true,
                    HasDrawingTools: true,
                    HasEditableProperties: false,
                    CanCalibratePixels: false,
                    CanEditCaliperSettings: false,
                    CanRunGradientDetection: false,
                    HasImage: true));

            Assert.True(state.ExportSnapshotEnabled);
            Assert.True(state.ExportAnalysisCsvEnabled);
            Assert.True(state.ShowAnalysisSummaryEnabled);
        }

        [Fact]
        public void CanRunGradientDetection_MatchesImplementedRoiTypes()
        {
            Assert.True(ContextMenuController.CanRunGradientDetection(new CaliperMeasureRoi(), hasAnalysisBitmap: true));
            Assert.True(ContextMenuController.CanRunGradientDetection(new CircularCaliperMeasureRoi(), hasAnalysisBitmap: true));
            Assert.False(ContextMenuController.CanRunGradientDetection(new LineCaliperMeasureRoi(), hasAnalysisBitmap: true));
            Assert.False(ContextMenuController.CanRunGradientDetection(new CaliperMeasureRoi(), hasAnalysisBitmap: false));
        }
    }
}