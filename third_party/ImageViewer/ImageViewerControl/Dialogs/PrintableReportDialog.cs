using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ImageViewer.Core.Analysis;
using ImageViewer.Localization;
using ImageViewer.Services;

namespace ImageViewer.Dialogs;

/// <summary>
/// 可打印测量报告窗口：预览、打印、导出 XPS。
/// Chinese: 预览用 A4 版式；真正打印时按打印机可打印区域重建文档，避免驱动边距造成二次裁切。
/// 内容来自 <see cref="MeasurementReport"/>，与 Markdown 报告共用同一份模型与词条。
/// English: Preview at A4; printing rebuilds the document at the printable area so driver margins
/// cannot clip content.
/// </summary>
internal sealed class PrintableReportDialog : Window
{
    private readonly MeasurementReport _report;
    private readonly IReadOnlyList<SpcReportRow> _spcRows;
    private readonly Func<string?>? _chooseXpsPath;
    private readonly FlowDocumentReader _reader;
    private readonly TextBlock _statusText;
    private bool _disposed;

    public PrintableReportDialog(
        MeasurementReport report,
        IReadOnlyList<SpcReportRow> spcRows,
        Func<string?>? chooseXpsPath = null)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _spcRows = spcRows ?? throw new ArgumentNullException(nameof(spcRows));
        _chooseXpsPath = chooseXpsPath;

        Title = UiText.Get("PrintableReportDialogTitle");
        Width = 1000;
        Height = 820;
        MinWidth = 720;
        MinHeight = 560;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemColors.ControlBrush;
        AutomationProperties.SetName(this, Title);

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new TextBlock
        {
            Text = UiText.Get("PrintableReportDialogHeading"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        heading.Children.Add(new TextBlock
        {
            Text = UiText.Get("PrintableReportDialogDescription"),
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush
        });
        root.Children.Add(heading);

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 6)
        };
        toolbar.Children.Add(CreateButton("PrintableReportPrintButton", OnPrintClick));
        toolbar.Children.Add(CreateButton("PrintableReportExportXpsButton", OnExportXpsClick));
        Grid.SetRow(toolbar, 1);
        root.Children.Add(toolbar);

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = SystemColors.GrayTextBrush,
            MinHeight = 18,
            Margin = new Thickness(0, 0, 0, 6)
        };
        AutomationProperties.SetName(_statusText, UiText.Get("PrintableReportDialogTitle"));
        Grid.SetRow(_statusText, 2);
        root.Children.Add(_statusText);

        _reader = new FlowDocumentReader
        {
            Document = MeasurementReportDocumentBuilder.Build(report, spcRows),
            ViewingMode = FlowDocumentReaderViewingMode.Page,
            IsPrintEnabled = false,
            IsFindEnabled = true,
            Background = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        };
        AutomationProperties.SetName(_reader, UiText.Get("PrintableReportDialogTitle"));
        Grid.SetRow(_reader, 3);
        root.Children.Add(_reader);

        var closeButton = new Button
        {
            Content = UiText.Get("DialogButtonClose"),
            IsCancel = true,
            IsDefault = true,
            MinWidth = 88,
            Padding = new Thickness(12, 4, 12, 4),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) => Close();
        AutomationProperties.SetName(closeButton, UiText.Get("DialogButtonClose"));
        Grid.SetRow(closeButton, 4);
        root.Children.Add(closeButton);

        Content = root;
    }

    /// <summary>预览文档（测试与宿主可读取结构）。</summary>
    internal FlowDocument ReportDocument => _reader.Document;

    /// <summary>最近一次操作的结果文案。</summary>
    internal string StatusMessage => _statusText.Text;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Close();
    }

    /// <summary>导出 A4 版式 XPS；路径为空时静默返回。</summary>
    internal bool ExportXps(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            FlowDocument document = MeasurementReportDocumentBuilder.Build(_report, _spcRows);
            PrintableReportExporter.ExportToXps(document, filePath);
            SetStatus(UiText.Format("PrintableReportExportXpsSuccess", Path.GetFileName(filePath)), success: true);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(UiText.Format("PrintableReportExportXpsFailed", ex.Message), success: false);
            return false;
        }
    }

    private void OnExportXpsClick(object sender, RoutedEventArgs e) => ExportXps(_chooseXpsPath?.Invoke());

    private void OnPrintClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var printDialog = new PrintDialog();
            if (printDialog.ShowDialog() != true)
            {
                SetStatus(UiText.Get("PrintableReportPrintCancelled"), success: false);
                return;
            }

            double width = printDialog.PrintableAreaWidth;
            double height = printDialog.PrintableAreaHeight;
            if (!double.IsFinite(width) || width <= 0)
            {
                width = MeasurementReportDocumentBuilder.A4PageWidth;
            }

            if (!double.IsFinite(height) || height <= 0)
            {
                height = MeasurementReportDocumentBuilder.A4PageHeight;
            }

            FlowDocument document = MeasurementReportDocumentBuilder.Build(
                _report,
                _spcRows,
                width,
                height,
                pagePadding: 36);
            printDialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, _report.Title);
            SetStatus(UiText.Get("PrintableReportPrintSuccess"), success: true);
        }
        catch (Exception ex)
        {
            SetStatus(UiText.Format("PrintableReportPrintFailed", ex.Message), success: false);
        }
    }

    private static Button CreateButton(string labelKey, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = UiText.Get(labelKey),
            MinWidth = 108,
            Padding = new Thickness(12, 4, 12, 4),
            Margin = new Thickness(0, 0, 8, 0)
        };
        button.Click += handler;
        AutomationProperties.SetName(button, UiText.Get(labelKey));
        return button;
    }

    private void SetStatus(string message, bool success)
    {
        _statusText.Text = message;
        _statusText.Foreground = success ? Brushes.DarkGreen : Brushes.DarkRed;
    }
}
