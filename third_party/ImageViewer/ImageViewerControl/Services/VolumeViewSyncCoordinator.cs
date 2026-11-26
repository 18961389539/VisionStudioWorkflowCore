using System;
using ImageViewer.Controls;

namespace ImageViewer.Services
{
    public sealed class VolumeViewSyncCoordinator : IDisposable
    {
        private readonly VolumeViewer _sliceViewer;
        private readonly AdaptiveImageViewer _adaptiveViewer;
        private bool _isDisposed;

        public VolumeViewSyncCoordinator(AdaptiveImageViewer adaptiveViewer)
        {
            _adaptiveViewer = adaptiveViewer ?? throw new ArgumentNullException(nameof(adaptiveViewer));
            _sliceViewer = adaptiveViewer.VolumeViewer;
            adaptiveViewer.Volume3DViewer.SwitchToAxialSliceRequested += OnSwitchToAxialSliceRequested;
        }

        public int CurrentSliceIndex => _sliceViewer.CurrentSliceIndex;

        public void SelectAxialSlice(int index)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _sliceViewer.SelectSlice(index);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _adaptiveViewer.Volume3DViewer.SwitchToAxialSliceRequested -= OnSwitchToAxialSliceRequested;
            GC.SuppressFinalize(this);
        }

        private void OnSwitchToAxialSliceRequested(object? sender, EventArgs e)
        {
            _adaptiveViewer.DisplayMode = AdaptiveDisplayMode.AxialSlice;
        }
    }
}
