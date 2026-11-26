using ImageViewer.Controls;
using ImageViewer.Models;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class RoiSelectionStateEvaluatorTests
    {
        [Fact]
        public void Evaluate_WithNoSelection_HidesPropertyPanelAndSkipsRefresh()
        {
            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(selectedRoi: null, hasPropertyEditor: false);

            Assert.False(state.ShowPropertyPanel);
            Assert.Equal(RoiSelectionRefreshKind.None, state.RefreshKind);
        }

        [Fact]
        public void Evaluate_WithSelectedRoiAndPropertyEditor_ShowsPropertyPanel()
        {
            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(new RotatedRect(), hasPropertyEditor: true);

            Assert.True(state.ShowPropertyPanel);
            Assert.Equal(RoiSelectionRefreshKind.None, state.RefreshKind);
        }

        [Fact]
        public void Evaluate_WithSelectedRoiAndNoPropertyEditor_HidesPropertyPanel()
        {
            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(new RotatedRect(), hasPropertyEditor: false);

            Assert.False(state.ShowPropertyPanel);
            Assert.Equal(RoiSelectionRefreshKind.None, state.RefreshKind);
        }

        [Fact]
        public void Evaluate_WithUndetectedDualEdgeCaliper_RequestsDualEdgeRefresh()
        {
            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(new CaliperMeasureRoi(), hasPropertyEditor: false);

            Assert.Equal(RoiSelectionRefreshKind.Caliper, state.RefreshKind);
        }

        [Fact]
        public void Evaluate_WithUndetectedLineCaliper_RequestsLineRefresh()
        {
            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(new LineCaliperMeasureRoi(), hasPropertyEditor: false);

            Assert.Equal(RoiSelectionRefreshKind.LineCaliper, state.RefreshKind);
        }

        [Fact]
        public void Evaluate_WithUndetectedCircularCaliper_RequestsCircularRefresh()
        {
            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(new CircularCaliperMeasureRoi(), hasPropertyEditor: false);

            Assert.Equal(RoiSelectionRefreshKind.CircularCaliper, state.RefreshKind);
        }

        [Fact]
        public void Evaluate_WithDetectedCaliper_DoesNotRequestRefresh()
        {
            var roi = new CaliperMeasureRoi
            {
                HasDetectedEdges = true
            };

            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(roi, hasPropertyEditor: false);

            Assert.Equal(RoiSelectionRefreshKind.None, state.RefreshKind);
        }
    }
}