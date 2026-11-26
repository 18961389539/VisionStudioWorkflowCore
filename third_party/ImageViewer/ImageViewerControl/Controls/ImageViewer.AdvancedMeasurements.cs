using System.Windows;
using ImageViewer.Core.Measurements;
using ImageViewer.Dialogs;
using ImageViewer.Models;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls;

public partial class ImageViewer
{
    private AdvancedMeasurementDialog? _advancedMeasurementDialog;
    private bool _advancedMeasurementsDisposed;
    /// <summary>Opens the associative measurement workbench for this viewer.</summary>
    public void ShowAdvancedMeasurements()
    {
        ObjectDisposedException.ThrowIf(_advancedMeasurementsDisposed, this);
        if (_advancedMeasurementDialog != null)
        {
            _advancedMeasurementDialog.Activate();
            return;
        }
        var dialog = new AdvancedMeasurementDialog(() => ViewerState.AllRois, () => PixelSize, () => PhysicalUnit,
            () => Calibration, SetAdvancedMeasurements, GetAnalysisBitmapSource,
            () => _controlComposition.ViewportController.TryGetCurrentImagePath(), () => WorkpieceCoordinateSystem,
            () => PluginRegistry);
        Window? owner = Window.GetWindow(this);
        if (owner != null) dialog.Owner = owner;
        dialog.Resources.MergedDictionaries.Add(ThemeDictionary ?? ViewerThemes.CreateDictionary(Theme));
        _advancedMeasurementDialog = dialog;
        Unloaded += OnAdvancedMeasurementViewerUnloaded;
        dialog.Closed += (_, _) =>
        {
            Unloaded -= OnAdvancedMeasurementViewerUnloaded;
            _advancedMeasurementDialog = null;
        };
        dialog.Show();
    }

    private void OnAdvancedMeasurementViewerUnloaded(object sender, RoutedEventArgs e) => CloseAdvancedMeasurementDialog();
    private void CloseAdvancedMeasurementDialog() => _advancedMeasurementDialog?.Close();
    private void DisposeAdvancedMeasurementDialog()
    {
        _advancedMeasurementsDisposed = true;
        CloseAdvancedMeasurementDialog();
    }

    private void OnAdvancedMeasurementsClick(object sender, RoutedEventArgs e) => ShowAdvancedMeasurements();

    /// <summary>Opens a specific advanced operation and seeds its source from the current object.</summary>
    public void ShowAdvancedMeasurements(AdvancedMeasurementKind kind, RoiBase? initialSource = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ShowAdvancedMeasurements();
        if (initialSource != null && !AdvancedMeasurementMenuCatalog.CanSeed(kind, initialSource, GetAnalysisBitmapSource() != null))
            initialSource = null;
        _advancedMeasurementDialog!.BeginMeasurement(kind, initialSource);
    }

    internal void SetAdvancedMeasurements(RoiBase roi, AdvancedMeasurementDefinition[] definitions)
    {
        if (!ViewerState.AllRois.Contains(roi)) throw new ArgumentException("Measurement owner is no longer present.", nameof(roi));
        ViewerState.UndoRedo.Execute(new AdvancedMeasurementEditCommand(roi, definitions));
        _controlComposition.SessionController.MarkDirty();
        UpdateContextMenuState();
    }

    private sealed class AdvancedMeasurementEditCommand : IUndoRedoCommand
    {
        private readonly RoiBase _roi;
        private readonly AdvancedMeasurementDefinition[] _before;
        private readonly AdvancedMeasurementDefinition[] _after;
        public AdvancedMeasurementEditCommand(RoiBase roi, AdvancedMeasurementDefinition[] definitions)
        {
            _roi = roi;
            _before = Clone(roi.AdvancedMeasurements);
            _after = Clone(definitions);
        }
        public void Execute() => _roi.AdvancedMeasurements = Clone(_after);
        public void Undo() => _roi.AdvancedMeasurements = Clone(_before);
        private static AdvancedMeasurementDefinition[] Clone(AdvancedMeasurementDefinition[] items) => items.Select(item => item.DeepClone()).ToArray();
    }
}
