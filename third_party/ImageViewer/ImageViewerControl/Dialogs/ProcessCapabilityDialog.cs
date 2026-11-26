using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ImageViewer.Controls;
using ImageViewer.Core.Spc;
using ImageViewer.Localization;
using ImageViewer.Services;

namespace ImageViewer.Dialogs;

/// <summary>
/// 过程能力与 Xbar-R 控制图窗口。
/// Chinese: 数据来自当前图像的同类 ROI（同一工件上的重复特征），子组大小由用户选择；
/// 样本不足以构成两个完整子组时只显示能力指数，不画控制图——控制限没有样本支撑就是编出来的。
/// English: Shows capability indices plus Xbar-R charts; charts require at least two complete subgroups.
/// </summary>
internal sealed class ProcessCapabilityDialog : Window
{
    private readonly IReadOnlyList<SpcMeasurementSeries> _series;
    private readonly ComboBox _seriesCombo;
    private readonly ComboBox _subgroupCombo;
    private readonly TextBlock _sampleCountText;
    private readonly TextBlock _meanText;
    private readonly TextBlock _sigmaWithinText;
    private readonly TextBlock _sigmaOverallText;
    private readonly TextBlock _cpText;
    private readonly TextBlock _cpkText;
    private readonly TextBlock _ppText;
    private readonly TextBlock _ppkText;
    private readonly TextBlock _specLimitsText;
    private readonly TextBlock _violationText;
    private readonly TextBlock _statusText;
    private readonly Canvas _meanCanvas;
    private readonly Canvas _rangeCanvas;
    private XbarRangeControlChart? _chart;
    private bool _refreshing;
    private bool _disposed;

    public ProcessCapabilityDialog(IReadOnlyList<SpcMeasurementSeries> series)
    {
        _series = series ?? throw new ArgumentNullException(nameof(series));

        Title = UiText.Get("ProcessCapabilityDialogTitle");
        Width = 900;
        Height = 760;
        MinWidth = 700;
        MinHeight = 560;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemColors.ControlBrush;
        AutomationProperties.SetName(this, Title);

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        heading.Children.Add(new TextBlock
        {
            Text = UiText.Get("ProcessCapabilityDialogHeading"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        heading.Children.Add(new TextBlock
        {
            Text = UiText.Get("ProcessCapabilityDialogDescription"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush
        });
        root.Children.Add(heading);

        var content = new StackPanel();
        var scrollViewer = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scrollViewer, 1);
        root.Children.Add(scrollViewer);

        var selectors = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        content.Children.Add(selectors);

        var seriesPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        seriesPanel.Children.Add(new TextBlock
        {
            Text = UiText.Get("ProcessCapabilitySeriesLabel"),
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 0, 0, 2)
        });
        _seriesCombo = new ComboBox
        {
            ItemsSource = _series,
            DisplayMemberPath = nameof(SpcMeasurementSeries.DisplayName)
        };
        AutomationProperties.SetName(_seriesCombo, UiText.Get("ProcessCapabilitySeriesLabel"));
        seriesPanel.Children.Add(_seriesCombo);
        Grid.SetColumn(seriesPanel, 0);
        selectors.Children.Add(seriesPanel);

        var subgroupPanel = new StackPanel();
        subgroupPanel.Children.Add(new TextBlock
        {
            Text = UiText.Get("ProcessCapabilitySubgroupLabel"),
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 0, 0, 2)
        });
        _subgroupCombo = new ComboBox();
        AutomationProperties.SetName(_subgroupCombo, UiText.Get("ProcessCapabilitySubgroupLabel"));
        subgroupPanel.Children.Add(_subgroupCombo);
        Grid.SetColumn(subgroupPanel, 1);
        selectors.Children.Add(subgroupPanel);

        var summary = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        for (int column = 0; column < 4; column++)
        {
            summary.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = column % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star)
            });
        }

        for (int row = 0; row < 5; row++)
        {
            summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        _sampleCountText = AddMetric(summary, 0, 0, "ProcessCapabilitySampleCount");
        _meanText = AddMetric(summary, 0, 2, "ProcessCapabilityMean");
        _sigmaWithinText = AddMetric(summary, 1, 0, "ProcessCapabilitySigmaWithin");
        _sigmaOverallText = AddMetric(summary, 1, 2, "ProcessCapabilitySigmaOverall");
        _cpText = AddMetric(summary, 2, 0, "ProcessCapabilityCp");
        _cpkText = AddMetric(summary, 2, 2, "ProcessCapabilityCpk");
        _ppText = AddMetric(summary, 3, 0, "ProcessCapabilityPp");
        _ppkText = AddMetric(summary, 3, 2, "ProcessCapabilityPpk");
        _specLimitsText = AddMetric(summary, 4, 0, "ProcessCapabilitySpecLimits");
        _violationText = AddMetric(summary, 4, 2, "ProcessCapabilityViolations");
        content.Children.Add(summary);

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 0, 0, 6)
        };
        content.Children.Add(_statusText);

        _meanCanvas = CreateChartCanvas();
        _rangeCanvas = CreateChartCanvas();
        content.Children.Add(_meanCanvas);
        content.Children.Add(_rangeCanvas);

        var closeButton = new Button
        {
            Content = UiText.Get("DialogButtonClose"),
            IsCancel = true,
            IsDefault = true,
            MinWidth = 88,
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        closeButton.Click += (_, _) => Close();
        Grid.SetRow(closeButton, 2);
        root.Children.Add(closeButton);

        Content = root;

        _seriesCombo.SelectionChanged += OnSeriesSelectionChanged;
        _subgroupCombo.SelectionChanged += (_, _) =>
        {
            if (!_refreshing)
            {
                Refresh();
            }
        };
        _meanCanvas.SizeChanged += (_, _) => RedrawCharts();
        _rangeCanvas.SizeChanged += (_, _) => RedrawCharts();

        if (_series.Count > 0)
        {
            _seriesCombo.SelectedIndex = 0;
        }

        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }

    private static Canvas CreateChartCanvas() => new()
    {
        Height = 200,
        Margin = new Thickness(0, 6, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        ClipToBounds = true
    };

    private static TextBlock AddMetric(Grid grid, int row, int column, string labelKey)
    {
        var label = new TextBlock
        {
            Text = UiText.Get(labelKey),
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);

        var value = new TextBlock
        {
            Text = "—",
            Margin = new Thickness(0, 3, 24, 3),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetRow(value, row);
        Grid.SetColumn(value, column + 1);
        grid.Children.Add(value);
        return value;
    }

    private void OnSeriesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            UpdateSubgroupChoices();
        }
        finally
        {
            _refreshing = false;
        }

        Refresh();
    }

    private void UpdateSubgroupChoices()
    {
        int maximum = _seriesCombo.SelectedItem is SpcMeasurementSeries series
            ? Math.Min(10, series.Values.Count / 2)
            : 0;
        _subgroupCombo.ItemsSource = maximum >= 2 ? Enumerable.Range(2, maximum - 1).ToArray() : null;
        _subgroupCombo.SelectedItem = maximum >= 5 ? 5 : maximum >= 2 ? maximum : null;
    }

    private void Refresh()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (_seriesCombo.SelectedItem is not SpcMeasurementSeries series)
            {
                ClearSelection();
                return;
            }

            ProcessCapability capability = ProcessCapability.Calculate(
                series.Values,
                series.LowerSpecificationLimit,
                series.UpperSpecificationLimit);
            _sampleCountText.Text = capability.SampleCount.ToString(CultureInfo.CurrentCulture);
            _meanText.Text = Format(capability.Mean);
            _sigmaWithinText.Text = Format(capability.SigmaWithin);
            _sigmaOverallText.Text = Format(capability.SigmaOverall);
            _cpText.Text = Format(capability.Cp, "0.###");
            _cpkText.Text = Format(capability.Cpk, "0.###");
            _ppText.Text = Format(capability.Pp, "0.###");
            _ppkText.Text = Format(capability.Ppk, "0.###");
            _specLimitsText.Text = FormatSpecificationLimits(series);
            UpdateChart(series);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ClearSelection()
    {
        _sampleCountText.Text = "—";
        _meanText.Text = "—";
        _sigmaWithinText.Text = "—";
        _sigmaOverallText.Text = "—";
        _cpText.Text = "—";
        _cpkText.Text = "—";
        _ppText.Text = "—";
        _ppkText.Text = "—";
        _specLimitsText.Text = "—";
        _violationText.Text = "—";
        _statusText.Text = UiText.Get("ProcessCapabilityNoSeries");
        _chart = null;
        ControlChartRenderer.DrawEmpty(_meanCanvas, UiText.Get("ControlChartInsufficientData"));
        ControlChartRenderer.DrawEmpty(_rangeCanvas, UiText.Get("ControlChartInsufficientData"));
    }

    private void UpdateChart(SpcMeasurementSeries series)
    {
        if (_subgroupCombo.SelectedItem is not int subgroupSize)
        {
            _chart = null;
            _violationText.Text = "—";
            _statusText.Text = UiText.Get("ProcessCapabilityInsufficientChartData");
            ControlChartRenderer.DrawEmpty(_meanCanvas, UiText.Get("ControlChartInsufficientData"));
            ControlChartRenderer.DrawEmpty(_rangeCanvas, UiText.Get("ControlChartInsufficientData"));
            return;
        }

        try
        {
            XbarRangeControlChart chart = XbarRangeControlChart.Calculate(series.Values, subgroupSize);
            _chart = chart;
            _violationText.Text = chart.HasViolations
                ? UiText.Format("ProcessCapabilityViolationCount", chart.Violations.Count)
                : UiText.Get("ProcessCapabilityNoViolations");
            _statusText.Text = UiText.Format(
                "ProcessCapabilityChartSummary",
                chart.SubgroupCount,
                subgroupSize,
                series.Values.Count % subgroupSize);
            DrawCharts();
        }
        catch (ArgumentException)
        {
            _chart = null;
            _violationText.Text = "—";
            _statusText.Text = UiText.Get("ProcessCapabilityInsufficientChartData");
            ControlChartRenderer.DrawEmpty(_meanCanvas, UiText.Get("ControlChartInsufficientData"));
            ControlChartRenderer.DrawEmpty(_rangeCanvas, UiText.Get("ControlChartInsufficientData"));
        }
    }

    private void DrawCharts()
    {
        if (_chart is not { } chart)
        {
            return;
        }

        ControlChartRenderer.Draw(_meanCanvas, chart, SpcControlChartKind.Mean, UiText.Format("ControlChartMeanTitle", chart.SubgroupSize));
        ControlChartRenderer.Draw(_rangeCanvas, chart, SpcControlChartKind.Range, UiText.Format("ControlChartRangeTitle", chart.SubgroupSize));
    }

    private void RedrawCharts()
    {
        if (_refreshing || _disposed)
        {
            return;
        }

        if (_chart is null)
        {
            ControlChartRenderer.DrawEmpty(_meanCanvas, UiText.Get("ControlChartInsufficientData"));
            ControlChartRenderer.DrawEmpty(_rangeCanvas, UiText.Get("ControlChartInsufficientData"));
            return;
        }

        DrawCharts();
    }

    private static string FormatSpecificationLimits(SpcMeasurementSeries series)
    {
        if (!series.HasSpecificationLimits)
        {
            return UiText.Get("ProcessCapabilityNoLimits");
        }

        string suffix = string.IsNullOrWhiteSpace(series.Unit) ? string.Empty : " " + series.Unit;
        if (series.LowerSpecificationLimit is { } lower && series.UpperSpecificationLimit is { } upper)
        {
            return $"{Format(lower)} – {Format(upper)}{suffix}";
        }

        return series.UpperSpecificationLimit is { } onlyUpper
            ? $"≤ {Format(onlyUpper)}{suffix}"
            : $"≥ {Format(series.LowerSpecificationLimit)}{suffix}";
    }

    private static string Format(double value) => Format(value, "0.####");

    private static string Format(double? value, string format = "0.####") =>
        value is { } number && double.IsFinite(number)
            ? number.ToString(format, CultureInfo.CurrentCulture)
            : "—";
}
