using System;
using System.Windows;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal sealed class RoiSelectionStateController
    {
        private readonly IRoiSelectionStateHost _host;

        public RoiSelectionStateController(IRoiSelectionStateHost host)
        {
            _host = host;
        }

        public void HandleSelectedRoiChanged()
        {
            RoiBase? selectedRoi = _host.SelectedRoi;
            FrameworkElement? editor = ResolvePropertyEditor(selectedRoi);
            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(selectedRoi, editor != null);

            ApplyPropertyPanel(editor, state.ShowPropertyPanel);
            ApplyDetectionRefresh(selectedRoi, state.RefreshKind);
            _host.DrawRois();
        }

        public void RefreshPropertyPanel()
        {
            RoiBase? selectedRoi = _host.SelectedRoi;
            FrameworkElement? editor = ResolvePropertyEditor(selectedRoi);
            RoiSelectionState state = RoiSelectionStateEvaluator.Evaluate(selectedRoi, editor != null);
            ApplyPropertyPanel(editor, state.ShowPropertyPanel);
        }

        public void HandleRoiEdited(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);

            ApplyDetectionRefresh(roi, RoiSelectionStateEvaluator.GetRefreshKind(roi));
            if (ReferenceEquals(_host.SelectedRoi, roi))
            {
                RefreshPropertyPanel();
            }

            _host.DrawRois();
        }

        private FrameworkElement? ResolvePropertyEditor(RoiBase? selectedRoi)
        {
            return selectedRoi == null ? null : _host.CreatePropertyEditor(selectedRoi);
        }

        private void ApplyPropertyPanel(FrameworkElement? editor, bool showPropertyPanel)
        {
            _host.ApplyPropertyPanel(editor, showPropertyPanel);
        }

        private void ApplyDetectionRefresh(RoiBase? roi, RoiSelectionRefreshKind refreshKind)
        {
            if (roi != null && refreshKind != RoiSelectionRefreshKind.None)
            {
                _host.ApplyCaliperDetection(roi);
            }
        }
    }
}
