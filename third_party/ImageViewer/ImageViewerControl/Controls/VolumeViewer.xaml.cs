using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class VolumeViewer : UserControl, IDisposable, IAsyncDisposable
    {
        /// <summary>控件主题（默认深色）。</summary>
        public static readonly DependencyProperty ThemeProperty =
            DependencyProperty.Register(
                nameof(Theme),
                typeof(ViewerThemes.Theme),
                typeof(VolumeViewer),
                new PropertyMetadata(ViewerThemes.Theme.Dark, OnThemeChanged));

        /// <summary>自定义主题字典；非 null 时优先于 <see cref="Theme"/>。</summary>
        public static readonly DependencyProperty ThemeDictionaryProperty =
            DependencyProperty.Register(
                nameof(ThemeDictionary),
                typeof(ResourceDictionary),
                typeof(VolumeViewer),
                new PropertyMetadata(null, OnThemeChanged));

        private VolumeData? _volume;
        private bool _isDisposed;
        private readonly ImageViewer sliceViewer;

        public VolumeViewer() : this(static () => new ImageViewer()) { }

        internal VolumeViewer(Func<ImageViewer> createImageViewer)
        {
            InitializeComponent();
            sliceViewer = createImageViewer();
            sliceViewer.rootGrid.PreviewMouseWheel += OnPreviewMouseWheel;
            sliceViewerHost.Content = sliceViewer;
            ApplyTheme();
            UpdateSliceState();
        }

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
            if (d is VolumeViewer viewer)
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

            ViewerThemeSwitcher.Apply(this, ThemeDictionary ?? ViewerThemes.CreateDictionary(Theme));
            sliceViewer.Theme = Theme;
            sliceViewer.ThemeDictionary = ThemeDictionary;
        }

        public VolumeData? Volume
        {
            get => _volume;
            set
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                _volume = value;
                sliceSlider.Maximum = Math.Max(0, (value?.Depth ?? 1) - 1);
                sliceSlider.Value = 0;
                UpdateSliceState();
            }
        }

        public int CurrentSliceIndex => _volume == null ? -1 : (int)sliceSlider.Value;

        public event EventHandler? CurrentSliceChanged;

        public ImageViewer SliceViewer => sliceViewer;

        public void SelectSlice(int sliceIndex)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_volume == null)
            {
                throw new InvalidOperationException("A volume must be assigned before selecting a slice.");
            }

            if ((uint)sliceIndex >= (uint)_volume.Depth)
            {
                throw new ArgumentOutOfRangeException(nameof(sliceIndex));
            }

            sliceSlider.Value = sliceIndex;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            sliceViewer.rootGrid.PreviewMouseWheel -= OnPreviewMouseWheel;
            sliceViewer.Dispose();
            _volume = null;
            GC.SuppressFinalize(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            GC.SuppressFinalize(this);
            return new ValueTask(sliceViewer.AutoSaveCloseCompletion);
        }

        private void OnSliceValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_isDisposed)
            {
                UpdateSliceState();
                CurrentSliceChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_volume == null || e.Delta == 0)
            {
                return;
            }

            // 所有 2D 视图统一使用 Ctrl+滚轮缩放；普通滚轮在轴位体数据中切片，
            // Shift 可一次跳过 5 层，便于快速浏览厚体数据。
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                return;
            }

            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
            int direction = e.Delta > 0 ? 1 : -1;
            int target = Math.Clamp(CurrentSliceIndex + direction * step, 0, _volume.Depth - 1);
            if (target != CurrentSliceIndex)
            {
                SelectSlice(target);
            }

            e.Handled = true;
        }

        private void UpdateSliceState()
        {
            if (_volume == null)
            {
                sliceViewer.SetVolumeSliceImage(null);
                sliceStatusText.Text = UiText.Get("StatusNoVolume");
                return;
            }

            int sliceIndex = CurrentSliceIndex;
            sliceViewer.SetVolumeSliceImage(VolumeSliceService.GetSlice(_volume, VolumeSliceOrientation.Axial, sliceIndex));
            string sliceStatus = UiText.Format("StatusAxialSlice", sliceIndex + 1, _volume.Depth);
            sliceStatusText.Text = UiText.Format("StatusVolumePlaneSpacing", sliceStatus, "X", _volume.SpacingX, "Y", _volume.SpacingY);
        }
    }
}
