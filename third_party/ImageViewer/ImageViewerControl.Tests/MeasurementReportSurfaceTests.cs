using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Localization;
using ImageViewer.Models;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    /// <summary>
    /// 离线工作流（选择配方 → 标定检查 → 测量 → 复核 → 出报告）的端到端守卫测试。
    /// Chinese: 这些断言锁住"报告能不能作为交付物"的三件事：可信度声明、单位正确、判定可追溯。
    /// English: Guards that the report is a defensible deliverable — confidence stated, units right,
    /// verdicts traceable.
    /// </summary>
    public class MeasurementReportSurfaceTests
    {
        private static LineMeasureRoi CreateMeasuredRoi(string label, double pixelLength, double pixelSize, MeasurementTolerance? tolerance = null)
        {
            return new LineMeasureRoi
            {
                Label = label,
                P1 = new PointD(0, 0),
                P2 = new PointD(pixelLength, 0),
                Tolerance = tolerance
            };
        }

        [Fact]
        public void ReportCarriesCalibrationIdentityAndPrecisionIntoTheDeliverable()
        {
            var calibration = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                MeasurementUncertaintyPixels = 0.2,
                ReprojectionErrorRms = 0.08,
                CalibrationId = "CERT-2026-01",
                CalibratedAtUtc = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero)
            };
            var roi = CreateMeasuredRoi("孔径", pixelLength: 200, pixelSize: 0.05,
                new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.2, ToleranceMinus = 0.2 });

            string report = RoiAnalysisExportService.BuildReport(
                [roi],
                bitmap: null,
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: calibration,
                sourceName: "part-001.png");

            // 一份没有可追溯标定的尺寸报告在计量上是无效的。
            Assert.Contains("# 测量报告 – part-001.png", report);
            Assert.Contains("CERT-2026-01", report);
            Assert.Contains("2026-05-06 07:08 UTC", report);
            Assert.Contains("1 px = 0.05 mm", report);
            Assert.Contains("重投影 RMS: 0.08 px", report);
            // 0.2 px × 0.05 mm/px = 0.01 mm
            Assert.Contains("0.01 mm", report);
            // 测量值 200 px × 0.05 = 10 mm，正好等于标称 10 mm。
            Assert.Contains("10 mm", report);
        }

        [Fact]
        public void ReportStatesWhetherAVerdictWasPossible()
        {
            var roi = CreateMeasuredRoi("无公差项", pixelLength: 100, pixelSize: 0.05);

            string report = RoiAnalysisExportService.BuildReport(
                [roi],
                bitmap: null,
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: null);

            // 能算值不等于能判定；报告必须把这两件事分开说。
            Assert.Contains("只能给出物理测量值，无法给出尺寸判定", report);
            Assert.Contains("无可判定项", report);
            Assert.Contains("没有任何 ROI 配置公差", report);
        }

        [Fact]
        public void ReportNamesTheQuantityForAngularRois()
        {
            var parallelism = new ParallelismMeasureRoi
            {
                Label = "上表面平行度",
                Tolerance = new MeasurementTolerance { Nominal = 0.5, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
            };

            string report = RoiAnalysisExportService.BuildReport(
                [parallelism],
                bitmap: null,
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: null);

            // 「0.05」到底是毫米还是度必须写清楚，否则报告不可读。
            // 该 ROI 未检测（无测得值），因此行内是 "—"，但量名列必须写对。
            Assert.Contains("平行度角度偏差", report);
            Assert.Contains("| 上表面平行度 |", report);
            Assert.Contains("不可评估", report);
        }

        [Fact]
        public void UncalibratedReportSaysBlockedRatherThanShowingFakeMillimetres()
        {
            var roi = CreateMeasuredRoi("孔径", pixelLength: 100, pixelSize: 0.05,
                new MeasurementTolerance { Nominal = 5, TolerancePlus = 0.1, ToleranceMinus = 0.1 });

            string report = RoiAnalysisExportService.BuildReport(
                [roi],
                bitmap: null,
                pixelSize: 0,
                physicalUnit: null,
                calibration: null);

            Assert.Contains("已阻断", report);
            Assert.Contains("未标定（像素单位）", report);
            Assert.Contains("未设置像素标定比例", report);
        }

        [Fact]
        public void ReadinessFormatterLeadsWithWhetherWorkCanContinue()
        {
            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0,
                physicalUnit: null,
                calibration: null,
                roiCount: 1,
                tolerancedRoiCount: 1);

            string text = MeasurementWorkflowReadinessFormatter.Format(readiness, pixelSize: 0, physicalUnit: null);

            Assert.Contains(UiText.Get("CalibrationCheckCapabilityBlocked"), text);
            Assert.Contains(UiText.Get("CalibrationCheckScaleMissing"), text);
            Assert.Contains("[阻断]", text);
            // 检查结论必须提醒"亚像素 ≠ 准"。
            Assert.Contains(UiText.Get("CalibrationCheckFooter"), text);
        }

        [Fact]
        public void ReadinessFormatterListsBlockersBeforeWarnings()
        {
            var calibration = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                K3 = 0.001
            };

            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: calibration,
                roiCount: 1,
                tolerancedRoiCount: 0);

            string text = MeasurementWorkflowReadinessFormatter.Format(readiness, pixelSize: 0.05, physicalUnit: "mm");

            Assert.Contains("[警告]", text);
            // 只有警告没有阻断，所以"阻断"标签不应出现。
            Assert.DoesNotContain("[阻断]", text);
            Assert.Contains(UiText.Get("CalibrationCheckCapabilityValuesOnly"), text);
        }

        [Fact]
        public void ReadinessFormatterSaysNoIssuesWhenFullyConfigured()
        {
            var calibration = new CameraCalibration
            {
                NormalizationRadius = 100,
                K1 = 0.01,
                MeasurementUncertaintyPixels = 0.2,
                CalibrationId = "CERT-Z"
            };

            MeasurementWorkflowReadiness readiness = MeasurementWorkflowReadinessEvaluator.Evaluate(
                pixelSize: 0.05,
                physicalUnit: "mm",
                calibration: calibration,
                roiCount: 2,
                tolerancedRoiCount: 2,
                unmeasuredTolerancedRoiCount: 0,
                hasKnownReference: true);

            string text = MeasurementWorkflowReadinessFormatter.Format(readiness, pixelSize: 0.05, physicalUnit: "mm");

            Assert.Contains(UiText.Get("CalibrationCheckNoIssues"), text);
            Assert.Contains(UiText.Get("CalibrationCheckCapabilityVerdict"), text);
            Assert.Contains("1 px = 0.05 mm", text);
        }

        [Fact]
        public void ReportAppendsProcessCapabilityBeforeTheFooterForRepeatedQuantities()
        {
            var tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.5, ToleranceMinus = 0.5 };
            var rois = new RoiBase[]
            {
                CreateMeasuredRoi("孔1", pixelLength: 9, pixelSize: 1, tolerance),
                CreateMeasuredRoi("孔2", pixelLength: 10, pixelSize: 1, tolerance),
                CreateMeasuredRoi("孔3", pixelLength: 11, pixelSize: 1, tolerance)
            };

            string report = RoiAnalysisExportService.BuildReport(
                rois,
                bitmap: null,
                pixelSize: 1,
                physicalUnit: "mm",
                calibration: null);

            Assert.Contains("## 过程能力 (SPC)", report);
            Assert.Contains("| 长度 | mm | 3 |", report);
            int spcIndex = report.IndexOf("## 过程能力 (SPC)", StringComparison.Ordinal);
            int footerIndex = report.IndexOf("本报告由 ImageViewer 自动生成", StringComparison.Ordinal);
            Assert.True(spcIndex >= 0 && spcIndex < footerIndex, "SPC 章节必须位于报告页脚之前。");
        }

        [Fact]
        public async Task ExportWritesReportAlongsideCsvAndMetadata()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-report-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string imagePath = Path.Combine(root, "part-777.png");
                SaveBitmap(imagePath, CreateBitmap(24, 16, 120));
                string csvPath = Path.Combine(root, "part-777.csv");

                var roi = new LineMeasureRoi
                {
                    Label = "长度",
                    P1 = new PointD(0, 0),
                    P2 = new PointD(100, 0),
                    Tolerance = new MeasurementTolerance { Nominal = 5, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
                };
                var calibration = new CameraCalibration
                {
                    NormalizationRadius = 100,
                    K1 = 0.01,
                    MeasurementUncertaintyPixels = 0.2,
                    CalibrationId = "CERT-777"
                };

                var context = new RoiAnalysisExportContext(imagePath, PluginRegistry: null);
                await RoiAnalysisExportService.SaveCsvAsync(
                    csvPath,
                    [roi],
                    bitmap: null,
                    pixelSize: 0.05,
                    physicalUnit: "mm",
                    calibration,
                    exportContext: context);

                string reportPath = Path.ChangeExtension(csvPath, ".report.md");
                Assert.True(File.Exists(reportPath), "报告必须与 CSV 一并产出，否则‘出报告’这一步没有交付物。");

                string report = File.ReadAllText(reportPath);
                // 三件套必须互相印证：报告带标定身份、来源文件名与来源哈希。
                Assert.Contains("CERT-777", report);
                Assert.Contains("part-777.png", report);
                Assert.Contains("part-777.png（SHA256 ", report);
                // 其余两份交付物仍在原位，说明报告是"追加"而不是"替换"。
                Assert.True(File.Exists(csvPath));
                string metadataPath = Path.ChangeExtension(csvPath, ".metadata.json");
                Assert.True(File.Exists(metadataPath));
                Assert.Contains("\"processCapability\"", File.ReadAllText(metadataPath), StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static BitmapSource CreateBitmap(int width, int height, byte value)
        {
            int stride = width * 4;
            var pixels = new byte[stride * height];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }

            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }

        private static void SaveBitmap(string path, BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
