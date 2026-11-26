using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Abstractions;
using ImageViewer.Models;
using ImageViewer.Rendering;
using ImageViewer.ViewModels;

namespace ImageViewer.Plugins
{
    /// <summary>Provides the stable identity and UI tool metadata for an ROI plugin.</summary>
    public interface IRoiPluginDefinition
    {
        string TypeKey { get; }
        Type RoiType { get; }
        int HitTestOrder { get; }
        IReadOnlyList<RoiToolDescriptor> DrawingTools { get; }
        IRoiBehavior Behavior { get; }
        IRoiRenderer Renderer { get; }
    }

    /// <summary>Provides access to the typed ROI collection owned by a viewer state.</summary>
    public interface IRoiCollectionCapability
    {
        IEnumerable<RoiBase> GetRois(ImageViewerViewModel viewModel);
        void ClearCollection(ImageViewerViewModel viewModel);
        bool AddToCollection(ImageViewerViewModel viewModel, RoiBase roi);
        bool RemoveFromCollection(ImageViewerViewModel viewModel, RoiBase roi);
    }

    /// <summary>Creates and serializes ROI instances for persistence.</summary>
    public interface IRoiPersistenceCapability
    {
        RoiBase CreateRoi(RoiPersistenceData data);
        void PopulatePersistenceData(RoiBase roi, RoiPersistenceData data);
    }

    /// <summary>Builds read-only information lines for an ROI.</summary>
    public interface IRoiInfoCapability
    {
        IReadOnlyList<string> BuildInfoLines(RoiBase roi, BitmapSource? bitmap, double pixelSize, string? physicalUnit);
    }

    /// <summary>Creates an optional WPF property editor for an ROI.</summary>
    public interface IRoiEditorCapability
    {
        FrameworkElement? CreatePropertyEditor(RoiBase roi);
    }
}
