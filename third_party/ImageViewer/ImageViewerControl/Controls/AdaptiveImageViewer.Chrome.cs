using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class AdaptiveImageViewer : UserControl, IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// 控件主题（默认深色）。
        /// Chinese: 只影响本控件自身的配色；内部承载的 <see cref="ImageViewer"/>/<see cref="Volume3DViewer"/> 会同步。
        /// English: Theme of this control; the nested viewers are kept in sync.
        /// </summary>
        public static readonly DependencyProperty ThemeProperty =
            DependencyProperty.Register(
                nameof(Theme),
                typeof(ViewerThemes.Theme),
                typeof(AdaptiveImageViewer),
                new PropertyMetadata(ViewerThemes.Theme.Dark, OnThemeChanged));

        /// <summary>自定义主题字典；非 null 时优先于 <see cref="Theme"/>。</summary>
        public static readonly DependencyProperty ThemeDictionaryProperty =
            DependencyProperty.Register(
                nameof(ThemeDictionary),
                typeof(ResourceDictionary),
                typeof(AdaptiveImageViewer),
                new PropertyMetadata(null, OnThemeChanged));

        /// <summary>控件主题（默认深色）。</summary>
        public ViewerThemes.Theme Theme
        {
            get => (ViewerThemes.Theme)GetValue(ThemeProperty);
            set => SetValue(ThemeProperty, value);
        }

        /// <summary>自定义主题字典；非 null 时优先于 <see cref="Theme"/>。</summary>
        public ResourceDictionary? ThemeDictionary
        {
            get => (ResourceDictionary?)GetValue(ThemeDictionaryProperty);
            set => SetValue(ThemeDictionaryProperty, value);
        }

        private static void OnThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AdaptiveImageViewer viewer)
            {
                viewer.ApplyTheme();
            }
        }

        private void ApplyTheme()
        {
            if (!IsInitialized)
            {
                return;
            }

            ResourceDictionary dictionary = ThemeDictionary ?? ViewerThemes.CreateDictionary(Theme);
            ViewerThemeSwitcher.Apply(this, dictionary);

            // 内部承载的查看器与自适应面板共享同一套配色，一并切换。
            _imageViewer.Theme = Theme;
            _imageViewer.ThemeDictionary = ThemeDictionary;
            _volumeViewer.Theme = Theme;
            _volumeViewer.ThemeDictionary = ThemeDictionary;
            _volume3DViewer.Theme = Theme;
            _volume3DViewer.ThemeDictionary = ThemeDictionary;
        }

        private void UpdateStatus()
        {
            string data = _volume == null
                ? (_imageSource == null ? UiText.Get("StatusNoDataLoaded") : UiText.Get("StatusSingleImage"))
                : UiText.Format("StatusVolumeSummary", _volume.Width, _volume.Height, _volume.Depth, _volume.SpacingX, _volume.SpacingY, _volume.SpacingZ);
            if (_isMprRebuilding)
            {
                statusText.Text = UiText.Get("AdaptiveMprRebuilding");
            }
            else if (_volume != null && _readyMprMode == _displayMode)
            {
                UpdateMprStatus(_displayMode, GetMprSliceIndex(_displayMode), _volume);
            }
            else if (_volume != null && _displayMode == AdaptiveDisplayMode.AxialSlice)
            {
                UpdateAxialStatus(_volume);
            }
            else
            {
                statusText.Text = data;
            }
            stateBarText.Text = UiText.Format("StatusModeFormat", LocalizeMode(ResolveMode()), data);
            UpdateMprSliceControl(ResolveMode());
        }

        private static string LocalizeMode(AdaptiveDisplayMode mode)
        {
            return mode switch
            {
                AdaptiveDisplayMode.TwoDimensional => UiText.Get("Mode2D"),
                AdaptiveDisplayMode.ThreeDimensional => UiText.Get("Mode3D"),
                AdaptiveDisplayMode.AxialSlice => UiText.Get("ModeAxialSlice"),
                AdaptiveDisplayMode.Coronal => UiText.Get("ModeCoronal"),
                AdaptiveDisplayMode.Sagittal => UiText.Get("ModeSagittal"),
                _ => UiText.Get("ModeAuto")
            };
        }

        private void UpdateButtonStates()
        {
            bool hasVolume = _volume != null;
            bool operationActive = _operationCancellation != null;
            volumeControls.Visibility = hasVolume ? Visibility.Visible : Visibility.Collapsed;
            stateBarText.Visibility = hasVolume ? Visibility.Visible : Visibility.Collapsed;
            operationStatusPanel.Visibility = hasVolume || operationActive ? Visibility.Visible : Visibility.Collapsed;
            threeDimensionalButton.IsEnabled = hasVolume && !operationActive;
            axialButton.IsEnabled = hasVolume && !operationActive;
            coronalButton.IsEnabled = hasVolume && !operationActive;
            sagittalButton.IsEnabled = hasVolume && !operationActive;
            qualityButton.IsEnabled = hasVolume && !operationActive;
            segmentButton.IsEnabled = hasVolume && !operationActive;
            cancelButton.IsEnabled = operationActive || _isMprRebuilding;
            cancelButton.Visibility = cancelButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
            bool isMpr = hasVolume && (_displayMode == AdaptiveDisplayMode.Coronal || _displayMode == AdaptiveDisplayMode.Sagittal);
            mprSliceSlider.IsEnabled = isMpr && !operationActive;
            mprPreviousButton.IsEnabled = isMpr && !operationActive && mprSliceSlider.Value > mprSliceSlider.Minimum;
            mprNextButton.IsEnabled = isMpr && !operationActive && mprSliceSlider.Value < mprSliceSlider.Maximum;
            acceptSegmentationButton.IsEnabled = _pendingSegmentation != null;
            rejectSegmentationButton.IsEnabled = _pendingSegmentation != null;
            UpdateModeButtonVisuals();
            UpdateGestureHint();
            UpdateDataQualityPanelVisibility();
        }

        private void UpdateGestureHint()
        {
            if (_volume == null || ResolveMode() == AdaptiveDisplayMode.TwoDimensional)
            {
                gestureHintText.Text = UiText.Get("AdaptiveGestureImage");
                return;
            }

            gestureHintText.Text = ResolveMode() == AdaptiveDisplayMode.ThreeDimensional
                ? UiText.Get("AdaptiveGesture3D")
                : UiText.Get("AdaptiveGestureVolume");
        }

        private void UpdateModeButtonVisuals()
        {
            SetModeButtonVisual(autoButton, _displayMode == AdaptiveDisplayMode.Auto);
            SetModeButtonVisual(twoDimensionalButton, _displayMode == AdaptiveDisplayMode.TwoDimensional);
            SetModeButtonVisual(threeDimensionalButton, _displayMode == AdaptiveDisplayMode.ThreeDimensional);
            SetModeButtonVisual(axialButton, _displayMode == AdaptiveDisplayMode.AxialSlice);
            SetModeButtonVisual(coronalButton, _displayMode == AdaptiveDisplayMode.Coronal);
            SetModeButtonVisual(sagittalButton, _displayMode == AdaptiveDisplayMode.Sagittal);
        }

        private static void SetModeButtonVisual(Button button, bool isSelected)
        {
            // 直接指向统一主题键：主题画刷全部来自 UserControl.Resources 里的主题字典，
            // 这样 Theme/ThemeDictionary 切换后按钮视觉会跟着变（此前用了控件私有的 AdaptiveMode* 兼容键，
            // 而那些键挂在子级 Grid.Resources 上，语义与作用域都容易出偏差）。
            button.Background = (Brush)button.FindResource(isSelected ? "ViewerListItemSelectedBrush" : "ViewerSurfaceAltBrush");
            button.BorderBrush = (Brush)button.FindResource(isSelected ? "ViewerAccentBrush" : "ViewerChipBorderBrush");
            button.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private AdaptiveDisplayMode ResolveMode()
        {
            if (_volume == null)
            {
                return AdaptiveDisplayMode.TwoDimensional;
            }

            return _displayMode == AdaptiveDisplayMode.Auto ? AdaptiveDisplayMode.ThreeDimensional : _displayMode;
        }
    }
}
