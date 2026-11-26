using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls
{
    internal sealed class ViewModelController : IDisposable
    {
        private readonly ImageViewerViewModel _viewModel;
        private readonly Action _updateContextMenuState;
        private readonly Action _handleSelectedRoiChanged;
        private readonly Action _drawRois;
        private readonly Action _markDocumentDirty;
        private bool _isAttached;

        public ViewModelController(
            ImageViewerViewModel viewModel,
            Action updateContextMenuState,
            Action handleSelectedRoiChanged,
            Action drawRois,
            Action markDocumentDirty)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            _updateContextMenuState = updateContextMenuState ?? throw new ArgumentNullException(nameof(updateContextMenuState));
            _handleSelectedRoiChanged = handleSelectedRoiChanged ?? throw new ArgumentNullException(nameof(handleSelectedRoiChanged));
            _drawRois = drawRois ?? throw new ArgumentNullException(nameof(drawRois));
            _markDocumentDirty = markDocumentDirty ?? throw new ArgumentNullException(nameof(markDocumentDirty));
        }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.UndoRedo.PropertyChanged += OnUndoRedoPropertyChanged;
            _viewModel.AllRois.CollectionChanged += OnAllRoisCollectionChanged;

            foreach (var roi in _viewModel.AllRois)
            {
                roi.PropertyChanged += OnRoiPropertyChanged;
            }

            _isAttached = true;
        }

        public void Dispose()
        {
            if (!_isAttached)
            {
                return;
            }

            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.UndoRedo.PropertyChanged -= OnUndoRedoPropertyChanged;
            _viewModel.AllRois.CollectionChanged -= OnAllRoisCollectionChanged;

            foreach (var roi in _viewModel.AllRois)
            {
                roi.PropertyChanged -= OnRoiPropertyChanged;
            }

            _isAttached = false;
        }

        private void OnUndoRedoPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UndoRedoManager.CanUndo) || e.PropertyName == nameof(UndoRedoManager.CanRedo))
            {
                _updateContextMenuState();
            }
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ImageViewerViewModel.SelectedRoi))
            {
                return;
            }

            _handleSelectedRoiChanged();
            _updateContextMenuState();
        }

        private void OnAllRoisCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (var item in e.OldItems.OfType<RoiBase>())
                {
                    item.PropertyChanged -= OnRoiPropertyChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (var item in e.NewItems.OfType<RoiBase>())
                {
                    item.PropertyChanged += OnRoiPropertyChanged;
                }
            }

            if (e.Action != NotifyCollectionChangedAction.Move)
            {
                _markDocumentDirty();
            }
        }

        private void OnRoiPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            _drawRois();
            if (e.PropertyName != nameof(RoiBase.IsSelected))
            {
                _markDocumentDirty();
            }
        }
    }
}
