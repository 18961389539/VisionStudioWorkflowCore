using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Localization;

namespace ImageViewer.Dialogs
{
    /// <summary>
    /// 卡尺设置对话框公共基类
    /// Chinese: 集中窗口骨架（标题、参数主区、高级折叠区、实时预览、预览/确定/取消按钮条）、
    /// 输入解析辅助与“预览即应用、确定即提交”流程；派生类只声明自己的字段与 TryApplyInputs。
    /// English: Shared chrome, parsing helpers and the apply-then-commit flow for caliper settings dialogs.
    /// </summary>
    internal abstract class CaliperSettingsDialogBase : Window
    {
        private readonly string _titleKey;
        private readonly object _model;
        private readonly Action<object>? _previewAction;
        private readonly CheckBox _livePreviewCheckBox;

        /// <summary>
        /// 构造卡尺设置对话框骨架
        /// Chinese: 建立标题、参数主区、高级折叠区、说明、实时预览与按钮条；model 为确定时提交的模型实例，
        /// previewAction 收到派生类克隆出的模型用于实时预览。
        /// English: Builds the shared chrome; model is committed on OK while previewAction receives a clone for live preview.
        /// </summary>
        protected CaliperSettingsDialogBase(
            string titleKey,
            string headingKey,
            string descriptionKey,
            double width,
            double height,
            object model,
            Action<object>? previewAction)
        {
            _titleKey = titleKey;
            _model = model;
            _previewAction = previewAction;
            Title = UiText.Get(titleKey);
            Width = width;
            Height = height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get(headingKey),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            });

            PrimaryPanel = new StackPanel();
            panel.Children.Add(PrimaryPanel);

            AdvancedPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(new Expander
            {
                Header = UiText.Get("DialogAdvancedParameters"),
                IsExpanded = false,
                Margin = new Thickness(0, 6, 0, 0),
                Content = AdvancedPanel
            });

            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get(descriptionKey),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                Margin = new Thickness(0, 8, 0, 12)
            });

            _livePreviewCheckBox = new CheckBox
            {
                Content = UiText.Get("DialogLivePreview"),
                IsChecked = true,
                Margin = new Thickness(0, 0, 0, 8)
            };
            _livePreviewCheckBox.Checked += (_, _) => PreviewCurrentValues();
            panel.Children.Add(_livePreviewCheckBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var previewButton = new Button { Content = UiText.Get("DialogPreviewNow"), Width = 82, Margin = new Thickness(0, 0, 8, 0) };
            previewButton.Click += (_, _) => PreviewCurrentValues();
            var okButton = new Button { Content = UiText.Get("DialogButtonOk"), Width = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            okButton.Click += OnOkClick;
            var cancelButton = new Button { Content = UiText.Get("DialogButtonCancel"), Width = 72, IsCancel = true };
            cancelButton.Click += (_, _) => Close();
            buttonPanel.Children.Add(previewButton);
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            };
        }

        /// <summary>主参数区面板。</summary>
        protected StackPanel PrimaryPanel { get; }

        /// <summary>高级参数区面板（已放进 Expander）。</summary>
        protected StackPanel AdvancedPanel { get; }

        /// <summary>确定成功后为被应用的模型实例，取消/未确定为 null。</summary>
        protected object? AppliedModel { get; private set; }

        /// <summary>
        /// 解析输入并写回模型
        /// Chinese: 校验失败时返回 false 并给出提示文本；成功时把控件值写回模型。
        /// English: Returns false with an error message on invalid input; otherwise writes control values back to the model.
        /// </summary>
        protected abstract bool TryApplyInputs(out string errorMessage);

        /// <summary>
        /// 克隆模型用于预览
        /// Chinese: 通常返回 _model.Clone()，供实时预览使用，避免预览直接改动待提交模型。
        /// English: Usually returns _model.Clone() so preview never mutates the model pending commit.
        /// </summary>
        protected abstract object CloneModelForPreview();

        /// <summary>
        /// 往面板追加“标签 + 文本框”字段
        /// Chinese: 统一字段版式并返回文本框，供派生类声明自己的字段控件。
        /// English: Appends a label + text box field and returns the text box.
        /// </summary>
        protected static TextBox AddField(Panel panel, string label, string value)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            var textBox = new TextBox { Text = value };
            panel.Children.Add(textBox);
            return textBox;
        }

        /// <summary>为文本框注册实时预览：文本变化且勾选实时预览时触发。</summary>
        protected void EnableLivePreview(TextBox textBox)
        {
            textBox.TextChanged += (_, _) =>
            {
                if (_livePreviewCheckBox?.IsChecked == true)
                {
                    PreviewCurrentValues();
                }
            };
        }

        /// <summary>为下拉框注册实时预览：选择变化且勾选实时预览时触发。</summary>
        protected void EnableLivePreview(ComboBox comboBox)
        {
            comboBox.SelectionChanged += (_, _) =>
            {
                if (_livePreviewCheckBox?.IsChecked == true)
                {
                    PreviewCurrentValues();
                }
            };
        }

        /// <summary>解析不小于 minValue 的整数。</summary>
        protected static bool TryParsePositiveInt(string text, out int value, int minValue)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= minValue;
        }

        /// <summary>解析非负整数。</summary>
        protected static bool TryParseNonNegativeInt(string text, out int value)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        /// <summary>解析非负浮点数。</summary>
        protected static bool TryParseNonNegativeDouble(string text, out double value)
        {
            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        protected readonly record struct CommonInputs(
            int Count, int SearchRange, int SamplingHalfWidth, double EdgeSigma,
            double MinimumGradient, int MinimumValidCalipers, double OutlierThreshold);

        protected sealed class CommonFields
        {
            private readonly TextBox _count;
            private readonly TextBox _searchRange;
            private readonly TextBox _samplingHalfWidth;
            private readonly TextBox _edgeSigma;
            private readonly TextBox _minimumGradient;
            private readonly TextBox _minimumValidCalipers;
            private readonly TextBox _outlierThreshold;

            public CommonFields(CaliperSettingsDialogBase dialog, CommonInputs values)
            {
                _count = AddNumberField(dialog.PrimaryPanel, "CaliperFieldCount", values.Count);
                _searchRange = AddNumberField(dialog.PrimaryPanel, "CaliperFieldSearchRange", values.SearchRange);
                _samplingHalfWidth = AddNumberField(dialog.AdvancedPanel, "CaliperFieldSamplingHalfWidth", values.SamplingHalfWidth);
                _edgeSigma = AddNumberField(dialog.AdvancedPanel, "CaliperFieldEdgeSigma", values.EdgeSigma);
                _minimumGradient = AddNumberField(dialog.AdvancedPanel, "CaliperFieldMinimumGradient", values.MinimumGradient);
                _minimumValidCalipers = AddNumberField(dialog.AdvancedPanel, "CaliperFieldMinimumValid", values.MinimumValidCalipers);
                _outlierThreshold = AddNumberField(dialog.AdvancedPanel, "CaliperFieldOutlierThreshold", values.OutlierThreshold);

                TextBox AddNumberField<T>(Panel panel, string labelKey, T value) where T : IFormattable
                {
                    TextBox textBox = AddField(panel, UiText.Get(labelKey), value.ToString(null, CultureInfo.InvariantCulture));
                    dialog.EnableLivePreview(textBox);
                    return textBox;
                }
            }

            public bool TryRead(int minimumCount, int minimumValidCount, out CommonInputs values)
            {
                values = default;
                if (!TryParsePositiveInt(_count.Text, out int count, minimumCount) ||
                    !TryParsePositiveInt(_searchRange.Text, out int searchRange, minValue: 1) ||
                    !TryParseNonNegativeInt(_samplingHalfWidth.Text, out int samplingHalfWidth) ||
                    !TryParseNonNegativeDouble(_edgeSigma.Text, out double edgeSigma) || edgeSigma is < 0.5 or > 5.0 ||
                    !TryParseNonNegativeDouble(_minimumGradient.Text, out double minimumGradient) ||
                    !TryParsePositiveInt(_minimumValidCalipers.Text, out int minimumValidCalipers, minimumValidCount) ||
                    !TryParseNonNegativeDouble(_outlierThreshold.Text, out double outlierThreshold))
                {
                    return false;
                }

                values = new CommonInputs(count, searchRange, samplingHalfWidth, edgeSigma,
                    minimumGradient, minimumValidCalipers, outlierThreshold);
                return true;
            }
        }

        private void OnOkClick(object? sender, RoutedEventArgs e)
        {
            if (!TryApplyInputs(out string errorMessage))
            {
                MessageBox.Show(this, errorMessage, UiText.Get(_titleKey), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PreviewCurrentValues();
            AppliedModel = _model;
            DialogResult = true;
            Close();
        }

        private void PreviewCurrentValues()
        {
            if (!TryApplyInputs(out _))
            {
                return;
            }

            _previewAction?.Invoke(CloneModelForPreview());
        }
    }
}
