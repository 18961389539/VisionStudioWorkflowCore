using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ImageViewer.Abstractions;

namespace ImageViewer.Controls
{
    internal sealed class ExternalImageSourceBindingController : IDisposable
    {
        private readonly FrameworkElement _owner;
        private readonly Func<ImageSource?> _currentImageSourceProvider;
        private readonly Action<ImageSource?> _setCurrentImageSource;
        private INotifyPropertyChanged? _externalDataContextNotifier;
        private bool _isMirroringDataContextImageSource;
        private bool _isAttached;

        public ExternalImageSourceBindingController(
            FrameworkElement owner,
            Func<ImageSource?> currentImageSourceProvider,
            Action<ImageSource?> setCurrentImageSource)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _currentImageSourceProvider = currentImageSourceProvider ?? throw new ArgumentNullException(nameof(currentImageSourceProvider));
            _setCurrentImageSource = setCurrentImageSource ?? throw new ArgumentNullException(nameof(setCurrentImageSource));
        }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _owner.DataContextChanged += OnExternalDataContextChanged;
            SyncImageSourceFromDataContext();
            _isAttached = true;
        }

        public void Refresh()
        {
            SyncImageSourceFromDataContext();
        }

        public void Dispose()
        {
            if (!_isAttached)
            {
                return;
            }

            if (_externalDataContextNotifier != null)
            {
                _externalDataContextNotifier.PropertyChanged -= OnExternalDataContextPropertyChanged;
                _externalDataContextNotifier = null;
            }

            _owner.DataContextChanged -= OnExternalDataContextChanged;
            _isAttached = false;
        }

        private void OnExternalDataContextChanged(object? sender, DependencyPropertyChangedEventArgs e)
        {
            if (_externalDataContextNotifier != null)
            {
                _externalDataContextNotifier.PropertyChanged -= OnExternalDataContextPropertyChanged;
            }

            _externalDataContextNotifier = e.NewValue as INotifyPropertyChanged;
            if (_externalDataContextNotifier != null)
            {
                _externalDataContextNotifier.PropertyChanged += OnExternalDataContextPropertyChanged;
            }

            SyncImageSourceFromDataContext();
        }

        private void OnExternalDataContextPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(IImageViewerImageSourceProvider.ViewerImage))
            {
                SyncImageSourceFromDataContext();
            }
        }

        private void SyncImageSourceFromDataContext()
        {
            if (TryGetImageSourceFromDataContext(_owner.DataContext, out var source))
            {
                _isMirroringDataContextImageSource = true;
                if (!ReferenceEquals(_currentImageSourceProvider(), source))
                {
                    _setCurrentImageSource(source);
                }
                return;
            }

            if (_isMirroringDataContextImageSource)
            {
                _isMirroringDataContextImageSource = false;
                if (_currentImageSourceProvider() != null)
                {
                    _setCurrentImageSource(null);
                }
            }
        }

        private static bool TryGetImageSourceFromDataContext(object? dataContext, out ImageSource? imageSource)
        {
            if (dataContext is null)
            {
                imageSource = null;
                return false;
            }

            if (dataContext is IImageViewerImageSourceProvider provider)
            {
                imageSource = provider.ViewerImage;
                return true;
            }

            if (dataContext is ImageSource source)
            {
                imageSource = source;
                return true;
            }

            imageSource = null;
            return false;
        }
    }
}
