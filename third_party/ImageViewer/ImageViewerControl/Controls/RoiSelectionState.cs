using System.Windows;
using System.Windows.Controls;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal interface IRoiSelectionStateHost
    {
        RoiBase? SelectedRoi { get; }

        FrameworkElement? CreatePropertyEditor(RoiBase roi);

        void ApplyPropertyPanel(FrameworkElement? editor, bool isVisible);

        void ApplyCaliperDetection(RoiBase roi);

        void DrawRois();
    }

    internal enum RoiSelectionRefreshKind
    {
        None,
        Caliper,
        LineCaliper,
        CircularCaliper
    }

    internal readonly record struct RoiSelectionState(bool ShowPropertyPanel, RoiSelectionRefreshKind RefreshKind);

    internal static class RoiSelectionStateEvaluator
    {
        public static RoiSelectionState Evaluate(RoiBase? selectedRoi, bool hasPropertyEditor)
        {
            return new RoiSelectionState(
                ShowPropertyPanel: selectedRoi != null && hasPropertyEditor,
                RefreshKind: GetRefreshKind(selectedRoi));
        }

        public static RoiSelectionRefreshKind GetRefreshKind(RoiBase? roi)
        {
            return roi switch
            {
                CaliperMeasureRoi { HasDetectedEdges: false } => RoiSelectionRefreshKind.Caliper,
                LineCaliperMeasureRoi { HasDetectedLine: false } => RoiSelectionRefreshKind.LineCaliper,
                CircularCaliperMeasureRoi { HasDetectedEdges: false } => RoiSelectionRefreshKind.CircularCaliper,
                _ => RoiSelectionRefreshKind.None
            };
        }
    }
}
