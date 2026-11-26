using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Core.Measurements;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Utils;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls;

public partial class ImageViewer
{
    private readonly List<MenuItem> _contextualGeneratedMenuItems = [];
    private ContextualPathVertexTarget? _contextualPathVertexTarget;

    internal void CaptureContextualPathVertex(RoiBase? roi, int? pointIndex)
    {
        _contextualPathVertexTarget = null;
        if (roi is null || pointIndex is not int index)
        {
            return;
        }

        ObservableCollection<PointD>? points = roi switch
        {
            PolygonRoi polygon => polygon.Points,
            PolylineRoi polyline => polyline.Points,
            _ => null
        };
        if (points is not null && index >= 0 && index < points.Count)
        {
            _contextualPathVertexTarget = new ContextualPathVertexTarget(roi, points, index, points[index], points.ToArray());
        }
    }

    /// <summary>Refreshes the selected-object shortcuts immediately before the context menu is shown.</summary>
    internal void PrepareContextualRoiMenu()
    {
        foreach (MenuItem item in _contextualGeneratedMenuItems)
        {
            contextRoiShortcutsMenuItem.Items.Remove(item);
        }

        _contextualGeneratedMenuItems.Clear();
        RoiBase? selected = ViewerState.SelectedRoi;
        contextRoiShortcutsMenuItem.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        if (selected is null)
        {
            _contextualPathVertexTarget = null;
            return;
        }

        string selectedName = string.IsNullOrWhiteSpace(selected.Label) ? UiText.Get("ContextRoiUnnamed") : selected.Label;
        contextRoiShortcutsMenuItem.Header = UiText.Format("ContextRoiShortcutsFor", selectedName, selected.DisplayTypeName);

        if (DetectionParameterRecommendationService.IsSupported(selected))
            AddContextualMenuItem(CreateDetectionRecommendationMenuItem());

        var generated = new MenuItem
        {
            Header = UiText.Get("ContextRoiMeasurements")
        };
        AddContextualMenuItem(generated);

        switch (selected)
        {
            case PointCoordinateMeasureRoi or PointAnnotationRoi:
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Position, null, selected);
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Midpoint, null, selected);
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Projection, null, selected);
                break;
            case PolygonRoi or PolylineRoi:
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.GapProfile, null, selected);
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.ContourDeviation, null, selected);
                AddVertexDeletionShortcut(contextRoiShortcutsMenuItem, selected);
                break;
            case LineMeasureRoi or CaliperMeasureRoi or LineCaliperMeasureRoi:
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Straightness, null, selected);
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.ShortestDistance, null, selected);
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Centerline, null, selected);
                if (selected is LineMeasureRoi or CaliperMeasureRoi)
                {
                    AddAction(generated, UiText.Get("ContextRoiCalibratePixels"),
                        () => _roiMenuCommandController.Execute(ImageViewerRoiMenuCommand.CalibratePixels), enabled: !selected.IsLocked && calibratePixelsMenuItem.IsEnabled);
                }
                break;
            case CircleRoi or ThreePointCircleMeasureRoi or CircularCaliperMeasureRoi:
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Circularity, "ContextRoiCircularDeviation", selected);
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Pitch, "ContextRoiCenterDistance", selected, "ContextRoiCenterDistanceHint");
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.ShortestDistance, "ContextRoiBoundaryDistance", selected);
                break;
            case RotatedRect or BlobAnalysisRoi:
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Rectangle, null, selected);
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.Slot, null, selected);
                AddAdvancedShortcut(generated, AdvancedMeasurementKind.ShapeMetrics, null, selected);
                break;
        }
        if (generated.Items.Count == 0)
        {
            contextRoiShortcutsMenuItem.Items.Remove(generated);
            _contextualGeneratedMenuItems.Remove(generated);
        }
    }

    private void AddAdvancedShortcut(MenuItem parent, AdvancedMeasurementKind kind, string? headerKey, RoiBase source, string? hintKey = null)
    {
        MeasurementMenuAvailability availability = AdvancedMeasurementMenuCatalog.GetAvailability(
            kind, ViewerState.AllRois, GetAnalysisBitmapSource() is not null, source);
        bool rasterBlobShape = source is BlobAnalysisRoi && GetAnalysisBitmapSource() is not null &&
            kind is AdvancedMeasurementKind.Rectangle or AdvancedMeasurementKind.Slot or AdvancedMeasurementKind.ShapeMetrics;
        bool isAvailable = (availability.IsEnabled || rasterBlobShape)
            && AdvancedMeasurementMenuCatalog.CanSeed(kind, source, GetAnalysisBitmapSource() is not null);
        AdvancedMeasurementMenuDescriptor descriptor = AdvancedMeasurementMenuCatalog.Get(kind);
        var item = new MenuItem
        {
            Header = UiText.Get(headerKey ?? descriptor.HeaderKey),
            IsEnabled = isAvailable,
            Tag = new ImageViewerAdvancedMeasurementMenuTag(kind, source, hintKey)
        };
        if (hintKey is not null)
        {
            item.ToolTip = UiText.Get(hintKey);
        }
        else if (!isAvailable && availability.ReasonKey is not null)
        {
            item.ToolTip = UiText.Get(availability.ReasonKey);
        }
        else if (!isAvailable) item.ToolTip = UiText.Get("MenuNeedCompatibleSource");

        item.Click += (_, _) => ShowAdvancedMeasurements(kind, source);
        parent.Items.Add(item);
    }

    private void AddVertexDeletionShortcut(MenuItem parent, RoiBase selected)
    {
        if (_contextualPathVertexTarget is not { } target || !ReferenceEquals(target.Roi, selected))
        {
            return;
        }

        bool canDelete = ContextualPathVertexEdit.CanDelete(target, ViewerState.SelectedRoi, ViewerState.AllRois, out string reasonKey);
        var item = new MenuItem
        {
            Header = UiText.Get("ContextRoiDeleteVertex"),
            IsEnabled = canDelete,
            ToolTip = canDelete ? null : UiText.Get(reasonKey)
        };
        item.Click += (_, _) => DeleteContextualVertex(target);
        AddContextualMenuItem(item, parent);
    }

    private void DeleteContextualVertex(ContextualPathVertexTarget target)
    {
        if (!ContextualPathVertexEdit.TryDelete(target, ViewerState.SelectedRoi, ViewerState.AllRois, ViewerState, out string reasonKey))
        {
            ShowStatusHint(UiText.Get(reasonKey.Length == 0 ? "ContextRoiVertexStale" : reasonKey), StatusHintKind.Error);
            return;
        }

        _contextualPathVertexTarget = null;
        DrawRois();
        UpdateInfoPanel(force: true);
        _controlComposition.SessionController.MarkDirty();
        UpdateContextMenuState();
    }

    private void AddContextualMenuItem(MenuItem item, ItemsControl? parent = null)
    {
        _contextualGeneratedMenuItems.Add(item);
        (parent ?? contextRoiShortcutsMenuItem).Items.Add(item);
    }

    private static void AddAction(MenuItem parent, string header, Action action, bool enabled)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
    }

}

internal sealed record ContextualPathVertexTarget(RoiBase Roi, ObservableCollection<PointD> Points, int Index, PointD Point, PointD[] OriginalPoints);

internal static class ContextualPathVertexEdit
{
    public static bool CanDelete(ContextualPathVertexTarget target, RoiBase? selected, IReadOnlyList<RoiBase> rois, out string reasonKey)
    {
        reasonKey = "ContextRoiVertexStale";
        if (!ReferenceEquals(selected, target.Roi) || !rois.Contains(target.Roi))
        {
            return false;
        }

        if (target.Roi.IsLocked)
        {
            reasonKey = "ContextRoiVertexLocked";
            return false;
        }

        ObservableCollection<PointD>? currentPoints = target.Roi switch
        {
            PolygonRoi polygon => polygon.Points,
            PolylineRoi polyline => polyline.Points,
            _ => null
        };
        if (currentPoints is null || !ReferenceEquals(currentPoints, target.Points) ||
            target.Index < 0 || target.Index >= currentPoints.Count || currentPoints[target.Index] != target.Point ||
            !currentPoints.SequenceEqual(target.OriginalPoints))
        {
            return false;
        }

        int minimum = target.Roi is PolygonRoi ? 3 : target.Roi is PolylineRoi ? 2 : int.MaxValue;
        if (currentPoints.Count <= minimum)
        {
            reasonKey = "ContextRoiVertexMinimum";
            return false;
        }

        reasonKey = string.Empty;
        return true;
    }

    public static bool TryDelete(
        ContextualPathVertexTarget target,
        RoiBase? selected,
        IReadOnlyList<RoiBase> rois,
        ImageViewerViewModel viewModel,
        out string reasonKey)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (!CanDelete(target, selected, rois, out reasonKey))
        {
            return false;
        }

        RoiBase before = target.Roi.Clone();
        RoiBase after = target.Roi.Clone();
        if (after is PolygonRoi polygon)
        {
            polygon.Points.RemoveAt(target.Index);
        }
        else if (after is PolylineRoi polyline)
        {
            polyline.Points.RemoveAt(target.Index);
        }
        else
        {
            reasonKey = "ContextRoiVertexStale";
            return false;
        }

        viewModel.UndoRedo.Execute(new RoiStateCommand(target.Roi, before, after));
        reasonKey = string.Empty;
        return true;
    }
}
