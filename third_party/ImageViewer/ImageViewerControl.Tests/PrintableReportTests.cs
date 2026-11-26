using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Xps.Packaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Dialogs;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests;

public class PrintableReportTests
{
    [Fact]
    public void DocumentCarriesVerdictCalibrationItemsAndSpcSection()
    {
        WpfTestRunner.Run(() =>
        {
            MeasurementReport report = BuildReport();
            var rows = new[]
            {
                new SpcReportRow("长度", "mm", 3, "5.02", "0.01", "0.02", "1.5", "1.4", "1.3", "1.2", "[4.95, 5.05] mm")
            };

            FlowDocument document = MeasurementReportDocumentBuilder.Build(report, rows);
            string text = new TextRange(document.ContentStart, document.ContentEnd).Text;

            Assert.Equal(MeasurementReportDocumentBuilder.A4PageWidth, document.PageWidth);
            Assert.Equal(MeasurementReportDocumentBuilder.A4PageHeight, document.PageHeight);
            Assert.Contains("整体判定", text);
            // 有一项不合格，整体结论必须落到“不合格”，不能只显示计数。
            Assert.Contains("不合格", text);
            Assert.Contains("CERT-9", text);
            Assert.Contains("part-9.png", text);
            Assert.Contains("过程能力 (SPC)", text);
            Assert.Contains("5.05", text);
        });
    }

    [Fact]
    public void DocumentShowsUndistortionFailureFromReportAudit()
    {
        WpfTestRunner.Run(() =>
        {
            var calibration = new CameraCalibration
            {
                PrincipalX = 0,
                PrincipalY = 0,
                NormalizationRadius = 100,
                K1 = -1,
                MeasurementUncertaintyPixels = 0.2
            };
            DistortionAudit audit = CameraCalibrationAudit.Audit(calibration, [new PointD(100, 0)]);
            Assert.True(audit.HasNonConvergedPoints);
            MeasurementReport report = MeasurementReportBuilder.Build(
                [new MeasurementReportItem("长度", "长度", "mm", null, 5, 0.05, 0.05, "Passed", "NotEvaluable")],
                0.05,
                "mm",
                calibration,
                distortionAudit: audit);

            FlowDocument document = MeasurementReportDocumentBuilder.Build(report, []);
            string text = new TextRange(document.ContentStart, document.ContentEnd).Text;

            Assert.Contains("去畸变反解未收敛", text);
            Assert.Contains("相关尺寸和公差判定不可评估", text);
        });
    }

    [Fact]
    public void LongReportPaginatesAcrossMultiplePages()
    {
        WpfTestRunner.Run(() =>
        {
            var items = new List<MeasurementReportItem>();
            for (int index = 0; index < 160; index++)
            {
                items.Add(new MeasurementReportItem(
                    $"特征-{index:000}",
                    "长度",
                    "mm",
                    5 + (index % 3) * 0.01,
                    5,
                    0.05,
                    0.05,
                    "Passed",
                    "Passed"));
            }

            MeasurementReport report = MeasurementReportBuilder.Build(items, 0.05, "mm", calibration: null);
            FlowDocument document = MeasurementReportDocumentBuilder.Build(report, []);

            var paginator = (IDocumentPaginatorSource)document;
            paginator.DocumentPaginator.ComputePageCount();

            // 打印/预览的可信度来自“确实会翻页”，而不是一次性把内容挤出纸外。
            Assert.True(paginator.DocumentPaginator.PageCount > 1);
        });
    }

    [Fact]
    public void ExportToXpsWritesAPackageThatCanBeReopened()
    {
        WpfTestRunner.Run(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"image-viewer-report-{Guid.NewGuid():N}.xps");
            try
            {
                FlowDocument document = MeasurementReportDocumentBuilder.Build(BuildReport(), []);
                PrintableReportExporter.ExportToXps(document, path);

                Assert.True(File.Exists(path));
                Assert.True(new FileInfo(path).Length > 0);

                using var package = new XpsDocument(path, FileAccess.Read);
                FixedDocumentSequence sequence = package.GetFixedDocumentSequence();
                Assert.NotNull(sequence);
                Assert.True(sequence.DocumentPaginator.PageCount >= 1);
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void DialogPreviewsA4DocumentAndExportsXpsToChosenPath()
    {
        WpfTestRunner.Run(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"image-viewer-report-{Guid.NewGuid():N}.xps");
            try
            {
                var dialog = new PrintableReportDialog(BuildReport(), [], () => path);

                Assert.Equal(MeasurementReportDocumentBuilder.A4PageWidth, dialog.ReportDocument.PageWidth);
                Assert.True(dialog.ExportXps(path));
                Assert.True(File.Exists(path));
                Assert.Contains(Path.GetFileName(path), dialog.StatusMessage);
                dialog.Close();
            }
            finally
            {
                File.Delete(path);
            }
        });
    }

    [Fact]
    public void DialogDoesNothingWhenNoExportPathWasChosen()
    {
        WpfTestRunner.Run(() =>
        {
            var dialog = new PrintableReportDialog(BuildReport(), []);

            Assert.False(dialog.ExportXps(null));
            Assert.Equal(string.Empty, dialog.StatusMessage);
            dialog.Close();
        });
    }

    private static MeasurementReport BuildReport()
    {
        var items = new List<MeasurementReportItem>
        {
            new("长度", "长度", "mm", 5.02, 5, 0.05, 0.05, "Passed", "Passed"),
            new("宽度", "宽度", "mm", 3.2, 3, 0.05, 0.05, "Passed", "Failed")
        };
        var calibration = new CameraCalibration
        {
            NormalizationRadius = 100,
            K1 = 0.01,
            MeasurementUncertaintyPixels = 0.2,
            CalibrationId = "CERT-9"
        };

        return MeasurementReportBuilder.Build(
            items,
            0.05,
            "mm",
            calibration,
            sourceName: "part-9.png",
            sourceSha256: "abcdefabcdefabcdef0123456789");
    }
}
