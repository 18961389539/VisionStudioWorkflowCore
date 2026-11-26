using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Models;
using ImageViewer.Rendering;
using ImageViewer.ViewModels;

namespace ImageViewer.Plugins
{
    public sealed class RoiPlugin<T> : IRoiPlugin where T : RoiBase
    {
        private readonly Func<ImageViewerViewModel, ICollection<T>> _getCollection;
        private readonly Func<RoiPersistenceData, T> _createRoi;
        private readonly Action<T, RoiPersistenceData> _populatePersistenceData;
        private readonly Func<T, BitmapSource?, double, string?, IEnumerable<string>> _buildInfoLines;
        private readonly Func<T, FrameworkElement?> _createPropertyEditor;

        public RoiPlugin(
            string typeKey,
            int hitTestOrder,
            Func<ImageViewerViewModel, ICollection<T>> getCollection,
            IEnumerable<RoiToolDescriptor>? drawingTools,
            IRoiBehavior behavior,
            IRoiRenderer renderer,
            Func<RoiPersistenceData, T> createRoi,
            Action<T, RoiPersistenceData> populatePersistenceData,
            Func<T, BitmapSource?, double, string?, IEnumerable<string>>? buildInfoLines = null,
            Func<T, FrameworkElement?>? createPropertyEditor = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(typeKey);
            ArgumentNullException.ThrowIfNull(getCollection);
            ArgumentNullException.ThrowIfNull(behavior);
            ArgumentNullException.ThrowIfNull(renderer);
            ArgumentNullException.ThrowIfNull(createRoi);
            ArgumentNullException.ThrowIfNull(populatePersistenceData);

            TypeKey = typeKey;
            HitTestOrder = hitTestOrder;
            _getCollection = getCollection;
            DrawingTools = (drawingTools ?? Enumerable.Empty<RoiToolDescriptor>()).ToArray();
            Behavior = behavior;
            Renderer = renderer;
            _createRoi = createRoi;
            _populatePersistenceData = populatePersistenceData;
            _buildInfoLines = buildInfoLines ?? ((_, _, _, _) => Array.Empty<string>());
            _createPropertyEditor = createPropertyEditor ?? (_ => null);
        }

        public string TypeKey { get; }

        public Type RoiType => typeof(T);

        public int HitTestOrder { get; }

        public IReadOnlyList<RoiToolDescriptor> DrawingTools { get; }

        public IRoiBehavior Behavior { get; }

        public IRoiRenderer Renderer { get; }

        public IEnumerable<RoiBase> GetRois(ImageViewerViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            return _getCollection(viewModel).Cast<RoiBase>();
        }

        public void ClearCollection(ImageViewerViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            _getCollection(viewModel).Clear();
        }

        public bool AddToCollection(ImageViewerViewModel viewModel, RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            ArgumentNullException.ThrowIfNull(roi);

            if (roi is not T typedRoi)
            {
                return false;
            }

            _getCollection(viewModel).Add(typedRoi);
            return true;
        }

        public bool RemoveFromCollection(ImageViewerViewModel viewModel, RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            ArgumentNullException.ThrowIfNull(roi);

            return roi is T typedRoi && _getCollection(viewModel).Remove(typedRoi);
        }

        public RoiBase CreateRoi(RoiPersistenceData data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return _createRoi(data);
        }

        public void PopulatePersistenceData(RoiBase roi, RoiPersistenceData data)
        {
            ArgumentNullException.ThrowIfNull(roi);
            ArgumentNullException.ThrowIfNull(data);

            if (roi is not T typedRoi)
            {
                throw new ArgumentException($"Expected ROI of type {typeof(T).Name}.", nameof(roi));
            }

            _populatePersistenceData(typedRoi, data);
        }

        public IReadOnlyList<string> BuildInfoLines(RoiBase roi, BitmapSource? bitmap, double pixelSize, string? physicalUnit)
        {
            ArgumentNullException.ThrowIfNull(roi);

            if (roi is not T typedRoi)
            {
                throw new ArgumentException($"Expected ROI of type {typeof(T).Name}.", nameof(roi));
            }

            return _buildInfoLines(typedRoi, bitmap, pixelSize, physicalUnit).ToArray();
        }

        public FrameworkElement? CreatePropertyEditor(RoiBase roi)
        {
            ArgumentNullException.ThrowIfNull(roi);

            if (roi is not T typedRoi)
            {
                throw new ArgumentException($"Expected ROI of type {typeof(T).Name}.", nameof(roi));
            }

            return _createPropertyEditor(typedRoi);
        }
    }
}
