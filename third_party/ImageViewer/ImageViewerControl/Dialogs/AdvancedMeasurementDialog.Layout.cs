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
    private void InitializeWindowContent()
    {
        Title = UiText.Get("AdvancedMeasurementDialogTitle");
        Width = 1160;
        Height = 760;
        MinWidth = 850;
        MinHeight = 560;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemColors.ControlBrush;
        AutomationProperties.SetName(this, Title);

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var headingPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        headingPanel.Children.Add(new TextBlock
        {
            Text = UiText.Get("AdvancedMeasurementDialogHeading"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        headingPanel.Children.Add(new TextBlock
        {
            Text = UiText.Get("AdvancedMeasurementDialogDescription"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush
        });
        root.Children.Add(headingPanel);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(370) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var editor = new Grid { Margin = new Thickness(0, 0, 14, 0) };
        editor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(editor, 0);
        body.Children.Add(editor);

        var editorContent = new StackPanel();
        editorContent.Children.Add(SectionHeader("AdvancedMeasurementOperationLabel"));
        _operationCombo = SetAutomationName(new ComboBox
        {
            ItemsSource = Enum.GetValues<AdvancedMeasurementKind>().Select(kind => new OperationOption(kind, UiText.Get(OperationResourceKey(kind)))).ToArray(),
            DisplayMemberPath = nameof(OperationOption.DisplayName),
            SelectedValuePath = nameof(OperationOption.Kind),
            Margin = new Thickness(0, 0, 0, 8)
        }, UiText.Get("AdvancedMeasurementOperationLabel"));
        _operationCombo.SelectionChanged += (_, _) => UpdateParameterLabel();
        editorContent.Children.Add(_operationCombo);

        _nameTextBox = AddTextField(editorContent, "AdvancedMeasurementNameLabel");
        var parameterRow = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        parameterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parameterRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        _parameterLabel = new TextBlock
        {
            Text = UiText.Get("AdvancedMeasurementParameterLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        _parameterTextBox = SetAutomationName(new TextBox { Text = "1" }, UiText.Get("AdvancedMeasurementParameterLabel"));
        Grid.SetColumn(_parameterTextBox, 1);
        parameterRow.Children.Add(_parameterLabel);
        parameterRow.Children.Add(_parameterTextBox);
        editorContent.Children.Add(parameterRow);
        _parameterHintText = new TextBlock
        {
            Text = UiText.Get("AdvancedMeasurementParameterHint"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(2, 0, 2, 5),
            Visibility = Visibility.Collapsed
        };
        editorContent.Children.Add(_parameterHintText);

        var nominalGrid = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        nominalGrid.ColumnDefinitions.Add(new ColumnDefinition());
        nominalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        nominalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        nominalGrid.ColumnDefinitions.Add(new ColumnDefinition());
        nominalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        AddCompactField(nominalGrid, 0, 1, "AdvancedMeasurementNominalXLabel", out _nominalXTextBox);
        AddCompactField(nominalGrid, 3, 4, "AdvancedMeasurementNominalYLabel", out _nominalYTextBox);
        _nominalXTextBox.Text = "0";
        _nominalYTextBox.Text = "0";
        editorContent.Children.Add(nominalGrid);
        _nominalInWorkpieceCheckBox = SetAutomationName(new CheckBox
        {
            Content = UiText.Get("AdvancedMeasurementWorkpieceNominal"),
            Margin = new Thickness(0, 2, 0, 6)
        }, UiText.Get("AdvancedMeasurementWorkpieceNominal"));
        editorContent.Children.Add(_nominalInWorkpieceCheckBox);

        var limitGrid = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        limitGrid.ColumnDefinitions.Add(new ColumnDefinition());
        limitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        limitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        limitGrid.ColumnDefinitions.Add(new ColumnDefinition());
        limitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
        AddCompactField(limitGrid, 0, 1, "AdvancedMeasurementLowerLimitLabel", out _lowerLimitTextBox);
        AddCompactField(limitGrid, 3, 4, "AdvancedMeasurementUpperLimitLabel", out _upperLimitTextBox);
        editorContent.Children.Add(limitGrid);

        editorContent.Children.Add(SectionHeader("AdvancedMeasurementAvailableSourcesLabel"));
        _availableSourcesList = SetAutomationName(new ListBox
        {
            ItemsSource = _sourceOptions,
            DisplayMemberPath = nameof(SourceOption.DisplayName),
            SelectionMode = SelectionMode.Single,
            Height = 100
        }, UiText.Get("AdvancedMeasurementAvailableSourcesLabel"));
        editorContent.Children.Add(_availableSourcesList);
        editorContent.Children.Add(new TextBlock
        {
            Text = UiText.Get("AdvancedMeasurementSourceHint"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(2, 4, 2, 4)
        });

        var sourceButtons = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        sourceButtons.Children.Add(CreateButton("AdvancedMeasurementSourceAdd", AddSelectedSource));
        sourceButtons.Children.Add(CreateButton("AdvancedMeasurementRefreshSources", RefreshSourceInventory));
        editorContent.Children.Add(sourceButtons);

        editorContent.Children.Add(SectionHeader("AdvancedMeasurementSourcesLabel"));
        _selectedSourcesList = SetAutomationName(new ListBox
        {
            ItemsSource = _selectedSources,
            DisplayMemberPath = nameof(SourceOption.DisplayName),
            SelectionMode = SelectionMode.Single,
            Height = 88
        }, UiText.Get("AdvancedMeasurementSourcesLabel"));
        editorContent.Children.Add(_selectedSourcesList);
        var orderButtons = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        orderButtons.Children.Add(CreateButton("AdvancedMeasurementSourceRemove", RemoveSelectedSource));
        orderButtons.Children.Add(CreateButton("AdvancedMeasurementSourceUp", () => MoveSelectedSource(-1)));
        orderButtons.Children.Add(CreateButton("AdvancedMeasurementSourceDown", () => MoveSelectedSource(1)));
        editorContent.Children.Add(orderButtons);

        var inputScroll = new ScrollViewer
        {
            Content = editorContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        editor.Children.Add(inputScroll);

        _definitionsList = SetAutomationName(new ListBox
        {
            ItemsSource = _savedDefinitions,
            DisplayMemberPath = nameof(AdvancedMeasurementDefinition.Name),
            Height = 112
        }, UiText.Get("AdvancedMeasurementSourcesLabel"));
        Grid.SetRow(_definitionsList, 1);
        editor.Children.Add(_definitionsList);

        var createButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        createButtons.Children.Add(CreateButton("AdvancedMeasurementAdd", AddOrUpdateDefinition, isDefault: true));
        createButtons.Children.Add(CreateButton("AdvancedMeasurementNew", ClearDefinitionEditor));
        createButtons.Children.Add(CreateButton("AdvancedMeasurementDelete", DeleteSelectedDefinition));
        Grid.SetRow(createButtons, 2);
        editor.RowDefinitions.Insert(1, new RowDefinition { Height = GridLength.Auto });
        editor.Children.Add(createButtons);
        _definitionsList.Margin = new Thickness(0, 6, 0, 0);

        var analysis = new Grid();
        analysis.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        analysis.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(analysis, 1);
        body.Children.Add(analysis);

        var resultsPanel = new DockPanel { LastChildFill = true };
        var resultsHeading = SectionHeader("AdvancedMeasurementResultsLabel");
        DockPanel.SetDock(resultsHeading, Dock.Top);
        resultsPanel.Children.Add(resultsHeading);
        _resultsList = SetAutomationName(new ListBox
        {
            ItemsSource = _evaluationRows,
            DisplayMemberPath = nameof(EvaluationRow.Summary)
        }, UiText.Get("AdvancedMeasurementResultsLabel"));
        _resultsList.SelectionChanged += (_, _) => ShowSelectedEvaluation();
        resultsPanel.Children.Add(_resultsList);
        _detailsText = SetAutomationName(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        }, UiText.Get("AdvancedMeasurementResultsLabel"));
        var detailsScroll = new ScrollViewer
        {
            Content = _detailsText,
            MaxHeight = 180,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 8, 0, 0)
        };
        DockPanel.SetDock(detailsScroll, Dock.Bottom);
        resultsPanel.Children.Add(detailsScroll);
        Grid.SetRow(resultsPanel, 0);
        analysis.Children.Add(resultsPanel);

        var previewPanel = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        previewPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        previewPanel.ColumnDefinitions.Add(new ColumnDefinition());
        previewPanel.ColumnDefinitions.Add(new ColumnDefinition());
        var witnessHeader = SectionHeader("AdvancedMeasurementPreviewLabel");
        Grid.SetColumn(witnessHeader, 0);
        previewPanel.Children.Add(witnessHeader);
        var samplesHeader = SectionHeader("AdvancedMeasurementSamplesLabel");
        Grid.SetColumn(samplesHeader, 1);
        previewPanel.Children.Add(samplesHeader);
        _witnessCanvas = CreatePreviewCanvas();
        _samplesCanvas = CreatePreviewCanvas();
        Grid.SetRow(_witnessCanvas, 1);
        Grid.SetRow(_samplesCanvas, 1);
        Grid.SetColumn(_samplesCanvas, 1);
        previewPanel.Children.Add(_witnessCanvas);
        previewPanel.Children.Add(_samplesCanvas);
        Grid.SetRow(previewPanel, 1);
        analysis.Children.Add(previewPanel);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var leftButtons = new WrapPanel();
        leftButtons.Children.Add(CreateButton("AdvancedMeasurementRefresh", EvaluateAll));
        _exportButton = CreateButton("AdvancedMeasurementExport", ExportCsvAsync);
        leftButtons.Children.Add(_exportButton);
        DockPanel.SetDock(leftButtons, Dock.Left);
        footer.Children.Add(leftButtons);
        var closeButton = CreateButton("AdvancedMeasurementClose", Close);
        closeButton.IsCancel = true;
        closeButton.HorizontalAlignment = HorizontalAlignment.Right;
        DockPanel.SetDock(closeButton, Dock.Right);
        footer.Children.Add(closeButton);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
    }

    private static Canvas CreatePreviewCanvas() => new()
    {
        Background = SystemColors.WindowBrush,
        ClipToBounds = true,
        MinHeight = 140,
        Margin = new Thickness(0, 0, 6, 0)
    };

    private static void AddCanvasHint(Canvas canvas, string text)
    {
        var hint = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(8) };
        canvas.Children.Add(hint);
    }

    private static void ClearPreview(Canvas canvas, string resourceKey)
    {
        canvas.Children.Clear();
        AddCanvasHint(canvas, UiText.Get(resourceKey));
    }

    private static TextBlock SectionHeader(string key) => new()
    {
        Text = UiText.Get(key),
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 8, 0, 4)
    };

    private static TextBox AddTextField(Panel panel, string labelKey)
    {
        panel.Children.Add(SectionHeader(labelKey));
        TextBox textBox = SetAutomationName(new TextBox(), UiText.Get(labelKey));
        panel.Children.Add(textBox);
        return textBox;
    }

    private static void AddCompactField(Grid grid, int labelColumn, int fieldColumn, string labelKey, out TextBox textBox)
    {
        var label = new TextBlock
        {
            Text = UiText.Get(labelKey),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        textBox = SetAutomationName(new TextBox(), UiText.Get(labelKey));
        Grid.SetColumn(label, labelColumn);
        Grid.SetColumn(textBox, fieldColumn);
        grid.Children.Add(label);
        grid.Children.Add(textBox);
    }

    private static Button CreateButton(string key, Action action, bool isDefault = false)
    {
        var button = new Button
        {
            Content = UiText.Get(key),
            IsDefault = isDefault,
            Margin = new Thickness(0, 0, 6, 4),
            Padding = new Thickness(8, 3, 8, 3)
        };
        AutomationProperties.SetName(button, UiText.Get(key));
        button.Click += (_, _) => action();
        return button;
    }

    private static T SetAutomationName<T>(T element, string name) where T : DependencyObject
    {
        AutomationProperties.SetName(element, name);
        return element;
    }

    private static string OperationResourceKey(AdvancedMeasurementKind kind) => kind switch
    {
        AdvancedMeasurementKind.Midpoint => "AdvancedMeasurementOperationMidpoint",
        AdvancedMeasurementKind.Intersection => "AdvancedMeasurementOperationIntersection",
        AdvancedMeasurementKind.Projection => "AdvancedMeasurementOperationProjection",
        AdvancedMeasurementKind.Centerline => "AdvancedMeasurementOperationCenterline",
        AdvancedMeasurementKind.AngleBisector => "AdvancedMeasurementOperationAngleBisector",
        AdvancedMeasurementKind.ShortestDistance => "AdvancedMeasurementOperationShortestDistance",
        AdvancedMeasurementKind.ProjectedDistance => "AdvancedMeasurementOperationProjectedDistance",
        AdvancedMeasurementKind.Straightness => "AdvancedMeasurementOperationStraightness",
        AdvancedMeasurementKind.Circularity => "AdvancedMeasurementOperationCircularity",
        AdvancedMeasurementKind.Rectangle => "AdvancedMeasurementOperationRectangle",
        AdvancedMeasurementKind.Slot => "AdvancedMeasurementOperationSlot",
        AdvancedMeasurementKind.GapProfile => "AdvancedMeasurementOperationGapProfile",
        AdvancedMeasurementKind.Position => "AdvancedMeasurementOperationPosition",
        AdvancedMeasurementKind.Pitch => "AdvancedMeasurementOperationPitch",
        AdvancedMeasurementKind.DistributionCircle => "AdvancedMeasurementOperationDistributionCircle",
        AdvancedMeasurementKind.ContourDeviation => "AdvancedMeasurementOperationContourDeviation",
        AdvancedMeasurementKind.ShapeMetrics => "AdvancedMeasurementOperationShapeMetrics",
        AdvancedMeasurementKind.PeriodicPitch => "AdvancedMeasurementOperationPeriodicPitch",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

}
