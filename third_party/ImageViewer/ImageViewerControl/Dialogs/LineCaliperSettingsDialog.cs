using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Localization;
using ImageViewer.Models;

namespace ImageViewer.Dialogs
{
    /// <summary>
    /// 直线卡尺设置对话框
    /// Chinese: 在基类骨架上声明直线卡尺的单边检测参数字段，并把控件值写回 LineCaliperMeasureRoi。
    /// English: Declares single-edge caliper fields on the shared base chrome and writes them back to LineCaliperMeasureRoi.
    /// </summary>
    internal sealed class LineCaliperSettingsDialog : CaliperSettingsDialogBase
    {
        private readonly LineCaliperMeasureRoi _model;
        private readonly CommonFields _commonFields;
        private readonly TextBox _edgeSelectionTextBox;
        private readonly TextBox _fitClippingEndPointsTextBox;
        private readonly ComboBox _polarityComboBox;
        private readonly ComboBox _lineFitModeComboBox;

        public LineCaliperSettingsDialog(LineCaliperMeasureRoi model, Action<LineCaliperMeasureRoi>? previewAction)
            : base(
                "LineCaliperDialogTitle",
                "LineCaliperDialogHeading",
                "LineCaliperDialogDescription",
                360,
                450,
                model,
                preview => previewAction?.Invoke((LineCaliperMeasureRoi)preview))
        {
            _model = model;

            _commonFields = new CommonFields(this, new CommonInputs(
                _model.CaliperCount, _model.CaliperSearchRange, _model.CaliperSamplingHalfWidth,
                _model.CaliperEdgeSigma, _model.CaliperMinimumGradient, _model.MinimumValidCalipers,
                _model.CaliperOutlierThreshold));

            _edgeSelectionTextBox = AddField(AdvancedPanel, UiText.Get("CaliperFieldEdgeSelection"), _model.EdgeSelection.ToString(CultureInfo.InvariantCulture));
            _fitClippingEndPointsTextBox = AddField(AdvancedPanel, UiText.Get("CaliperFieldFitClippingEndPoints"), _model.CaliperFitClippingEndPoints.ToString(CultureInfo.InvariantCulture));

            AdvancedPanel.Children.Add(new TextBlock { Text = UiText.Get("CaliperFieldPolarity"), Margin = new Thickness(0, 8, 0, 2) });
            _polarityComboBox = new ComboBox
            {
                ItemsSource = Enum.GetValues<CaliperEdgePolarity>(),
                SelectedItem = _model.CaliperEdgePolarity,
                Margin = new Thickness(0, 0, 0, 4)
            };
            AdvancedPanel.Children.Add(_polarityComboBox);

            AdvancedPanel.Children.Add(new TextBlock { Text = UiText.Get("CaliperFieldLineFitMode"), Margin = new Thickness(0, 8, 0, 2) });
            _lineFitModeComboBox = new ComboBox
            {
                ItemsSource = Enum.GetValues<LineFitMode>(),
                SelectedItem = _model.CaliperLineFitMode,
                Margin = new Thickness(0, 0, 0, 4)
            };
            AdvancedPanel.Children.Add(_lineFitModeComboBox);

            EnableLivePreview(_edgeSelectionTextBox);
            EnableLivePreview(_fitClippingEndPointsTextBox);
            EnableLivePreview(_polarityComboBox);
            EnableLivePreview(_lineFitModeComboBox);
        }

        public LineCaliperMeasureRoi? Result => AppliedModel as LineCaliperMeasureRoi;

        protected override object CloneModelForPreview() => _model.Clone();

        protected override bool TryApplyInputs(out string errorMessage)
        {
            errorMessage = UiText.Get("LineCaliperDialogInvalidMessage");
            if (!_commonFields.TryRead(minimumCount: 6, minimumValidCount: 3, out CommonInputs common) ||
                !TryParsePositiveInt(_edgeSelectionTextBox.Text, out int edgeSelection, minValue: 1) ||
                !TryParseNonNegativeInt(_fitClippingEndPointsTextBox.Text, out int fitClippingEndPoints) ||
                _polarityComboBox.SelectedItem is not CaliperEdgePolarity polarity ||
                _lineFitModeComboBox.SelectedItem is not LineFitMode lineFitMode)
            {
                return false;
            }

            _model.CaliperCount = common.Count;
            _model.CaliperSearchRange = common.SearchRange;
            _model.CaliperSamplingHalfWidth = common.SamplingHalfWidth;
            _model.CaliperEdgeSigma = common.EdgeSigma;
            _model.CaliperMinimumGradient = common.MinimumGradient;
            _model.MinimumValidCalipers = common.MinimumValidCalipers;
            _model.CaliperOutlierThreshold = common.OutlierThreshold;
            _model.EdgeSelection = Math.Clamp(edgeSelection, 1, 8);
            _model.CaliperFitClippingEndPoints = fitClippingEndPoints;
            _model.CaliperEdgePolarity = polarity;
            _model.CaliperLineFitMode = lineFitMode;
            return true;
        }
    }
}
