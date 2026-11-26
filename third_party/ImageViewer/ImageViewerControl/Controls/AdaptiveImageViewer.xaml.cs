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
    public enum AdaptiveDisplayMode
    {
        Auto,
        TwoDimensional,
        ThreeDimensional,
        AxialSlice,
        Coronal,
        Sagittal
    }

    public partial class AdaptiveImageViewer : UserControl, IDisposable, IAsyncDisposable
    {
        private readonly ImageViewer _imageViewer;
        private readonly VolumeViewer _volumeViewer;
        private readonly Volume3DViewer _volume3DViewer;
        private ImageSource? _imageSource;
        private VolumeData? _volume;
        private AdaptiveDisplayMode _displayMode = AdaptiveDisplayMode.Auto;
        private bool _isDisposed;
        private CancellationTokenSource? _operationCancellation;


        public AdaptiveImageViewer() : this(static () => new ImageViewer()) { }

        internal AdaptiveImageViewer(Func<ImageViewer> createImageViewer)
        {
            InitializeComponent();
            Loaded += OnLoaded;
            _imageViewer = createImageViewer();
            volumeImageLoadHost.DataContext = _imageViewer;
            _imageViewer.rootGrid.PreviewMouseWheel += OnPreviewMouseWheel;
            _volumeViewer = new VolumeViewer(createImageViewer);
            _imageViewer.DocumentDialogOwner = this;
            _volumeViewer.SliceViewer.DocumentDialogOwner = this;
            _volumeViewer.SliceViewer.SharedFileImageWorkflow = _imageViewer.FileImageWorkflow;
            _imageViewer.PrepareHostedImageReplacementAsync = PrepareOpenedImageAsync;
            _imageViewer.SuspendImageReplacementWrites = SuspendImageReplacementWrites;
            _imageViewer.ImageLoadStateChanged += _volumeViewer.SliceViewer.SetImageLoadState;
            _volume3DViewer = new Volume3DViewer();
            _volume3DViewer.SwitchToAxialSliceRequested += OnSwitchToAxialSliceRequested;
            _volumeViewer.CurrentSliceChanged += OnCurrentSliceChanged;
            ApplyTheme();
            UpdateDisplayedView();
            UpdateStatus();
            UpdateButtonStates();
        }

        public ImageSource? ImageSource
        {
            get => _imageSource;
            set
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                _imageSource = value;
                if (!IsMprModeActive)
                {
                    _imageViewer.ImageSource = value;
                }
                UpdateDisplayedView();
                UpdateStatus();
                UpdateButtonStates();
            }
        }


        public VolumeData? Volume
        {
            get => _volume;
            set
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                AbandonMprRequest();
                _readyMprMode = null;
                if (IsMprMode(_displayMode))
                {
                    _imageViewer.SetVolumeSliceImage(value is null ? _imageSource : null);
                }
                _operationCancellation?.Cancel();
                _volume = value;
                _volumeViewer.Volume = value;
                _volume3DViewer.Volume = value;
                _coronalSliceIndex = value == null ? 0 : value.Height / 2;
                _sagittalSliceIndex = value == null ? 0 : value.Width / 2;
                _pendingSegmentation = null;
                _qualityReport = null;
                anomalyList.Items.Clear();
                segmentationText.Text = string.Empty;
                retryButton.Visibility = Visibility.Collapsed;
                UpdateDataQualityPanelVisibility();
                _volume3DViewer.SetCurrentSlice(_volumeViewer.CurrentSliceIndex);
                UpdateDisplayedView();
                UpdateStatus();
                UpdateButtonStates();

                // 允许宿主先设置 DisplayMode 再赋 Volume：模式已是 MPR 时，新体数据必须重新加载目标平面。
                if (_volume != null && (_displayMode is AdaptiveDisplayMode.Coronal or AdaptiveDisplayMode.Sagittal))
                {
                    _ = SetDisplayModeAsync(_displayMode);
                }
            }
        }

        public AdaptiveDisplayMode DisplayMode
        {
            get => _displayMode;
            set
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);

                // MPR 模式不只是切换容器：必须同时重建目标方向的首个切片。
                // 属性 setter 不能 await，因此统一交给异步入口；它会先把模式切过去，再用重建状态遮罩阻止误操作。
                if (value is AdaptiveDisplayMode.Coronal or AdaptiveDisplayMode.Sagittal)
                {
                    _ = SetDisplayModeAsync(value);
                    return;
                }

                ApplyDisplayMode(value);
            }
        }

        /// <summary>
        /// 切换显示模式；MPR 模式会等待目标切片重建完成后再返回。
        /// Chinese: 属性 setter、按钮和快捷键都走这里，避免“模式已切换但图像还是旧平面”的分叉路径。
        /// English: Single transition path for the property, buttons and shortcuts; MPR waits for the requested plane.
        /// </summary>
        public Task<bool> SetDisplayModeAsync(AdaptiveDisplayMode mode, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (mode is AdaptiveDisplayMode.Coronal or AdaptiveDisplayMode.Sagittal)
            {
                return SetMprDisplayModeAsync(mode, cancellationToken);
            }

            ApplyDisplayMode(mode);
            return Task.FromResult(true);
        }

        public ImageViewer ImageViewer => _imageViewer;

        /// <summary>Raised after a new 2D image replaces the current document.</summary>
        public event EventHandler? ImageDocumentOpened;

        /// <summary>Opens the shared file dialog for a 2D image.</summary>
        public Task ShowOpenImageDialogAsync() => _imageViewer.ShowOpenImageDialogAsync();

        public bool IsDirty => _imageViewer.IsDirty || _volumeViewer.SliceViewer.IsDirty;

        public Task SaveSessionAsync() => _imageViewer.SaveSessionAsync();

        /// <summary>Saves every dirty view as its own session. Returns false if any edits remain unsaved.</summary>
        public async Task<bool> SaveUnsavedSessionsAsync()
        {
            ImageViewer axial = _volumeViewer.SliceViewer;
            Window? owner = Window.GetWindow(this);
            if (_imageViewer.IsDirty)
            {
                await _imageViewer.SaveSessionForViewAsync(UiText.Get("SessionPrimaryView"), axial, owner);
                if (_imageViewer.IsDirty)
                {
                    return false;
                }
            }
            if (axial.IsDirty)
            {
                await axial.SaveSessionForViewAsync(UiText.Get("SessionAxialView"), _imageViewer, owner);
            }
            return !IsDirty;
        }

        /// <summary>Opens a new image document after protecting unsaved edits.</summary>
        public async Task<bool> ReplaceImageDocumentAsync(ImageSource image, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(image);
            bool opened = await ReplaceDocumentContentAsync(image, null, cancellationToken);
            if (opened)
            {
                ImageDocumentOpened?.Invoke(this, EventArgs.Empty);
            }
            return opened;
        }

        /// <summary>Opens a new volume document after protecting unsaved edits.</summary>
        public Task<bool> ReplaceVolumeDocumentAsync(VolumeData volume, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(volume);
            return ReplaceDocumentContentAsync(volume.GetAxialSlice(0), volume, cancellationToken);
        }

        /// <summary>Opens a 2D project after protecting edits in the current viewers.</summary>
        public async Task<bool> OpenProjectFromFileAsync(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            void ShowProject()
            {
                ImageSource = _imageViewer.ImageSource;
                DisplayMode = AdaptiveDisplayMode.Auto;
                Volume = null;
            }

            using IDisposable primaryPause = _imageViewer.SuspendAutoSaveWrites();
            using IDisposable axialPause = _volumeViewer.SliceViewer.SuspendAutoSaveWrites();
            return await _imageViewer.OpenProjectFromFileAsync(filePath,
                token => _volume != null
                    ? _volumeViewer.SliceViewer.PrepareDocumentContentAsync(ShowProject, token)
                    : Task.FromResult<Action?>(ShowProject));
        }

        private async Task<bool> ReplaceDocumentContentAsync(ImageSource image, VolumeData? volume, CancellationToken cancellationToken)
        {
            // Both viewers can prompt before the shared volume changes. While preparing the
            // replacement, drain existing writes and keep their next timers from racing it.
            using var writes = SuspendImageReplacementWrites();
            return await _imageViewer.ReplaceDocumentContentAsync(
                token => PrepareRelatedImageContentAsync(image, volume, token), cancellationToken);
        }

        private async Task<Action?> PrepareOpenedImageAsync(ImageSource image, CancellationToken token)
        {
            Action? apply = await _imageViewer.PrepareDocumentContentAsync(
                ct => PrepareRelatedImageContentAsync(image, null, ct), token);
            if (apply == null)
            {
                return null;
            }
            return () =>
            {
                apply();
                ImageDocumentOpened?.Invoke(this, EventArgs.Empty);
            };
        }

        private Task<Action?> PrepareRelatedImageContentAsync(ImageSource image, VolumeData? volume, CancellationToken token)
        {
            void ApplyContent()
            {
                DisplayMode = AdaptiveDisplayMode.Auto;
                Volume = volume;
                ImageSource = image;
            }

            return _volume != null || volume != null
                ? _volumeViewer.SliceViewer.PrepareDocumentContentAsync(ApplyContent, token)
                : Task.FromResult<Action?>(ApplyContent);
        }

        private IDisposable SuspendImageReplacementWrites() => new ImageReplacementWriteSuspension(
            _imageViewer.SuspendAutoSaveWrites(), _volumeViewer.SliceViewer.SuspendAutoSaveWrites());

        private sealed class ImageReplacementWriteSuspension(IDisposable primary, IDisposable axial) : IDisposable
        {
            public void Dispose()
            {
                axial.Dispose();
                primary.Dispose();
            }
        }

        /// <summary>Marks host-provided initial content as the saved baseline.</summary>
        public void MarkDocumentClean()
        {
            _imageViewer.MarkDocumentClean();
            _volumeViewer.SliceViewer.MarkDocumentClean();
        }

        public VolumeViewer VolumeViewer => _volumeViewer;

        public Volume3DViewer Volume3DViewer => _volume3DViewer;

        public UserControl ActiveView => ResolveView();

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            AbandonMprRequest();
            _imageViewer.ImageLoadStateChanged -= _volumeViewer.SliceViewer.SetImageLoadState;
            _volumeViewer.SliceViewer.SharedFileImageWorkflow = null;
            _imageViewer.DocumentDialogOwner = null;
            _volumeViewer.SliceViewer.DocumentDialogOwner = null;
            _imageViewer.PrepareHostedImageReplacementAsync = null;
            _imageViewer.SuspendImageReplacementWrites = null;
            _imageViewer.rootGrid.PreviewMouseWheel -= OnPreviewMouseWheel;
            _imageViewer.Dispose();
            _volumeViewer.Dispose();
            _volume3DViewer.SwitchToAxialSliceRequested -= OnSwitchToAxialSliceRequested;
            _volumeViewer.CurrentSliceChanged -= OnCurrentSliceChanged;
            Loaded -= OnLoaded;
            _volume3DViewer.Dispose();
            _operationCancellation?.Cancel();
            contentHost.Content = null;
            _imageSource = null;
            _volume = null;
            GC.SuppressFinalize(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            GC.SuppressFinalize(this);
            return new ValueTask(Task.WhenAll(_imageViewer.AutoSaveCloseCompletion, _volumeViewer.SliceViewer.AutoSaveCloseCompletion));
        }

        private void ApplyDisplayMode(AdaptiveDisplayMode mode)
        {
            if (_isDisposed)
            {
                return;
            }

            bool wasMpr = IsMprMode(_displayMode);
            _displayMode = mode;
            if (!IsMprMode(mode))
            {
                AbandonMprRequest();
                _readyMprMode = null;
                _lastNonMprMode = mode;
                if (wasMpr)
                {
                    _imageViewer.SetVolumeSliceImage(_imageSource);
                }
            }
            else if (!wasMpr)
            {
                _imageSource = _imageViewer.ImageSource;
            }

            UpdateDisplayedView();
            UpdateStatus();
            UpdateButtonStates();
        }

        private void UpdateDisplayedView()
        {
            if (_isDisposed)
            {
                return;
            }

            UserControl view = ResolveView();
            if (!ReferenceEquals(contentHost.Content, view))
            {
                contentHost.Content = view;
            }
            volumeImageLoadHost.Visibility = ReferenceEquals(view, _volume3DViewer) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void OnRetryVolumeImageLoadClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await _imageViewer.RetryLastImageLoadAsync();
            }
            catch (Exception)
            {
                statusText.Text = UiText.Get("StatusViewerOperationFailed");
            }
        }

        private void OnCancelVolumeImageLoadClick(object sender, RoutedEventArgs e) => _imageViewer.CancelImageLoad();

        private UserControl ResolveView()
        {
            return ResolveMode() switch
            {
                AdaptiveDisplayMode.TwoDimensional => _imageViewer,
                AdaptiveDisplayMode.ThreeDimensional => _volume3DViewer,
                AdaptiveDisplayMode.AxialSlice => _volumeViewer,
                AdaptiveDisplayMode.Coronal => _imageViewer,
                AdaptiveDisplayMode.Sagittal => _imageViewer,
                _ => _imageViewer
            };
        }

        private void OnSwitchToAxialSliceRequested(object? sender, EventArgs e)
        {
            DisplayMode = AdaptiveDisplayMode.AxialSlice;
        }

        private void OnCurrentSliceChanged(object? sender, EventArgs e)
        {
            _volume3DViewer.SetCurrentSlice(_volumeViewer.CurrentSliceIndex);
            if (_volume != null && _displayMode == AdaptiveDisplayMode.AxialSlice)
            {
                UpdateAxialStatus(_volume);
            }
        }

        private void UpdateAxialStatus(VolumeData volume)
        {
            string sliceStatus = UiText.Format("StatusAxialSlice", _volumeViewer.CurrentSliceIndex + 1, volume.Depth);
            statusText.Text = UiText.Format("StatusVolumePlaneSpacing", sliceStatus, "X", volume.SpacingX, "Y", volume.SpacingY);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Focus();
        }

        private void OnAutoClick(object sender, RoutedEventArgs e) => DisplayMode = AdaptiveDisplayMode.Auto;
        private void OnTwoDimensionalClick(object sender, RoutedEventArgs e) => DisplayMode = AdaptiveDisplayMode.TwoDimensional;
        private void OnThreeDimensionalClick(object sender, RoutedEventArgs e) => DisplayMode = AdaptiveDisplayMode.ThreeDimensional;
        private void OnAxialClick(object sender, RoutedEventArgs e) => DisplayMode = AdaptiveDisplayMode.AxialSlice;
        private void OnCoronalClick(object sender, RoutedEventArgs e) => _ = SetDisplayModeAsync(AdaptiveDisplayMode.Coronal);
        private void OnSagittalClick(object sender, RoutedEventArgs e) => _ = SetDisplayModeAsync(AdaptiveDisplayMode.Sagittal);

        private async void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                await HandleKeyDownAsync(e);
            }
            catch (Exception)
            {
                statusText.Text = UiText.Get("StatusViewerOperationFailed");
            }
        }

        private async Task HandleKeyDownAsync(KeyEventArgs e)
        {
            // 让嵌套 ImageViewer 先处理 ROI 的方向键和 Esc；文本框、下拉框、列表和滑块
            // 也必须保留自己的键盘行为，避免外层查看器抢走输入。
            if (e.Handled || (e.Key != Key.Escape && IsKeyboardInputControlFocused()))
            {
                return;
            }

            switch (e.Key)
            {
                case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                    e.Handled = true;
                    await ShowOpenImageDialogAsync();
                    break;
                case Key.D1 when _volume != null:
                    DisplayMode = AdaptiveDisplayMode.AxialSlice;
                    break;
                case Key.D2 when _volume != null:
                    e.Handled = true;
                    await SetDisplayModeAsync(AdaptiveDisplayMode.Coronal);
                    break;
                case Key.D3 when _volume != null:
                    e.Handled = true;
                    await SetDisplayModeAsync(AdaptiveDisplayMode.Sagittal);
                    break;
                case Key.D4 when _volume != null:
                    DisplayMode = AdaptiveDisplayMode.ThreeDimensional;
                    break;
                case Key.Home: ResetActiveView(); break;
                case Key.F when _volume != null && ResolveMode() == AdaptiveDisplayMode.ThreeDimensional:
                    _volume3DViewer.FitVolume();
                    statusText.Text = UiText.Get("StatusVolumeFitted");
                    break;
                case Key.Up when IsMprModeActive:
                    e.Handled = true;
                    await StepMprSliceAsync(1);
                    break;
                case Key.Down when IsMprModeActive:
                    e.Handled = true;
                    await StepMprSliceAsync(-1);
                    break;
                case Key.Escape:
                    bool cancelled = false;
                    if (_operationCancellation is not null)
                    {
                        _operationCancellation.Cancel();
                        cancelled = true;
                    }

                    cancelled |= CancelMprNavigation();

                    if (!cancelled)
                    {
                        return;
                    }

                    break;
                default: return;
            }

            e.Handled = true;
        }

        private static bool IsKeyboardInputControlFocused()
        {
            IInputElement? focusedElement = Keyboard.FocusedElement;
            return focusedElement is TextBoxBase
                or PasswordBox
                or ComboBox
                or Selector
                or Slider;
        }

        private void ResetActiveView()
        {
            if (ResolveMode() == AdaptiveDisplayMode.ThreeDimensional)
            {
                _volume3DViewer.ResetCamera();
                statusText.Text = UiText.Get("StatusCameraReset");
            }
            else if (_volume == null)
            {
                statusText.Text = UiText.Get("Status2DViewReset");
            }
            else
            {
                _volumeViewer.SelectSlice(0);
                statusText.Text = UiText.Get("Status2DViewResetFirstSlice");
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            _operationCancellation?.Cancel();
            if (!CancelMprNavigation() || _operationCancellation != null)
            {
                statusText.Text = UiText.Get("StatusCancelRequested");
            }
            operationProgress.Visibility = Visibility.Collapsed;
            UpdateButtonStates();
        }

        private CancellationTokenSource BeginOperation(string message)
        {
            _operationCancellation?.Cancel();
            var operation = new CancellationTokenSource();
            _operationCancellation = operation;
            statusText.Text = message;
            operationProgress.Visibility = Visibility.Visible;
            UpdateButtonStates();
            return operation;
        }

        private bool IsCurrentOperation(CancellationTokenSource operation)
        {
            return ReferenceEquals(_operationCancellation, operation) && !operation.IsCancellationRequested && !_isDisposed;
        }

        private void EndOperation(CancellationTokenSource operation)
        {
            if (!ReferenceEquals(_operationCancellation, operation))
            {
                operation.Dispose();
                return;
            }

            operation.Dispose();
            _operationCancellation = null;
            if (!_isDisposed)
            {
                operationProgress.Visibility = Visibility.Collapsed;
                UpdateButtonStates();
            }
        }

    }
}
