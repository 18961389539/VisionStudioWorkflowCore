using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ImageViewer.Core.Geometry;
using ImageViewer.Core.Measurements;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Microsoft.Win32;

namespace ImageViewer.Dialogs;

/// <summary>Interactive workbench for creating, evaluating, inspecting, and exporting associative measurements.</summary>
internal sealed partial class AdvancedMeasurementDialog : Window
{
    private readonly Func<IReadOnlyList<RoiBase>> _getRois;
    private readonly Func<double> _getPixelSize;
    private readonly Func<string> _getUnit;
    private readonly Func<CameraCalibration?> _getCalibration;
    private readonly Action<RoiBase, AdvancedMeasurementDefinition[]> _setDefinitions;
    private readonly Func<System.Windows.Media.Imaging.BitmapSource?>? _getBitmap;
    private readonly Func<string?>? _getImagePath;
    private readonly Func<WorkpieceCoordinateSystem?>? _getWorkpiece;
    private readonly Func<RoiPluginRegistry>? _getPluginRegistry;
    private Button _exportButton = null!;
    private CancellationTokenSource? _exportCancellation;
    private readonly ObservableCollection<SourceOption> _sourceOptions = [];
    private readonly ObservableCollection<SourceOption> _selectedSources = [];
    private readonly ObservableCollection<AdvancedMeasurementDefinition> _savedDefinitions = [];
    private readonly ObservableCollection<EvaluationRow> _evaluationRows = [];
    private readonly Dictionary<Guid, RoiBase> _definitionOwners = [];
    private ComboBox _operationCombo = null!;
    private TextBox _nameTextBox = null!;
    private TextBox _parameterTextBox = null!;
    private TextBox _nominalXTextBox = null!;
    private TextBox _nominalYTextBox = null!;
    private TextBox _lowerLimitTextBox = null!;
    private TextBox _upperLimitTextBox = null!;
    private CheckBox _nominalInWorkpieceCheckBox = null!;
    private ListBox _availableSourcesList = null!;
    private ListBox _selectedSourcesList = null!;
    private ListBox _definitionsList = null!;
    private ListBox _resultsList = null!;
    private TextBlock _parameterLabel = null!;
    private TextBlock _parameterHintText = null!;
    private TextBlock _detailsText = null!;
    private Canvas _witnessCanvas = null!;
    private Canvas _samplesCanvas = null!;
    private IReadOnlyList<RoiBase> _observedRois = Array.Empty<RoiBase>();
    private INotifyCollectionChanged? _observedCollection;
    private readonly List<INotifyCollectionChanged> _observedGeometryCollections = [];
    private bool _refreshQueued;
    private bool _disposed;


    /// <summary>Creates the live analysis workbench.</summary>
    public AdvancedMeasurementDialog(
        Func<IReadOnlyList<RoiBase>> getRois,
        Func<double> getPixelSize,
        Func<string> getUnit,
        Func<CameraCalibration?> getCalibration,
        Action<RoiBase, AdvancedMeasurementDefinition[]> setDefinitions,
        Func<System.Windows.Media.Imaging.BitmapSource?>? getBitmap = null,
        Func<string?>? getImagePath = null,
        Func<WorkpieceCoordinateSystem?>? getWorkpiece = null,
        Func<RoiPluginRegistry>? getPluginRegistry = null)
    {
        _getRois = getRois ?? throw new ArgumentNullException(nameof(getRois));
        _getPixelSize = getPixelSize ?? throw new ArgumentNullException(nameof(getPixelSize));
        _getUnit = getUnit ?? throw new ArgumentNullException(nameof(getUnit));
        _getCalibration = getCalibration ?? throw new ArgumentNullException(nameof(getCalibration));
        _setDefinitions = setDefinitions ?? throw new ArgumentNullException(nameof(setDefinitions));
        _getBitmap = getBitmap;
        _getImagePath = getImagePath;
        _getWorkpiece = getWorkpiece;
        _getPluginRegistry = getPluginRegistry;

        InitializeWindowContent();

        _operationCombo.SelectedIndex = 0;
        _definitionsList.SelectionChanged += (_, _) => LoadSelectedDefinition();
        Closed += OnClosed;
        RefreshSourceInventory();
        EvaluateAll();
    }

    private void EvaluateAll()
    {
        IReadOnlyList<RoiBase> rois = _getRois();
        IReadOnlyList<AdvancedMeasurementEvaluation> results;
        try
        {
            results = AdvancedMeasurementService.EvaluateAll(rois, _getPixelSize(), _getUnit(), _getCalibration(), _getBitmap?.Invoke(), _getWorkpiece?.Invoke());
        }
        catch (Exception exception)
        {
            _evaluationRows.Clear();
            _detailsText.Text = UiText.Format("AdvancedMeasurementEvaluateFailed", exception.Message);
            ClearPreview(_witnessCanvas, "AdvancedMeasurementNoPreview");
            ClearPreview(_samplesCanvas, "AdvancedMeasurementNoSamples");
            return;
        }

        Dictionary<Guid, AdvancedMeasurementDefinition> definitions = AdvancedMeasurementService.EnumerateDefinitions(rois)
            .GroupBy(item => item.Id)
            .ToDictionary(group => group.Key, group => group.First());
        Guid? selectedId = (_resultsList.SelectedItem as EvaluationRow)?.Evaluation.Id;
        _evaluationRows.Clear();
        foreach (AdvancedMeasurementEvaluation evaluation in results)
        {
            if (definitions.TryGetValue(evaluation.Id, out AdvancedMeasurementDefinition? definition))
            {
                _evaluationRows.Add(new EvaluationRow(definition, evaluation, FormatSummary(definition, evaluation)));
            }
        }

        _resultsList.SelectedItem = selectedId is Guid id
            ? _evaluationRows.FirstOrDefault(row => row.Evaluation.Id == id)
            : _evaluationRows.FirstOrDefault();
        ShowSelectedEvaluation();
    }

    private string FormatSummary(AdvancedMeasurementDefinition definition, AdvancedMeasurementEvaluation evaluation)
    {
        string status = evaluation.Success
            ? evaluation.WithinLimits switch
            {
                true => UiText.Get("AdvancedMeasurementStatusWithinLimits"),
                false => UiText.Get("AdvancedMeasurementStatusOutOfLimits"),
                null => UiText.Get("AdvancedMeasurementStatusNoLimits")
            }
            : UiText.Get("AdvancedMeasurementStatusFailure");
        string primary = evaluation.Metrics.FirstOrDefault() is AdvancedMeasurementMetric metric
            ? FormatMetric(metric)
            : UiText.Get("AdvancedMeasurementNoSamples");
        string summary = UiText.Format("AdvancedMeasurementResultRow", definition.Name, status, primary);
        return evaluation.Warnings.Length > 0
            ? summary + UiText.Get("AdvancedMeasurementWarningSuffix")
            : summary;
    }

    private string FormatMetric(AdvancedMeasurementMetric metric)
    {
        string unit = metric.Unit ?? (metric.LengthPower == 0 ? string.Empty : _getUnit());
        if (metric.Unit is null && metric.LengthPower == 2 && !string.IsNullOrWhiteSpace(unit))
        {
            unit += "²";
        }

        return UiText.Format("AdvancedMeasurementMetricRow", UiText.Get(metric.Name), metric.Value, unit);
    }

    private void ShowSelectedEvaluation()
    {
        if (_resultsList.SelectedItem is not EvaluationRow row)
        {
            _detailsText.Text = UiText.Get("AdvancedMeasurementNoDefinitions");
            ClearPreview(_witnessCanvas, "AdvancedMeasurementNoPreview");
            ClearPreview(_samplesCanvas, "AdvancedMeasurementNoSamples");
            return;
        }

        AdvancedMeasurementEvaluation result = row.Evaluation;
        var lines = new List<string>
        {
            $"{UiText.Get("AdvancedMeasurementStatusLabel")}: {FormatSummary(row.Definition, result)}",
            UiText.Format("AdvancedMeasurementMethodRow", result.Method.Length > 0 ? UiText.Get(result.Method) : UiText.Get("AdvancedMeasurementStatusFailure"))
        };
        string[] sourceNames = row.Definition.Sources
            .Select(id => _sourceOptions.FirstOrDefault(option => option.Id == id)?.DisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        lines.Add(UiText.Format("AdvancedMeasurementSourceLine", row.Definition.Name, string.Join(", ", sourceNames)));
        if (result.Metrics.Length > 0)
        {
            lines.AddRange(result.Metrics.Select(FormatMetric));
        }

        if (result.WithinLimits is bool withinLimits)
        {
            lines.Add(UiText.Get(withinLimits ? "AdvancedMeasurementStatusWithinLimits" : "AdvancedMeasurementStatusOutOfLimits"));
        }

        foreach (string warning in result.Warnings)
        {
            lines.Add(UiText.Format("AdvancedMeasurementWarningRow", warning));
        }

        for (int i = 0; i < result.Samples.Length; i++)
        {
            double station = i < result.Stations.Length ? result.Stations[i] : i;
            lines.Add(UiText.Format("AdvancedMeasurementSampleTooltip", i + 1, Format(station), Format(result.Samples[i]), result.Metrics.FirstOrDefault()?.Unit ?? _getUnit()));
        }

        if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
        {
            lines.Add(UiText.Format("AdvancedMeasurementErrorRow", result.Error));
        }

        _detailsText.Text = string.Join(Environment.NewLine, lines);
        DrawWitness(_witnessCanvas, row);
        DrawSamples(_samplesCanvas, result.Stations, result.Samples);
    }

    private async void ExportCsvAsync()
    {
        if (_exportCancellation != null || _disposed)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        _exportButton.IsEnabled = false;
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = UiText.Get("FileDialogSaveAnalysisCsvTitle"),
                Filter = UiText.Get("FileDialogCsvFilter"),
                DefaultExt = ".csv",
                AddExtension = true
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            _detailsText.Text = UiText.Get("AdvancedMeasurementExportInProgress");
            await AdvancedMeasurementExportService.SaveAsync(
                dialog.FileName,
                _getPluginRegistry?.Invoke() ?? RoiPluginRegistry.CreateBuiltIn(),
                _getRois(),
                _getPixelSize(),
                _getUnit(),
                _getCalibration(),
                _getBitmap?.Invoke(),
                _getImagePath?.Invoke(),
                _getWorkpiece?.Invoke(),
                cancellation.Token);
            if (!_disposed)
            {
                _detailsText.Text = UiText.Get("AdvancedMeasurementExportComplete");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                MessageBox.Show(this, $"{UiText.Get("AdvancedMeasurementSaveFailed")}: {exception.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _exportCancellation = null;
            if (!_disposed)
            {
                _exportButton.IsEnabled = true;
            }
        }
    }

    private RoiBase? FindDefinitionOwner(IEnumerable<Guid> sourceIds)
    {
        var pending = new Queue<Guid>(sourceIds);
        var visited = new HashSet<Guid>();
        IReadOnlyList<AdvancedMeasurementDefinition> definitions = AdvancedMeasurementService.EnumerateDefinitions(_observedRois);
        while (pending.Count > 0)
        {
            Guid id = pending.Dequeue();
            if (!visited.Add(id))
            {
                continue;
            }

            RoiBase? roi = _observedRois.FirstOrDefault(item => item.MeasurementId == id);
            if (roi is not null)
            {
                return roi;
            }

            AdvancedMeasurementDefinition? definition = definitions.FirstOrDefault(item => item.Id == id);
            if (definition is not null)
            {
                foreach (Guid nestedSource in definition.Sources)
                {
                    pending.Enqueue(nestedSource);
                }
            }
        }

        return null;
    }

    private void QueueEvaluationRefresh()
    {
        if (_disposed || _refreshQueued || !IsLoaded)
        {
            return;
        }

        _refreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _refreshQueued = false;
            if (!_disposed)
            {
                RefreshSourceInventory();
                EvaluateAll();
            }
        }));
    }

    private void OnRoiPropertyChanged(object? sender, PropertyChangedEventArgs args) => QueueEvaluationRefresh();

    private void OnRoiCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueEvaluationRefresh();

    private void OnClosed(object? sender, EventArgs args)
    {
        _disposed = true;
        _exportCancellation?.Cancel();
        DetachRoiNotifications();
    }

    private void DetachRoiNotifications()
    {
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged -= OnRoiCollectionChanged;
            _observedCollection = null;
        }

        foreach (RoiBase roi in _observedRois)
        {
            roi.PropertyChanged -= OnRoiPropertyChanged;
        }

        foreach (INotifyCollectionChanged collection in _observedGeometryCollections)
        {
            collection.CollectionChanged -= OnRoiCollectionChanged;
        }

        _observedGeometryCollections.Clear();

        _observedRois = Array.Empty<RoiBase>();
    }


    private static string Format(double value) => value.ToString("G6", CultureInfo.CurrentCulture);

    private sealed record SourceOption(Guid Id, string DisplayName, RoiBase? Roi, AdvancedMeasurementDefinition? Definition);

    private sealed record OperationOption(AdvancedMeasurementKind Kind, string DisplayName);

    private sealed record GeometryLayer(IReadOnlyList<PointD> Points, Brush Stroke, bool Closed);

    private sealed record EvaluationRow(AdvancedMeasurementDefinition Definition, AdvancedMeasurementEvaluation Evaluation, string Summary);
}
