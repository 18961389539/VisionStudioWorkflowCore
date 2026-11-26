using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;

namespace ImageViewer.Services;

/// <summary>
/// 把结构化测量报告排版成 A4 的 <see cref="FlowDocument"/>。
/// Chinese: 内容和 Markdown 报告同源（<see cref="MeasurementReportText"/> / <see cref="SpcReportRow"/>），
/// 这里只负责“纸面上怎么排”，不重新计算任何数值。
/// English: Layout only — every number and label comes from the shared report model.
/// </summary>
internal static class MeasurementReportDocumentBuilder
{
    /// <summary>A4 宽度（DIP，210 mm）。</summary>
    public const double A4PageWidth = 793.92;

    /// <summary>A4 高度（DIP，297 mm）。</summary>
    public const double A4PageHeight = 1122.52;

    /// <summary>默认页边距（DIP）。</summary>
    public const double DefaultPagePadding = 48;

    private const string FooterDisclaimer = "本报告由 ImageViewer 自动生成；判定基于 2D 图像量测，不等同于三坐标测量机的基准公差结论。";

    /// <summary>
    /// 构造可打印文档。
    /// Chinese: 打印时传入打印机可打印区域尺寸，预览时传 A4；这样打印不会被驱动边距二次裁切。
    /// </summary>
    public static FlowDocument Build(
        MeasurementReport report,
        IReadOnlyList<SpcReportRow> spcRows,
        double pageWidth = A4PageWidth,
        double pageHeight = A4PageHeight,
        double pagePadding = DefaultPagePadding)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(spcRows);

        var document = new FlowDocument
        {
            PageWidth = pageWidth,
            PageHeight = pageHeight,
            PagePadding = new Thickness(pagePadding),
            ColumnWidth = double.PositiveInfinity,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = Brushes.Black,
            Background = Brushes.White
        };

        document.Blocks.Add(BuildTitle(report));
        document.Blocks.Add(BuildMetadata(report));
        BuildVerdictSection(document, report);
        BuildPrecisionSection(document, report);
        BuildReadinessSection(document, report);
        BuildItemsSection(document, report, pageWidth - (pagePadding * 2));
        BuildSpcSection(document, spcRows, pageWidth - (pagePadding * 2));
        document.Blocks.Add(new Paragraph(new Run(FooterDisclaimer))
        {
            FontSize = 9,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 16, 0, 0),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0.5, 0, 0),
            Padding = new Thickness(0, 6, 0, 0)
        });

        return document;
    }

    private static Paragraph BuildTitle(MeasurementReport report) => new(new Run(report.Title))
    {
        FontSize = 26,
        FontWeight = FontWeights.Bold,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static Paragraph BuildMetadata(MeasurementReport report)
    {
        var paragraph = new Paragraph
        {
            FontSize = 11,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 4)
        };

        paragraph.Inlines.Add(new Run("生成时间: " + report.GeneratedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)));
        paragraph.Inlines.Add(new LineBreak());
        if (!string.IsNullOrWhiteSpace(report.SourceName))
        {
            paragraph.Inlines.Add(new Run("图像来源: " + report.SourceName
                + (string.IsNullOrWhiteSpace(report.SourceSha256)
                    ? string.Empty
                    : "（SHA256 " + MeasurementReportText.ShortenHash(report.SourceSha256) + "）")));
            paragraph.Inlines.Add(new LineBreak());
        }

        paragraph.Inlines.Add(new Run("标定比例: " + report.Scale.Describe()));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run("标定身份: " + MeasurementReportText.DescribeCalibrationIdentity(report.Calibration)));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run("测量能力: " + MeasurementReportText.DescribeMeasurementCapability(report.Readiness)));
        return paragraph;
    }

    private static void BuildVerdictSection(FlowDocument document, MeasurementReport report)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 14, 0, 2),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 4)
        };
        paragraph.Inlines.Add(new Run("整体判定: ") { FontSize = 14, FontWeight = FontWeights.SemiBold });
        paragraph.Inlines.Add(new Run(MeasurementReportText.DescribeVerdict(report.OverallVerdict))
        {
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = report.HasFailure ? Brushes.DarkRed : Brushes.Black
        });
        document.Blocks.Add(paragraph);

        document.Blocks.Add(new Paragraph(CreateRun(string.Create(
            CultureInfo.InvariantCulture,
            $"测量项: {report.ItemCount}（通过 {report.PassedCount} / 不合格 {report.FailedCount} / 不可评估 {report.NotEvaluableCount}）")))
        {
            Margin = new Thickness(0, 4, 0, 0)
        });
    }

    private static void BuildPrecisionSection(FlowDocument document, MeasurementReport report)
    {
        CameraCalibration? calibration = report.Calibration;
        double? uncertaintyPixels = calibration is { IsValid: true } && calibration.MeasurementUncertaintyPixels > 0
            ? calibration.MeasurementUncertaintyPixels
            : null;
        double? reprojectionRms = calibration is { } c && c.ReprojectionErrorRms > 0 ? c.ReprojectionErrorRms : null;
        DistortionAudit? audit = report.DistortionAudit;
        double? pointUncertaintyPixels = audit is { HasPoints: true } && audit.MaximumUncertaintyPixels > 0
            ? audit.MaximumUncertaintyPixels
            : null;
        if (!uncertaintyPixels.HasValue && !reprojectionRms.HasValue && !pointUncertaintyPixels.HasValue &&
            audit is not { HasNonConvergedPoints: true })
        {
            return;
        }

        AddHeading(document, "标定能力");
        var list = CreateList();
        if (reprojectionRms.HasValue)
        {
            list.ListItems.Add(new ListItem(new Paragraph(CreateRun(
                "重投影 RMS: " + reprojectionRms.Value.ToString("G4", CultureInfo.InvariantCulture) + " px"))));
        }

        if (uncertaintyPixels.HasValue)
        {
            string text = "测量不确定度: " + uncertaintyPixels.Value.ToString("G4", CultureInfo.InvariantCulture) + " px";
            if (report.Scale.IsCalibrated)
            {
                text += "（≈ " + (uncertaintyPixels.Value * report.Scale.PixelSize).ToString("G4", CultureInfo.InvariantCulture)
                    + " " + report.Scale.Unit + ", 1σ）";
            }

            list.ListItems.Add(new ListItem(new Paragraph(CreateRun(text))));
        }

        if (audit is { HasPoints: true })
        {
            if (pointUncertaintyPixels.HasValue &&
                (!uncertaintyPixels.HasValue || pointUncertaintyPixels.Value > uncertaintyPixels.Value))
            {
                string text = "逐点去畸变不确定度（最大）: "
                    + pointUncertaintyPixels.Value.ToString("G4", CultureInfo.InvariantCulture) + " px";
                if (report.Scale.IsCalibrated)
                {
                    text += "（≈ " + (pointUncertaintyPixels.Value * report.Scale.PixelSize).ToString("G4", CultureInfo.InvariantCulture)
                        + " " + report.Scale.Unit + ", 1σ）";
                }

                list.ListItems.Add(new ListItem(new Paragraph(CreateRun(text))));
            }

            if (audit.HasNonConvergedPoints)
            {
                list.ListItems.Add(new ListItem(new Paragraph(CreateRun(
                    "去畸变反解未收敛: " + audit.NonConvergedPointCount + "/" + audit.PointCount + " 个测量点，最大残差 "
                    + audit.MaximumResidualPixels.ToString("G4", CultureInfo.InvariantCulture)
                    + " px；相关尺寸和公差判定不可评估。"))));
            }
        }

        list.ListItems.Add(new ListItem(new Paragraph(CreateRun(
            "单次测量的综合不确定度 = 重复性(未评估) ⊕ 标定不确定度；未做重复测量时取标定不确定度与逐点去畸变不确定度中的较大者。"))));
        document.Blocks.Add(list);
    }

    private static void BuildReadinessSection(FlowDocument document, MeasurementReport report)
    {
        AddHeading(document, "前置检查");
        if (report.Readiness.Checks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(CreateRun("全部通过（无标定/配置缺口）")));
            return;
        }

        var list = CreateList();
        foreach (MeasurementReadinessCheck check in report.Readiness.Checks)
        {
            var paragraph = new Paragraph();
            paragraph.Inlines.Add(new Run("[" + MeasurementReportText.DescribeLevel(check.Level) + "] ")
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = check.Level == MeasurementReadinessLevel.Blocked
                    ? Brushes.DarkRed
                    : check.Level == MeasurementReadinessLevel.Warning
                        ? Brushes.DarkOrange
                        : Brushes.Black
            });
            paragraph.Inlines.Add(new Run(check.Message));
            list.ListItems.Add(new ListItem(paragraph));
        }

        document.Blocks.Add(list);
    }

    private static void BuildItemsSection(FlowDocument document, MeasurementReport report, double availableWidth)
    {
        AddHeading(document, "测量明细");
        if (report.Items.Count == 0)
        {
            document.Blocks.Add(new Paragraph(CreateRun("当前没有测量项。")));
            return;
        }

        string[] headers = ["项目", "量", "测得值", "标称值", "公差", "检测", "判定"];
        double[] weights = [2.0, 1.6, 1.2, 1.2, 1.5, 1.1, 1.1];
        var table = CreateTable(availableWidth, weights);
        var header = CreateRow();
        foreach (string text in headers)
        {
            header.Cells.Add(CreateCell(text, header: true));
        }

        table.RowGroups[0].Rows.Add(header);

        foreach (MeasurementReportItem item in report.Items)
        {
            TableRow row = CreateRow();
            row.Cells.Add(CreateCell(item.Label));
            row.Cells.Add(CreateCell(item.Quantity));
            row.Cells.Add(CreateCell(MeasurementReportText.FormatValue(item.MeasuredValue, item.Unit)));
            row.Cells.Add(CreateCell(MeasurementReportText.FormatValue(item.NominalValue, item.Unit)));
            row.Cells.Add(CreateCell(MeasurementReportText.FormatTolerance(item)));
            row.Cells.Add(CreateCell(MeasurementReportText.DescribeDetectionStatus(item.DetectionStatus)));
            TableCell verdictCell = CreateCell(MeasurementReportText.DescribeVerdict(item.SpecificationStatus));
            if (string.Equals(item.SpecificationStatus, "Failed", StringComparison.Ordinal))
            {
                verdictCell.Foreground = Brushes.DarkRed;
                verdictCell.FontWeight = FontWeights.SemiBold;
            }

            row.Cells.Add(verdictCell);
            table.RowGroups[0].Rows.Add(row);
        }

        document.Blocks.Add(table);
    }

    private static void BuildSpcSection(FlowDocument document, IReadOnlyList<SpcReportRow> rows, double availableWidth)
    {
        if (rows.Count == 0)
        {
            return;
        }

        AddHeading(document, "过程能力 (SPC)");
        string[] headers = ["项目", "单位", "N", "均值", "组内 σ", "整体 σ", "Cp", "Cpk", "Pp", "Ppk", "规范限"];
        double[] weights = [1.8, 0.9, 0.6, 1.1, 1.0, 1.0, 0.8, 0.8, 0.8, 0.8, 1.9];
        var table = CreateTable(availableWidth, weights);
        var header = CreateRow();
        foreach (string text in headers)
        {
            header.Cells.Add(CreateCell(text, header: true, fontSize: 9));
        }

        table.RowGroups[0].Rows.Add(header);

        foreach (SpcReportRow item in rows)
        {
            TableRow row = CreateRow();
            row.Cells.Add(CreateCell(item.Label, fontSize: 9));
            row.Cells.Add(CreateCell(item.Unit, fontSize: 9));
            row.Cells.Add(CreateCell(item.SampleCount.ToString(CultureInfo.InvariantCulture), fontSize: 9));
            row.Cells.Add(CreateCell(item.Mean, fontSize: 9));
            row.Cells.Add(CreateCell(item.SigmaWithin, fontSize: 9));
            row.Cells.Add(CreateCell(item.SigmaOverall, fontSize: 9));
            row.Cells.Add(CreateCell(item.Cp, fontSize: 9));
            row.Cells.Add(CreateCell(item.Cpk, fontSize: 9));
            row.Cells.Add(CreateCell(item.Pp, fontSize: 9));
            row.Cells.Add(CreateCell(item.Ppk, fontSize: 9));
            row.Cells.Add(CreateCell(item.SpecificationLimits, fontSize: 9));
            table.RowGroups[0].Rows.Add(row);
        }

        document.Blocks.Add(table);
        document.Blocks.Add(new Paragraph(CreateRun(
            "组内 σ 由移动极差估计（d2=1.128），整体 σ 为样本标准差；Cp/Pp 需要双侧公差，Cpk/Ppk 在单侧公差时也可计算；σ 为零时指数无定义，记为 n/a。"))
        {
            FontSize = 9,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 0)
        });
    }

    private static Table CreateTable(double availableWidth, IReadOnlyList<double> weights)
    {
        double totalWeight = 0;
        foreach (double weight in weights)
        {
            totalWeight += weight;
        }

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 0)
        };

        foreach (double weight in weights)
        {
            table.Columns.Add(new TableColumn { Width = new GridLength(availableWidth * weight / totalWeight) });
        }

        table.RowGroups.Add(new TableRowGroup());
        return table;
    }

    private static TableRow CreateRow() => new();

    private static TableCell CreateCell(string text, bool header = false, double? fontSize = null)
    {
        var cell = new TableCell
        {
            Padding = new Thickness(4, 3, 4, 3),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0.5),
            Background = header ? Brushes.Gainsboro : null,
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal
        };
        cell.Blocks.Add(new Paragraph(new Run(text) { FontSize = fontSize ?? 10.5 })
        {
            Margin = new Thickness(0),
            TextAlignment = TextAlignment.Left
        });
        return cell;
    }

    private static void AddHeading(FlowDocument document, string text)
    {
        document.Blocks.Add(new Paragraph(new Run(text))
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 6),
            BorderBrush = Brushes.LightGray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 0, 0, 3),
            KeepWithNext = true
        });
    }

    private static List CreateList() => new()
    {
        MarkerStyle = TextMarkerStyle.Disc,
        Margin = new Thickness(18, 0, 0, 0),
        Padding = new Thickness(0)
    };

    private static Run CreateRun(string text) => new(text) { FontSize = 11 };
}
