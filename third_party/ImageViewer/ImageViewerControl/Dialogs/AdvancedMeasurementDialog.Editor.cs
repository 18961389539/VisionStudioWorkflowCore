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
using ImageViewer.Services;
using Microsoft.Win32;

namespace ImageViewer.Dialogs;

internal sealed partial class AdvancedMeasurementDialog
{
    private void RefreshSourceInventory()
    {
        DetachRoiNotifications();
        _observedRois = _getRois() ?? Array.Empty<RoiBase>();
        foreach (RoiBase roi in _observedRois)
        {
            roi.PropertyChanged += OnRoiPropertyChanged;
            if (roi is PolygonRoi polygon && polygon.Points is INotifyCollectionChanged polygonPoints)
            {
                polygonPoints.CollectionChanged += OnRoiCollectionChanged;
                _observedGeometryCollections.Add(polygonPoints);
            }
            else if (roi is PolylineRoi polyline && polyline.Points is INotifyCollectionChanged polylinePoints)
            {
                polylinePoints.CollectionChanged += OnRoiCollectionChanged;
                _observedGeometryCollections.Add(polylinePoints);
            }
        }

        _observedCollection = _observedRois as INotifyCollectionChanged;
        if (_observedCollection is not null)
        {
            _observedCollection.CollectionChanged += OnRoiCollectionChanged;
        }

        Guid? selectedDefinitionId = (_definitionsList.SelectedItem as AdvancedMeasurementDefinition)?.Id;
        _sourceOptions.Clear();
        _savedDefinitions.Clear();
        _definitionOwners.Clear();
        foreach (RoiBase roi in _observedRois)
        {
            _sourceOptions.Add(new SourceOption(roi.MeasurementId, roi.DisplayName, roi, null));
            foreach (AdvancedMeasurementDefinition definition in roi.AdvancedMeasurements)
            {
                _definitionOwners[definition.Id] = roi;
                _savedDefinitions.Add(definition);
            }
        }

        foreach (AdvancedMeasurementDefinition definition in AdvancedMeasurementService.EnumerateDefinitions(_observedRois))
        {
            RoiBase? owner = _definitionOwners.GetValueOrDefault(definition.Id);
            string display = UiText.Format("AdvancedMeasurementSourceLine", definition.Name, owner?.DisplayName ?? string.Empty);
            _sourceOptions.Add(new SourceOption(definition.Id, display, null, definition));
        }

        if (selectedDefinitionId is Guid id)
        {
            _definitionsList.SelectedItem = _savedDefinitions.FirstOrDefault(item => item.Id == id);
        }

        UpdateSelectedSourceLabels();
    }

    private void AddSelectedSource()
    {
        if (_availableSourcesList.SelectedItem is not SourceOption option || _selectedSources.Any(item => item.Id == option.Id))
        {
            return;
        }

        _selectedSources.Add(option);
        _selectedSourcesList.SelectedItem = option;
    }

    private void RemoveSelectedSource()
    {
        if (_selectedSourcesList.SelectedItem is SourceOption option)
        {
            _selectedSources.Remove(option);
        }
    }

    private void MoveSelectedSource(int offset)
    {
        if (_selectedSourcesList.SelectedItem is not SourceOption option)
        {
            return;
        }

        int index = _selectedSources.IndexOf(option);
        int destination = index + offset;
        if (destination < 0 || destination >= _selectedSources.Count)
        {
            return;
        }

        _selectedSources.Move(index, destination);
        _selectedSourcesList.SelectedItem = option;
    }

    private void AddOrUpdateDefinition()
    {
        if (_operationCombo.SelectedItem is not OperationOption operation
            || !TryParseDouble(_parameterTextBox.Text, out double parameter)
            || !TryParseDouble(_nominalXTextBox.Text, out double nominalX)
            || !TryParseDouble(_nominalYTextBox.Text, out double nominalY)
            || !TryParseOptionalDouble(_lowerLimitTextBox.Text, out double? lowerLimit)
            || !TryParseOptionalDouble(_upperLimitTextBox.Text, out double? upperLimit))
        {
            ShowWarning("AdvancedMeasurementInvalidInput");
            return;
        }

        if (string.IsNullOrWhiteSpace(_nameTextBox.Text) || _selectedSources.Count == 0
            || (lowerLimit is double lower && upperLimit is double upper && lower > upper))
        {
            ShowWarning("AdvancedMeasurementInvalidInput");
            return;
        }

        AdvancedMeasurementKind kind = operation.Kind;
        AdvancedMeasurementDefinition? existing = _definitionsList.SelectedItem as AdvancedMeasurementDefinition;
        var definition = new AdvancedMeasurementDefinition
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = _nameTextBox.Text.Trim(),
            Kind = kind,
            Sources = _selectedSources.Select(source => source.Id).ToArray(),
            Parameter = parameter,
            NominalX = nominalX,
            NominalY = nominalY,
            NominalInWorkpieceCoordinates = _nominalInWorkpieceCheckBox.IsChecked == true,
            LowerLimit = lowerLimit,
            UpperLimit = upperLimit
        };

        RoiBase? owner = existing is not null && _definitionOwners.TryGetValue(existing.Id, out RoiBase? existingOwner)
            ? existingOwner
            : FindDefinitionOwner(definition.Sources) ?? (_observedRois.Count > 0 ? _observedRois[0] : null);
        if (owner is null)
        {
            ShowWarning("AdvancedMeasurementNoSources");
            return;
        }

        AdvancedMeasurementDefinition[] ownerDefinitions = owner.AdvancedMeasurements
            .Where(item => existing is null || item.Id != existing.Id)
            .Select(item => item.DeepClone())
            .Append(definition)
            .ToArray();
        _setDefinitions(owner, ownerDefinitions);
        _definitionsList.SelectedItem = definition;
        RefreshSourceInventory();
        _definitionsList.SelectedItem = _savedDefinitions.FirstOrDefault(item => item.Id == definition.Id);
    }

    private void DeleteSelectedDefinition()
    {
        if (_definitionsList.SelectedItem is not AdvancedMeasurementDefinition selected
            || !_definitionOwners.TryGetValue(selected.Id, out RoiBase? owner))
        {
            return;
        }

        _setDefinitions(owner, [.. owner.AdvancedMeasurements.Where(item => item.Id != selected.Id).Select(item => item.DeepClone())]);
        RefreshSourceInventory();
    }

    private void LoadSelectedDefinition()
    {
        if (_definitionsList.SelectedItem is not AdvancedMeasurementDefinition definition)
        {
            return;
        }

        _operationCombo.SelectedValue = definition.Kind;
        _nameTextBox.Text = definition.Name;
        _parameterTextBox.Text = Format(definition.Parameter);
        _nominalXTextBox.Text = Format(definition.NominalX);
        _nominalYTextBox.Text = Format(definition.NominalY);
        _lowerLimitTextBox.Text = definition.LowerLimit is double lower ? Format(lower) : string.Empty;
        _upperLimitTextBox.Text = definition.UpperLimit is double upper ? Format(upper) : string.Empty;
        _nominalInWorkpieceCheckBox.IsChecked = definition.NominalInWorkpieceCoordinates;
        _selectedSources.Clear();
        foreach (Guid sourceId in definition.Sources)
        {
            SourceOption? source = _sourceOptions.FirstOrDefault(item => item.Id == sourceId);
            if (source is not null)
            {
                _selectedSources.Add(source);
            }
        }
    }

    private void ClearDefinitionEditor()
    {
        _definitionsList.SelectedItem = null;
        _nameTextBox.Clear();
        _operationCombo.SelectedIndex = 0;
        _parameterTextBox.Text = "1";
        _nominalXTextBox.Text = "0";
        _nominalYTextBox.Text = "0";
        _lowerLimitTextBox.Clear();
        _upperLimitTextBox.Clear();
        _nominalInWorkpieceCheckBox.IsChecked = false;
        _selectedSources.Clear();
    }

    internal void BeginMeasurement(AdvancedMeasurementKind kind, RoiBase? initialSource)
    {
        RefreshSourceInventory();
        ClearDefinitionEditor();
        _operationCombo.SelectedValue = kind;
        _nameTextBox.Text = UiText.Get(OperationResourceKey(kind));
        if (initialSource != null)
        {
            SourceOption? source = _sourceOptions.FirstOrDefault(option => option.Id == initialSource.MeasurementId);
            if (source != null) _selectedSources.Add(source);
        }
    }


    private void UpdateParameterLabel()
    {
        _parameterLabel.Text = UiText.Get("AdvancedMeasurementParameterLabel");
        bool hasParameter = _operationCombo.SelectedValue is AdvancedMeasurementKind.GapProfile
            or AdvancedMeasurementKind.ProjectedDistance or AdvancedMeasurementKind.PeriodicPitch or AdvancedMeasurementKind.ContourDeviation;
        _parameterHintText.Visibility = hasParameter ? Visibility.Visible : Visibility.Collapsed;
        if (_parameterTextBox.Parent is UIElement parameterRow)
            parameterRow.Visibility = _parameterHintText.Visibility;
        bool isPosition = _operationCombo.SelectedValue is AdvancedMeasurementKind.Position;
        if (_nominalXTextBox.Parent is UIElement nominalRow)
            nominalRow.Visibility = isPosition ? Visibility.Visible : Visibility.Collapsed;
        _nominalInWorkpieceCheckBox.Visibility = isPosition ? Visibility.Visible : Visibility.Collapsed;
        if (_lowerLimitTextBox.Parent is UIElement limitRow)
            limitRow.Visibility = _operationCombo.SelectedValue is AdvancedMeasurementKind.Midpoint or AdvancedMeasurementKind.Intersection
                ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSelectedSourceLabels()
    {
        foreach (SourceOption selected in _selectedSources.ToArray())
        {
            SourceOption? refreshed = _sourceOptions.FirstOrDefault(option => option.Id == selected.Id);
            if (refreshed is not null)
            {
                int index = _selectedSources.IndexOf(selected);
                _selectedSources[index] = refreshed;
            }
        }
    }


    private void ShowWarning(string resourceKey) => MessageBox.Show(this, UiText.Get(resourceKey), Title, MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool TryParseDouble(string text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
    }

    private static bool TryParseOptionalDouble(string text, out double? value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        if (TryParseDouble(text, out double parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }
}
