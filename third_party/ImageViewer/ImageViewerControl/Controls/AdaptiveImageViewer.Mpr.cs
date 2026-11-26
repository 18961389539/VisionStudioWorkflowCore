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
using System.Windows.Threading;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;

namespace ImageViewer.Controls
{
    public partial class AdaptiveImageViewer : UserControl, IDisposable, IAsyncDisposable
    {
        private const int MprNavigationDebounceMilliseconds = 50;

        private CancellationTokenSource? _mprCancellation;
        private DispatcherTimer? _mprNavigationDebounce;
        private PendingMprSliceRequest? _pendingMprSliceRequest;
        private int? _mprTargetSliceIndex;
        private AdaptiveDisplayMode? _readyMprMode;
        private AdaptiveDisplayMode _lastNonMprMode;
        private int _coronalSliceIndex;
        private int _sagittalSliceIndex;
        private bool _isUpdatingMprSlider;
        private bool _isMprRebuilding;

        private Task<bool> SetMprDisplayModeAsync(AdaptiveDisplayMode mode, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            CancelMprNavigationDebounce();
            ApplyDisplayMode(mode);

            if (_volume == null)
            {
                statusText.Text = UiText.Get("StatusLoadVolumeFirstMpr");
                return Task.FromResult(false);
            }

            return UpdateMprSliceAsync(mode, GetMprSliceIndex(mode), cancellationToken);
        }

        private static bool IsMprMode(AdaptiveDisplayMode mode) =>
            mode is AdaptiveDisplayMode.Coronal or AdaptiveDisplayMode.Sagittal;

        private bool IsMprModeActive => _volume != null && IsMprMode(_displayMode);

        internal void StepMprSlice(int offset)
        {
            _ = StepMprSliceAsync(offset);
        }

        internal Task<bool> StepMprSliceAsync(int offset)
        {
            if (!IsMprModeActive || _volume == null || _isDisposed)
            {
                return Task.FromResult(false);
            }

            AdaptiveDisplayMode mode = _displayMode;
            int current = GetMprNavigationIndex(mode);
            CancelMprNavigationDebounce();
            int maximum = mode == AdaptiveDisplayMode.Coronal ? _volume.Height - 1 : _volume.Width - 1;
            return UpdateMprSliceAsync(mode, Math.Clamp(current + offset, 0, maximum));
        }

        private int GetMprSliceIndex(AdaptiveDisplayMode mode) =>
            mode == AdaptiveDisplayMode.Coronal ? _coronalSliceIndex : _sagittalSliceIndex;

        private int GetMprNavigationIndex(AdaptiveDisplayMode mode) =>
            _pendingMprSliceRequest is { } pending && pending.Mode == mode
                ? pending.SliceIndex
                : _mprTargetSliceIndex ?? GetMprSliceIndex(mode);

        private async Task<bool> UpdateMprSliceAsync(
            AdaptiveDisplayMode mode, int sliceIndex, CancellationToken callerToken = default)
        {
            VolumeData? volume = _volume;
            if (volume == null || _isDisposed)
            {
                return false;
            }

            _mprCancellation?.Cancel();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            _mprCancellation = cancellation;
            _mprTargetSliceIndex = sliceIndex;
            UpdateMprSliceControl(mode);
            SetMprRebuildState(true);

            BitmapSource? slice = null;
            try
            {
                VolumeSliceOrientation orientation = mode == AdaptiveDisplayMode.Coronal
                    ? VolumeSliceOrientation.Coronal : VolumeSliceOrientation.Sagittal;
                slice = await VolumeSliceService.GetSliceAsync(volume, orientation, sliceIndex, cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _imageViewer.RuntimeServices.Logger.LogError("Reconstruct MPR slice", ex);
            }

            // Do not depend on an ambient WPF synchronization context. Hosts can assign the mode
            // on the owning STA before its window or dispatcher loop starts.
            try
            {
                return await Dispatcher.InvokeAsync(
                    () => CompleteMprRequest(cancellation, volume, mode, sliceIndex, slice)).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Dispatcher.HasShutdownStarted)
            {
                return false;
            }
        }

        private bool CompleteMprRequest(CancellationTokenSource cancellation, VolumeData volume,
            AdaptiveDisplayMode mode, int sliceIndex, BitmapSource? slice)
        {
            if (!ReferenceEquals(_mprCancellation, cancellation))
            {
                return false;
            }

            _mprCancellation = null;
            _mprTargetSliceIndex = null;
            if (_isDisposed || !ReferenceEquals(_volume, volume) || _displayMode != mode)
            {
                return false;
            }

            try
            {
                if (slice != null && !cancellation.IsCancellationRequested)
                {
                    if (mode == AdaptiveDisplayMode.Coronal)
                    {
                        _coronalSliceIndex = sliceIndex;
                        _volume3DViewer.SetCoronalSlice(sliceIndex);
                    }
                    else
                    {
                        _sagittalSliceIndex = sliceIndex;
                        _volume3DViewer.SetSagittalSlice(sliceIndex);
                    }

                    _imageViewer.SetVolumeSliceImage(slice);
                    _readyMprMode = mode;
                    UpdateMprStatus(mode, sliceIndex, volume);
                    UpdateMprSliceControl(mode);
                    return true;
                }

                RestoreReadyMprView(cancellation.IsCancellationRequested
                    ? UiText.Get("AdaptiveMprCanceled")
                    : UiText.Format("StatusUnableCreateView", LocalizeMode(mode)));
                return false;
            }
            finally
            {
                SetMprRebuildState(false);
            }
        }

        private void UpdateMprStatus(AdaptiveDisplayMode mode, int sliceIndex, VolumeData volume)
        {
            string sliceStatus = mode == AdaptiveDisplayMode.Coronal
                ? UiText.Format("StatusCoronalSlice", sliceIndex + 1, volume.Height)
                : UiText.Format("StatusSagittalSlice", sliceIndex + 1, volume.Width);
            statusText.Text = mode == AdaptiveDisplayMode.Coronal
                ? UiText.Format("StatusVolumePlaneSpacing", sliceStatus, "X", volume.SpacingX, "Z", volume.SpacingZ)
                : UiText.Format("StatusVolumePlaneSpacing", sliceStatus, "Z", volume.SpacingZ, "Y", volume.SpacingY);
        }


        private void OnPreviousMprSliceClick(object sender, RoutedEventArgs e) => StepMprSlice(-1);

        private void OnNextMprSliceClick(object sender, RoutedEventArgs e) => StepMprSlice(1);

        private void OnMprSliceValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingMprSlider || _volume == null || (_displayMode != AdaptiveDisplayMode.Coronal && _displayMode != AdaptiveDisplayMode.Sagittal))
            {
                return;
            }

            int sliceIndex = (int)Math.Round(e.NewValue);
            ScheduleMprSliceRequest(_displayMode, sliceIndex);
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_volume != null && (_displayMode == AdaptiveDisplayMode.Coronal || _displayMode == AdaptiveDisplayMode.Sagittal))
            {
                // 与轴位体数据保持一致：普通滚轮切片，Shift 快速跳层，Ctrl+滚轮
                // 交给嵌套 ImageViewer 执行缩放。
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    return;
                }

                int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 5 : 1;
                e.Handled = true;
                ScheduleMprSliceOffset((e.Delta > 0 ? 1 : -1) * step);
            }
        }

        private void ScheduleMprSliceOffset(int offset)
        {
            if (_volume == null || (_displayMode != AdaptiveDisplayMode.Coronal && _displayMode != AdaptiveDisplayMode.Sagittal))
            {
                return;
            }

            AdaptiveDisplayMode mode = _displayMode;
            int current = GetMprNavigationIndex(mode);
            int maximum = mode == AdaptiveDisplayMode.Coronal ? _volume.Height - 1 : _volume.Width - 1;
            ScheduleMprSliceRequest(mode, Math.Clamp(current + offset, 0, maximum));
        }

        private void ScheduleMprSliceRequest(AdaptiveDisplayMode mode, int sliceIndex)
        {
            if (_volume == null || _isDisposed)
            {
                return;
            }

            _pendingMprSliceRequest = new PendingMprSliceRequest(mode, sliceIndex);
            _mprCancellation?.Cancel();
            _mprCancellation = null;
            _mprTargetSliceIndex = null;
            if (_mprNavigationDebounce == null)
            {
                _mprNavigationDebounce = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(MprNavigationDebounceMilliseconds)
                };
                _mprNavigationDebounce.Tick += OnMprNavigationTick;
            }
            _mprNavigationDebounce.Stop();
            UpdateMprSliceControl(mode);
            SetMprRebuildState(true);
            _mprNavigationDebounce.Start();
        }

        private void OnMprNavigationTick(object? sender, EventArgs e)
        {
            PendingMprSliceRequest? pending = _pendingMprSliceRequest;
            CancelMprNavigationDebounce();
            if (!_isDisposed && pending is { } request && request.Mode == _displayMode)
            {
                _ = UpdateMprSliceAsync(request.Mode, request.SliceIndex);
            }
        }

        private void CancelMprNavigationDebounce()
        {
            _mprNavigationDebounce?.Stop();
            _pendingMprSliceRequest = null;
        }

        private void AbandonMprRequest()
        {
            CancelMprNavigationDebounce();
            _mprCancellation?.Cancel();
            _mprCancellation = null;
            _mprTargetSliceIndex = null;
            SetMprRebuildState(false);
        }

        private bool CancelMprNavigation()
        {
            if (_mprCancellation is { } active)
            {
                active.Cancel();
                return true;
            }

            if (_pendingMprSliceRequest == null)
            {
                return false;
            }

            CancelMprNavigationDebounce();
            RestoreReadyMprView(UiText.Get("AdaptiveMprCanceled"));
            SetMprRebuildState(false);
            return true;
        }

        private void RestoreReadyMprView(string status)
        {
            ApplyDisplayMode(_readyMprMode ?? _lastNonMprMode);
            statusText.Text = status;
        }

        private void SetMprRebuildState(bool rebuilding)
        {
            if (_isDisposed && rebuilding)
            {
                return;
            }

            if (rebuilding)
            {
                statusText.Text = UiText.Get("AdaptiveMprRebuilding");
            }

            if (_isMprRebuilding == rebuilding)
            {
                return;
            }

            _isMprRebuilding = rebuilding;
            if (mprRebuildOverlay != null)
            {
                mprRebuildOverlay.Visibility = rebuilding ? Visibility.Visible : Visibility.Collapsed;
            }

            if (_imageViewer != null)
            {
                _imageViewer.IsEnabled = !rebuilding;
            }

            if (!_isDisposed)
            {
                UpdateButtonStates();
            }
        }

        private void UpdateMprSliceControl(AdaptiveDisplayMode mode)
        {
            bool isMpr = _volume != null && (mode == AdaptiveDisplayMode.Coronal || mode == AdaptiveDisplayMode.Sagittal);
            mprSliceBar.Visibility = isMpr ? Visibility.Visible : Visibility.Collapsed;
            if (!isMpr || _volume == null)
            {
                return;
            }

            int sliceIndex = GetMprNavigationIndex(mode);
            int maximum = mode == AdaptiveDisplayMode.Coronal ? _volume.Height - 1 : _volume.Width - 1;
            _isUpdatingMprSlider = true;
            try
            {
                mprSliceSlider.Maximum = Math.Max(0, maximum);
                mprSliceSlider.Value = Math.Clamp(sliceIndex, 0, Math.Max(0, maximum));
            }
            finally
            {
                _isUpdatingMprSlider = false;
            }

            mprSliceStatusText.Text = UiText.Format("AdaptiveMprSliceStatus", LocalizeMode(mode), sliceIndex + 1, maximum + 1);
            mprPreviousButton.IsEnabled = sliceIndex > 0;
            mprNextButton.IsEnabled = sliceIndex < maximum;
        }

        private readonly record struct PendingMprSliceRequest(AdaptiveDisplayMode Mode, int SliceIndex);
    }
}
