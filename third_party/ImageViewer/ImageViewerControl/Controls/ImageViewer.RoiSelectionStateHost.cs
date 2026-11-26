using System.Windows;
using System.Windows.Controls;
using System;
using ImageViewer.Models;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerRoiSelectionStateHost : IRoiSelectionStateHost
    {
        private readonly ImageViewer _owner;
        private readonly ContentControl _propertyEditorHost;
        private readonly FrameworkElement _propertyPanel;
        private readonly Action<RoiBase> _applyCaliperDetection;
        private readonly Action _drawRois;

        public ImageViewerRoiSelectionStateHost(
            ImageViewer owner,
            ContentControl propertyEditorHost,
            FrameworkElement propertyPanel,
            Action<RoiBase> applyCaliperDetection,
            Action drawRois)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _propertyEditorHost = propertyEditorHost ?? throw new ArgumentNullException(nameof(propertyEditorHost));
            _propertyPanel = propertyPanel ?? throw new ArgumentNullException(nameof(propertyPanel));
            _applyCaliperDetection = applyCaliperDetection ?? throw new ArgumentNullException(nameof(applyCaliperDetection));
            _drawRois = drawRois ?? throw new ArgumentNullException(nameof(drawRois));
        }

        public RoiBase? SelectedRoi => _owner.ViewerState.SelectedRoi;

        public FrameworkElement? CreatePropertyEditor(RoiBase roi)
        {
            return _owner.PluginRegistry.FindByRoi(roi)?.CreatePropertyEditor(roi);
        }

        public void ApplyPropertyPanel(FrameworkElement? editor, bool isVisible)
        {
            _propertyEditorHost.Content = editor;
            _propertyPanel.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            _owner.circularCaliperSettingsButton.Visibility = isVisible && SelectedRoi is CircularCaliperMeasureRoi
                ? Visibility.Visible
                : Visibility.Collapsed;
            _owner.infoPanel.Visibility = isVisible ? Visibility.Collapsed : (_owner.ShowInfoPanel ? Visibility.Visible : Visibility.Collapsed);
            _owner.UpdateInteractionModeVisuals();
        }

        public void ApplyCaliperDetection(RoiBase roi) => _applyCaliperDetection(roi);

        public void DrawRois()
        {
            _drawRois();
        }
    }
}
