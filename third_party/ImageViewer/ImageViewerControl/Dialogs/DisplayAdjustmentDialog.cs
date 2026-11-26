using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ImageViewer.Core.Display;
using ImageViewer.Localization;

namespace ImageViewer.Dialogs
{
    /// <summary>
    /// 显示调节对话框（窗宽/窗位、亮度、对比度、伽马、反色）。
    /// Chinese: 窗宽窗位以**显示码级数**为单位（8 位 = 0..255），因为共用的传递曲线就定义在这 256 级上；
    /// 亮度/对比度以百分比给出（±100% 对应内部的 ±1.0）。拖动即通过 <paramref name="previewAction"/> 预览，
    /// 取消由调用方负责还原——对话框只负责产出参数，不碰渲染。
    /// English: Produces a <see cref="DisplayAdjustment"/> and previews it through a callback; applying and reverting
    /// stay with the owner.
    /// </summary>
    internal sealed class DisplayAdjustmentDialog : Window
    {
        /// <summary>显示码满量程（0..255）。</summary>
        private const double FullScaleLevels = 255.0;

        private readonly NumberField _windowWidth;
        private readonly NumberField _windowLevel;
        private readonly NumberField _brightness;
        private readonly NumberField _contrast;
        private readonly NumberField _gamma;
        private readonly CheckBox _invert;
        private readonly TextBlock _errorText;
        private readonly Func<DisplayAdjustment?>? _autoProvider;
        private readonly Action<DisplayAdjustment>? _previewAction;
        private bool _isSyncing;

        public DisplayAdjustmentDialog(
            DisplayAdjustment initial,
            Func<DisplayAdjustment?>? autoProvider = null,
            Action<DisplayAdjustment>? previewAction = null)
        {
            ArgumentNullException.ThrowIfNull(initial);
            _autoProvider = autoProvider;
            _previewAction = previewAction;

            Title = UiText.Get("DisplayAdjustmentDialogTitle");
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;

            var panel = new StackPanel { Margin = new Thickness(12), MinWidth = 380 };
            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get("DisplayAdjustmentDialogHint"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 10)
            });

            _windowWidth = AddField(panel, "DisplayAdjustmentWindowWidthLabel", 1, FullScaleLevels, ToLevels(initial.WindowWidth), "0.#");
            _windowLevel = AddField(panel, "DisplayAdjustmentWindowLevelLabel", 0, FullScaleLevels, ToLevels(initial.WindowLevel), "0.#");
            _brightness = AddField(panel, "DisplayAdjustmentBrightnessLabel", -100, 100, initial.Brightness * 100.0, "0.#");
            _contrast = AddField(panel, "DisplayAdjustmentContrastLabel", -100, 100, initial.Contrast * 100.0, "0.#");
            _gamma = AddField(panel, "DisplayAdjustmentGammaLabel", 0.1, 4.0, initial.Gamma, "0.00");

            _invert = new CheckBox
            {
                Content = UiText.Get("DisplayAdjustmentInvertLabel"),
                IsChecked = initial.Invert,
                Margin = new Thickness(0, 6, 0, 0)
            };
            _invert.Checked += (_, _) => Preview();
            _invert.Unchecked += (_, _) => Preview();
            panel.Children.Add(_invert);

            _errorText = new TextBlock
            {
                Foreground = Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            panel.Children.Add(_errorText);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            if (_autoProvider != null)
            {
                var autoButton = new Button { Content = UiText.Get("DisplayAdjustmentAutoButton"), MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
                autoButton.Click += (_, _) => ApplyAutoWindowLevel();
                buttons.Children.Add(autoButton);
            }

            var resetButton = new Button { Content = UiText.Get("DisplayAdjustmentResetButton"), MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
            resetButton.Click += (_, _) => Apply(DisplayAdjustment.Identity);
            buttons.Children.Add(resetButton);

            var okButton = new Button { Content = UiText.Get("DialogButtonOk"), MinWidth = 60, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            okButton.Click += (_, _) =>
            {
                if (TryBuild(out DisplayAdjustment? adjustment))
                {
                    Result = adjustment;
                    DialogResult = true;
                    Close();
                }
            };
            buttons.Children.Add(okButton);

            var cancelButton = new Button { Content = UiText.Get("DialogButtonCancel"), MinWidth = 60, IsCancel = true };
            cancelButton.Click += (_, _) =>
            {
                DialogResult = false;
                Close();
            };
            buttons.Children.Add(cancelButton);
            panel.Children.Add(buttons);

            Content = panel;
            Result = initial;
        }

        /// <summary>点确定时的调节参数（打开时为初值）。</summary>
        public DisplayAdjustment Result { get; private set; }

        private NumberField AddField(StackPanel panel, string labelKey, double min, double max, double initial, string format)
        {
            var field = new NumberField(UiText.Get(labelKey), min, max, initial, format);
            field.Changed += OnFieldChanged;
            panel.Children.Add(field.Host);
            return field;
        }

        private void OnFieldChanged(object? sender, EventArgs e) => Preview();

        private void Preview()
        {
            if (_isSyncing)
            {
                return;
            }

            if (TryBuild(out DisplayAdjustment? adjustment))
            {
                _errorText.Text = string.Empty;
                _previewAction?.Invoke(adjustment);
            }
            else
            {
                _errorText.Text = UiText.Get("DisplayAdjustmentDialogInvalidRange");
            }
        }

        private void Apply(DisplayAdjustment adjustment)
        {
            _isSyncing = true;
            _windowWidth.Value = ToLevels(adjustment.WindowWidth);
            _windowLevel.Value = ToLevels(adjustment.WindowLevel);
            _brightness.Value = adjustment.Brightness * 100.0;
            _contrast.Value = adjustment.Contrast * 100.0;
            _gamma.Value = adjustment.Gamma;
            _invert.IsChecked = adjustment.Invert;
            _isSyncing = false;
            Preview();
        }

        private void ApplyAutoWindowLevel()
        {
            DisplayAdjustment? suggestion = _autoProvider?.Invoke();
            if (suggestion == null)
            {
                _errorText.Text = UiText.Get("DisplayAdjustmentAutoNeedsImage");
                return;
            }

            // 自动窗宽窗位只覆盖窗口，保留用户已经设好的亮度/对比度/伽马/反色。
            Apply(suggestion with
            {
                Brightness = _brightness.Value / 100.0,
                Contrast = _contrast.Value / 100.0,
                Gamma = _gamma.Value,
                Invert = _invert.IsChecked == true
            });
        }

        private bool TryBuild([NotNullWhen(true)] out DisplayAdjustment? adjustment)
        {
            adjustment = new DisplayAdjustment
            {
                WindowWidth = _windowWidth.Value / FullScaleLevels,
                WindowLevel = _windowLevel.Value / FullScaleLevels,
                Brightness = _brightness.Value / 100.0,
                Contrast = _contrast.Value / 100.0,
                Gamma = _gamma.Value,
                Invert = _invert.IsChecked == true
            };

            if (adjustment.TryValidate(out _))
            {
                return true;
            }

            adjustment = null;
            return false;
        }

        private static double ToLevels(double normalized) => Math.Round(Math.Clamp(normalized, 0d, 1d) * FullScaleLevels);

        /// <summary>一行"滑块 + 数值输入"控件。滑块保证取值范围，文本框保证能输精确值。</summary>
        private sealed class NumberField
        {
            private readonly Slider _slider;
            private readonly TextBox _textBox;

            public NumberField(string label, double min, double max, double initial, string format)
            {
                Minimum = min;
                Maximum = max;
                Format = format;

                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
                panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 2) });

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                _textBox = new TextBox
                {
                    Width = 60,
                    TextAlignment = TextAlignment.Right,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                _slider = new Slider { Minimum = min, Maximum = max, IsSnapToTickEnabled = false, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(_slider, 0);
                Grid.SetColumn(_textBox, 1);
                row.Children.Add(_slider);
                row.Children.Add(_textBox);
                panel.Children.Add(row);
                Host = panel;

                _slider.ValueChanged += (_, _) =>
                {
                    if (!_isSyncing)
                    {
                        CommitSlider();
                    }
                };
                _textBox.LostFocus += (_, _) => CommitText();
                _textBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        CommitText();
                    }
                };

                Value = initial;
            }

            public event EventHandler? Changed;

            public FrameworkElement Host { get; }

            public double Minimum { get; }

            public double Maximum { get; }

            private string Format { get; }

            /// <summary>当前值（已裁剪到 [Minimum,Maximum]）。</summary>
            /// <remarks>setter 走"外部改值"路径（重置/自动），不再回调 <see cref="Changed"/>，由调用方统一触发预览。</remarks>
            public double Value
            {
                get => _slider.Value;
                set
                {
                    _isSyncing = true;
                    _slider.Value = Math.Clamp(value, Minimum, Maximum);
                    _textBox.Text = _slider.Value.ToString(Format, CultureInfo.CurrentCulture);
                    _isSyncing = false;
                }
            }

            private bool _isSyncing;

            private void CommitSlider()
            {
                _textBox.Text = _slider.Value.ToString(Format, CultureInfo.CurrentCulture);
                Changed?.Invoke(this, EventArgs.Empty);
            }

            private void CommitText()
            {
                if (!double.TryParse(_textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) &&
                    !double.TryParse(_textBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                {
                    // 认不出的输入按"用户只是敲了字"处理：回退到滑块当前值，不静默吃掉也不报错。
                    _textBox.Text = _slider.Value.ToString(Format, CultureInfo.CurrentCulture);
                    return;
                }

                double clamped = Math.Clamp(parsed, Minimum, Maximum);
                if (clamped == _slider.Value)
                {
                    _textBox.Text = _slider.Value.ToString(Format, CultureInfo.CurrentCulture);
                    return;
                }

                Value = clamped;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
