using System.Threading;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ImageViewer.Dialogs;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.ViewModels;

namespace ImageViewer.Controls;

public partial class ImageViewer
{
    private CancellationTokenSource? _detectionRecommendationCancellation;
    private DetectionParameterRecommendationDialog? _detectionRecommendationDialog;
    private bool _detectionRecommendationsDisposed;

    /// <summary>Reviews image-based parameter suggestions and applies the accepted trial as one undoable edit.</summary>
    public async Task RecommendDetectionParametersAsync()
    {
        ObjectDisposedException.ThrowIf(_detectionRecommendationsDisposed, this);
        string? unavailable = GetDetectionRecommendationUnavailableReason();
        if (unavailable != null)
        {
            ShowStatusHint(UiText.Get(unavailable), StatusHintKind.Info);
            return;
        }

        RoiBase source = ViewerState.SelectedRoi!;
        RoiBase sourceSnapshot = source.Clone();
        var snapshot = DetectionRecommendationEdit.Capture(ViewerState, source, PixelSize, PhysicalUnit);
        var imageIdentity = ImageSource;
        long imageGeneration = _analysisState.SourceGeneration;
        var bitmap = GetAnalysisBitmapSource()!;
        if (!bitmap.IsFrozen)
        {
            bitmap = bitmap.CloneCurrentValue();
            bitmap.Freeze();
        }
        var quality = QualityProfile;
        string calibration = CalibrationFingerprint();
        var operation = new CancellationTokenSource();
        _detectionRecommendationCancellation = operation;
        Unloaded += OnDetectionRecommendationUnloaded;
        UpdateContextMenuState();
        ShowStatusHint(UiText.Get("DetectionRecommendationComputing"), StatusHintKind.Info);
        try
        {
            DetectionRecommendationPreview preview = await Task.Run(
                () => DetectionRecommendationPreviewService.Create(bitmap, sourceSnapshot, quality, operation.Token), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (!IsCurrent())
            {
                ShowStatusHint(UiText.Get("DetectionRecommendationStale"), StatusHintKind.Info);
                return;
            }
            var dialog = new DetectionParameterRecommendationDialog(preview, bitmap);
            if (Window.GetWindow(this) is { } owner) dialog.Owner = owner;
            _detectionRecommendationDialog = dialog;
            if (dialog.ShowDialog() == true && dialog.AcceptedRoi is { } accepted)
            {
                if (!IsCurrent() || !snapshot.TryApply(ViewerState, accepted, PixelSize, PhysicalUnit))
                {
                    ShowStatusHint(UiText.Get("DetectionRecommendationStale"), StatusHintKind.Info);
                    return;
                }
                _controlComposition.SessionController.MarkDirty();
                DrawRois();
                UpdateInfoPanel(force: true);
                ShowStatusHint(UiText.Get("DetectionRecommendationApplied"), StatusHintKind.Info);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            LogNonCriticalError("Recommend detection parameters", ex);
            ShowStatusHint(UiText.Get("DetectionRecommendationFailed"), StatusHintKind.Error);
        }
        finally
        {
            Unloaded -= OnDetectionRecommendationUnloaded;
            _detectionRecommendationDialog = null;
            _detectionRecommendationCancellation = null;
            operation.Dispose();
            if (!_detectionRecommendationsDisposed) UpdateContextMenuState();
        }

        bool IsCurrent() => !_detectionRecommendationsDisposed && !operation.IsCancellationRequested
            && ReferenceEquals(ImageSource, imageIdentity) && QualityProfile == quality
            && _analysisState.SourceGeneration == imageGeneration
            && CalibrationFingerprint() == calibration && snapshot.IsCurrent(ViewerState, PixelSize, PhysicalUnit);
    }

    private string CalibrationFingerprint() => Calibration is { } calibration
        ? JsonSerializer.Serialize(calibration, Core.Persistence.ImageViewerCoreJsonSerializationContext.Default.CameraCalibration)
        : string.Empty;

    private string? GetDetectionRecommendationUnavailableReason() => _detectionRecommendationCancellation != null
        ? "DetectionRecommendationBusy"
        : GetAnalysisBitmapSource() == null ? "MenuNeedImage"
        : ViewerState.SelectedRoi is not { } roi || !DetectionParameterRecommendationService.IsSupported(roi)
            ? "DetectionRecommendationNeedRoi" : roi.IsLocked ? "DetectionRecommendationLocked" : null;

    private MenuItem CreateDetectionRecommendationMenuItem()
    {
        var item = new MenuItem
        {
            Header = UiText.Get("DetectionRecommendationMenu"),
            Tag = new ImageViewerDetectionRecommendationMenuTag()
        };
        item.Click += OnDetectionRecommendationClick;
        return item;
    }

    private async void OnDetectionRecommendationClick(object sender, RoutedEventArgs e)
        => await RunUiOperationAsync(UiText.Get("DetectionRecommendationTitle"), RecommendDetectionParametersAsync);

    private void OnDetectionRecommendationUnloaded(object sender, RoutedEventArgs e)
    {
        _detectionRecommendationCancellation?.Cancel();
        _detectionRecommendationDialog?.Close();
    }

    private void DisposeDetectionRecommendations()
    {
        _detectionRecommendationsDisposed = true;
        OnDetectionRecommendationUnloaded(this, new RoutedEventArgs());
    }
}

internal sealed class ImageViewerDetectionRecommendationMenuTag : ImageViewerMenuCommandTag;

internal sealed class DetectionRecommendationEdit
{
    private readonly RoiBase _source;
    private readonly string _state;
    private DetectionRecommendationEdit(ImageViewerViewModel model, RoiBase source, double pixelSize, string? unit)
    {
        _source = source;
        _state = Serialize(model, source, pixelSize, unit);
    }

    internal static DetectionRecommendationEdit Capture(ImageViewerViewModel model, RoiBase source, double pixelSize, string? unit)
        => new(model, source, pixelSize, unit);

    internal bool IsCurrent(ImageViewerViewModel model, double pixelSize, string? unit) => !_source.IsLocked
        && model.AllRois.Contains(_source) && _state == Serialize(model, _source, pixelSize, unit);

    internal bool TryApply(ImageViewerViewModel model, RoiBase candidate, double pixelSize, string? unit)
    {
        if (!IsCurrent(model, pixelSize, unit) || candidate.GetType() != _source.GetType()
            || candidate.MeasurementId != _source.MeasurementId) return false;
        model.UndoRedo.Execute(new RoiStateCommand(_source, _source.Clone(), candidate.Clone()));
        return true;
    }

    private static string Serialize(ImageViewerViewModel model, RoiBase source, double pixelSize, string? unit)
        => RoiPersistenceService.Serialize([source], pixelSize, unit, model.PluginRegistry, model.QualityProfile);
}
