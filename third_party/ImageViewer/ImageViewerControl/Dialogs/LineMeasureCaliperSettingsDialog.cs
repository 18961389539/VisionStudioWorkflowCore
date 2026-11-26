using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Localization;
using ImageViewer.Models;

namespace ImageViewer.Dialogs
{
    /// <summary>
    /// 直线测量卡尺设置对话框
    /// Chinese: 在基类骨架上声明直线测量卡尺的参数字段，并把控件值写回 CaliperMeasureRoi。
    /// English: Declares line-measure caliper fields on the shared base chrome and writes them back to CaliperMeasureRoi.
    /// </summary>
    internal sealed class LineMeasureCaliperSettingsDialog : CaliperSettingsDialogBase
    {
        private readonly CaliperMeasureRoi _model;
        private readonly CommonFields _commonFields;
        private readonly TextBox _regionLengthTextBox;
        private readonly TextBox _minimumEdgeGapTextBox;
        private readonly TextBox _nominalEdgeGapTextBox;
        private readonly TextBox _nominalEdgeGapToleranceTextBox;
        private readonly TextBox _fitClippingEndPointsTextBox;
        private readonly ComboBox _polarityComboBox;
        private readonly ComboBox _lineFitModeComboBox;

        public LineMeasureCaliperSettingsDialog(CaliperMeasureRoi model, Action<CaliperMeasureRoi>? previewAction)
            : base(
                "LineMeasureCaliperDialogTitle",
                "LineMeasureCaliperDialogHeading",
                "LineMeasureCaliperDialogDescription",
                360,
                480,
                model,
                preview => previewAction?.Invoke((CaliperMeasureRoi)preview))
        {
            _model = model;

            _commonFields = new CommonFields(this, new CommonInputs(
                _model.CaliperCount, _model.CaliperSearchRange, _model.CaliperSamplingHalfWidth,
                _model.CaliperEdgeSigma, _model.CaliperMinimumGradient, _model.MinimumValidCalipers,
                _model.CaliperOutlierThreshold));
            _regionLengthTextBox = AddField(PrimaryPanel, UiText.Get("CaliperFieldRegionLength"), _model.CaliperRegionLength.ToString(CultureInfo.InvariantCulture));

            _minimumEdgeGapTextBox = AddField(AdvancedPanel, UiText.Get("CaliperFieldMinimumEdgeGap"), _model.MinimumEdgeGap.ToString(CultureInfo.InvariantCulture));
            _nominalEdgeGapTextBox = AddField(AdvancedPanel, UiText.Get("CaliperFieldNominalEdgeGap"), _model.NominalEdgeGap.ToString(CultureInfo.InvariantCulture));
            _nominalEdgeGapToleranceTextBox = AddField(AdvancedPanel, UiText.Get("CaliperFieldNominalEdgeGapTolerance"), _model.NominalEdgeGapTolerance.ToString(CultureInfo.InvariantCulture));
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

            EnableLivePreview(_regionLengthTextBox);
            EnableLivePreview(_minimumEdgeGapTextBox);
            EnableLivePreview(_nominalEdgeGapTextBox);
            EnableLivePreview(_nominalEdgeGapToleranceTextBox);
            EnableLivePreview(_fitClippingEndPointsTextBox);
            EnableLivePreview(_polarityComboBox);
            EnableLivePreview(_lineFitModeComboBox);
        }

        public CaliperMeasureRoi? Result => AppliedModel as CaliperMeasureRoi;

        protected override object CloneModelForPreview() => _model.Clone();

        protected override bool TryApplyInputs(out string errorMessage)
        {
            errorMessage = UiText.Get("LineMeasureCaliperDialogInvalidMessage");
            if (!_commonFields.TryRead(minimumCount: 3, minimumValidCount: 2, out CommonInputs common) ||
                !TryParseNonNegativeDouble(_regionLengthTextBox.Text, out double regionLength) ||
                !TryParseNonNegativeDouble(_minimumEdgeGapTextBox.Text, out double minimumEdgeGap) ||
                !TryParseNonNegativeDouble(_nominalEdgeGapTextBox.Text, out double nominalEdgeGap) ||
                !TryParseNonNegativeDouble(_nominalEdgeGapToleranceTextBox.Text, out double nominalEdgeGapTolerance) ||
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
            _model.CaliperRegionLength = regionLength;
            _model.CaliperMinimumGradient = common.MinimumGradient;
            _model.MinimumValidCalipers = common.MinimumValidCalipers;
            _model.CaliperOutlierThreshold = common.OutlierThreshold;
            _model.MinimumEdgeGap = minimumEdgeGap;
            _model.NominalEdgeGap = nominalEdgeGap;
            _model.NominalEdgeGapTolerance = nominalEdgeGapTolerance;
            _model.CaliperFitClippingEndPoints = fitClippingEndPoints;
            _model.CaliperEdgePolarity = polarity;
            _model.CaliperLineFitMode = lineFitMode;
            return true;
        }
    }
}
