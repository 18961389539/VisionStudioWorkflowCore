using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Core.Measurements;
using ImageViewer.Models;
using ImageViewer.Plugins;
using ImageViewer.Services;
using Xunit;

namespace ImageViewerControl.Tests
{
    public class BatchRoiAnalysisExportServiceTests
    {
        [Fact]
        public async Task UndistortionFailureIsReportedAndExcludedFromMeasurementOutputs()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-audit-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string input = Path.Combine(root, "input.png");
                string csvPath = Path.Combine(root, "results.csv");
                SaveBitmap(input, CreateBitmap(220, 220, 80));
                var calibration = new CameraCalibration
                {
                    PrincipalX = 100, PrincipalY = 100, NormalizationRadius = 100, K1 = -1, MeasurementUncertaintyPixels = 0.25
                };
                var roi = new LineMeasureRoi
                {
                    P1 = new PointD(100, 100), P2 = new PointD(200, 100), Label = "unreliable",
                    Tolerance = new MeasurementTolerance { Nominal = 100, TolerancePlus = 10, ToleranceMinus = 10 }
                };

                MeasurementReport report = RoiAnalysisExportService.BuildReportModel([roi], 1, "mm", calibration);
                MeasurementReportItem item = Assert.Single(report.Items);
                Assert.Null(item.MeasuredValue);
                Assert.Equal("NotEvaluable", item.SpecificationStatus);
                Assert.Equal(1, report.DistortionAudit!.NonConvergedPointCount);
                Assert.False(report.Readiness.CanProduceSpecificationVerdict);
                Assert.Contains(report.Readiness.Checks,
                    check => check.Code == MeasurementWorkflowReadinessEvaluator.CodeCalibrationUndistortionNotConverged);
                var valid = new LineMeasureRoi
                {
                    P1 = new PointD(100, 100), P2 = new PointD(110, 100),
                    Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 1, ToleranceMinus = 1 }
                };
                MeasurementReport mixed = RoiAnalysisExportService.BuildReportModel([roi, valid], 1, "mm", calibration);
                Assert.Equal(1, mixed.PassedCount);
                Assert.Equal("NoVerdict", mixed.OverallVerdict);
                Assert.Contains("畸变反解未收敛", RoiAnalysisExportService.BuildReport([roi], null, 1, "mm", calibration));
                Assert.Contains("相关尺寸和公差判定不可评估", RoiInfoService.BuildInfo(
                    roi, null, 1, "mm", RoiPluginRegistry.CreateBuiltIn(), calibration: calibration));
                Assert.DoesNotContain("Length=", RoiAnalysisExportService.BuildCsv([roi], null, 1, "mm", calibration));

                var definition = new AdvancedMeasurementDefinition { Kind = AdvancedMeasurementKind.Midpoint, Sources = [roi.MeasurementId] };
                var advanced = AdvancedMeasurementService.Evaluate(definition, [roi], 1, "mm", calibration);
                Assert.False(advanced.Success);
                Assert.Empty(advanced.Metrics);
                Assert.Contains("did not converge", advanced.Error);

                await BatchRoiAnalysisExportService.ExportAsync(csvPath, [input], [roi], 1, "mm", calibration);
                string csv = await File.ReadAllTextAsync(csvPath);
                Assert.Contains("\"NotEvaluable\",,100", csv);
                Assert.DoesNotContain("Length=", csv);
                using JsonDocument metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(csvPath, ".metadata.json")));
                JsonElement audit = metadata.RootElement.GetProperty("distortionAudit");
                Assert.Equal(1, audit.GetProperty("nonConvergedPointCount").GetInt32());
                Assert.True(audit.GetProperty("maximumUncertaintyPixels").GetDouble() > calibration.MeasurementUncertaintyPixels);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ExportAsync_CombinesRowsAndKeepsPerFileFailures()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string firstPath = Path.Combine(root, "first.png");
                SaveBitmap(firstPath, CreateBitmap(20, 16, 80));
                string missingPath = Path.Combine(root, "missing.png");
                string csvPath = Path.Combine(root, "batch.csv");
                var roi = new CircleRoi { Center = new PointD(8, 7), Radius = 3, Label = "target" };

                BatchRoiAnalysisExportSummary summary = await BatchRoiAnalysisExportService.ExportAsync(
                    csvPath,
                    [firstPath, missingPath],
                    [roi],
                    pixelSize: 1,
                    physicalUnit: null);

                Assert.Equal(2, summary.RequestedFileCount);
                Assert.Equal(1, summary.ProcessedFileCount);
                Assert.Equal(1, summary.FailedFileCount);
                Assert.Equal(2, summary.ExportedRowCount);
                Assert.Equal(1, summary.DecodedFileCount);
                Assert.Equal(1, summary.FullySuccessfulFileCount);
                Assert.Equal(1, summary.InputFailedFileCount);
                Assert.Single(summary.Errors);
                string csv = File.ReadAllText(csvPath);
                string metadataPath = Path.ChangeExtension(csvPath, ".metadata.json");
                Assert.True(File.Exists(metadataPath));
                Assert.Contains("Source,Status,Error,Type,Label", csv);
                Assert.Contains("first.png", csv);
                Assert.Contains("Geometry", csv);
                Assert.Contains("missing.png", csv);
                Assert.Contains("InputMissing", csv);
                Assert.Contains("Failed", csv);
                Assert.Contains("NotEvaluable", csv);

                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
                JsonElement inputRecords = metadata.RootElement.GetProperty("inputRecords");
                Assert.Equal(2, inputRecords.GetArrayLength());
                JsonElement firstRecord = inputRecords.EnumerateArray().First(record =>
                    record.GetProperty("path").GetString()!.EndsWith("first.png", StringComparison.OrdinalIgnoreCase));
                Assert.Equal("Processed", firstRecord.GetProperty("status").GetString());
                Assert.Equal(20, firstRecord.GetProperty("pixelWidth").GetInt32());
                Assert.Equal(16, firstRecord.GetProperty("pixelHeight").GetInt32());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(firstPath))).ToLowerInvariant(),
                    firstRecord.GetProperty("sha256").GetString());
                JsonElement missingRecord = inputRecords.EnumerateArray().First(record =>
                    record.GetProperty("path").GetString()!.EndsWith("missing.png", StringComparison.OrdinalIgnoreCase));
                Assert.Equal("InputMissing", missingRecord.GetProperty("status").GetString());
                Assert.Contains("找不到输入图像。", missingRecord.GetProperty("error").GetString());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ExportAsync_EmitsRowsAndTraceForUnreadableInput()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string invalidPath = Path.Combine(root, "invalid.png");
                File.WriteAllText(invalidPath, "not an image");
                string csvPath = Path.Combine(root, "batch.csv");
                var rois = new RoiBase[]
                {
                    new CircleRoi { Center = new PointD(8, 7), Radius = 3, Label = "target" },
                    new CircleRoi { Center = new PointD(4, 5), Radius = 2, Label = "second" }
                };

                BatchRoiAnalysisExportSummary summary = await BatchRoiAnalysisExportService.ExportAsync(
                    csvPath,
                    [invalidPath],
                    rois,
                    pixelSize: 1,
                    physicalUnit: null);

                Assert.Equal(1, summary.RequestedFileCount);
                Assert.Equal(0, summary.ProcessedFileCount);
                Assert.Equal(1, summary.FailedFileCount);
                Assert.Equal(2, summary.ExportedRowCount);
                Assert.Equal(0, summary.DecodedFileCount);
                Assert.Equal(1, summary.InputFailedFileCount);
                string csv = File.ReadAllText(csvPath);
                Assert.Equal(2, csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length - 1);
                Assert.Equal(2, csv.Split("DecodeFailed", StringSplitOptions.None).Length - 1);

                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(csvPath, ".metadata.json")));
                JsonElement record = Assert.Single(metadata.RootElement.GetProperty("inputRecords").EnumerateArray());
                Assert.Equal("DecodeFailed", record.GetProperty("status").GetString());
                Assert.Equal(2, record.GetProperty("rowCount").GetInt32());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(invalidPath))).ToLowerInvariant(),
                    record.GetProperty("sha256").GetString());
                Assert.True(record.GetProperty("lengthBytes").GetInt64() > 0);
                Assert.False(record.GetProperty("error").GetString() is null or "");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ExportAsync_WhenCanceledSeparatesUnfinishedInputsFromCompletedFailures()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string[] paths =
                [
                    Path.Combine(root, "missing.png"), Path.Combine(root, "completed.png"),
                    Path.Combine(root, "interrupted.png"), Path.Combine(root, "unstarted.png")
                ];
                foreach (string path in paths.Skip(1))
                {
                    SaveBitmap(path, CreateBitmap(20, 16, 80));
                }
                string csvPath = Path.Combine(root, "batch.csv");
                var roi = new CircleRoi { Center = new PointD(8, 7), Radius = 3 };
                using var cancellation = new CancellationTokenSource();
                BatchRoiAnalysisExportProgress? finalProgress = null;
                var progress = new CallbackProgress(update =>
                {
                    finalProgress = update;
                    if (update.Phase == "Preparing" && update.CurrentPath == paths[2])
                    {
                        cancellation.Cancel();
                    }
                });

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BatchRoiAnalysisExportService.ExportAsync(
                    csvPath, paths, [roi], pixelSize: 1, physicalUnit: null, progress: progress,
                    cancellationToken: cancellation.Token));

                Assert.Equal(("Canceled", 2, 2), (finalProgress!.Phase, finalProgress.CompletedFileCount, finalProgress.ExportedRowCount));
                string csv = await File.ReadAllTextAsync(csvPath);
                Assert.Contains("missing.png", csv);
                Assert.Contains("completed.png", csv);
                Assert.DoesNotContain("interrupted.png", csv);
                Assert.DoesNotContain("unstarted.png", csv);
                using JsonDocument metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(csvPath, ".metadata.json")));
                JsonElement rootElement = metadata.RootElement;
                Assert.Equal("Canceled", rootElement.GetProperty("status").GetString());
                Assert.Equal(4, rootElement.GetProperty("inputs").GetArrayLength());
                Assert.Collection(rootElement.GetProperty("inputRecords").EnumerateArray(),
                    record => Assert.Equal("InputMissing", record.GetProperty("status").GetString()),
                    record => Assert.Equal("Processed", record.GetProperty("status").GetString()));
                Assert.Equal(2, rootElement.GetProperty("repeatability").GetProperty("sourceCount").GetInt32());
                JsonElement result = rootElement.GetProperty("result");
                Assert.Equal(1, result.GetProperty("failedFileCount").GetInt32());
                Assert.Equal(1, result.GetProperty("decodedFileCount").GetInt32());
                Assert.Equal(2, result.GetProperty("unfinishedFileCount").GetInt32());
                using JsonDocument summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(csvPath, ".summary.json")));
                Assert.True(summary.RootElement.GetProperty("IsCanceled").GetBoolean());
                Assert.Equal(1, summary.RootElement.GetProperty("FailedFileCount").GetInt32());
                Assert.Equal(2, summary.RootElement.GetProperty("UnfinishedFileCount").GetInt32());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private sealed class CallbackProgress(Action<BatchRoiAnalysisExportProgress> report) : IProgress<BatchRoiAnalysisExportProgress>
        {
            public void Report(BatchRoiAnalysisExportProgress value) => report(value);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ExportAsync_ClearsTemplateDetectionAndCountsRoiFailures(bool includeSuccessfulRoi)
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string imagePath = Path.Combine(root, "blank.png");
                SaveBitmap(imagePath, CreateBitmap(40, 30, 100));
                string csvPath = Path.Combine(root, "batch.csv");
                var roi = new CaliperMeasureRoi
                {
                    P1 = new PointD(10, 15),
                    P2 = new PointD(20, 15),
                    Tolerance = new MeasurementTolerance { Nominal = 10, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
                };
                roi.SetDetectedEdges(new PointD(10, 15), new PointD(10, 20), new PointD(20, 15), new PointD(20, 20));
                roi.ApplyWidthMeasurement(new CaliperWidthMeasurementResult([10], centerDistance: 10));

                RoiBase[] templates = includeSuccessfulRoi
                    ? [roi, new CircleRoi { Center = new PointD(18, 15), Radius = 3 }]
                    : [roi];
                BatchRoiAnalysisExportSummary summary = await BatchRoiAnalysisExportService.ExportAsync(
                    csvPath,
                    [imagePath],
                    templates,
                    pixelSize: 1,
                    physicalUnit: "mm");

                Assert.Equal(0, summary.ProcessedFileCount);
                Assert.Equal(1, summary.FailedFileCount);
                Assert.Equal(1, summary.DecodedFileCount);
                Assert.Equal(0, summary.InputFailedFileCount);
                Assert.Equal(includeSuccessfulRoi ? 1 : 0, summary.PartiallySuccessfulFileCount);
                Assert.Equal(includeSuccessfulRoi ? 0 : 1, summary.AllRoiFailedFileCount);
                string csv = File.ReadAllText(csvPath);
                Assert.Contains("DetectionFailed", csv);
                Assert.Contains("\"NotMeasured\",\"NotEvaluable\",,10", csv, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ExportAsync_KeepsMalformedPathAsPerItemFailure()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string csvPath = Path.Combine(root, "batch.csv");
                string malformedPath = "bad\0path.png";
                BatchRoiAnalysisExportSummary summary = await BatchRoiAnalysisExportService.ExportAsync(
                    csvPath,
                    [malformedPath],
                    [new CircleRoi { Label = "target" }],
                    pixelSize: 1,
                    physicalUnit: "px");

                Assert.Equal(1, summary.RequestedFileCount);
                Assert.Equal(1, summary.FailedFileCount);
                Assert.Contains("InputMissing", File.ReadAllText(csvPath));
                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(csvPath, ".metadata.json")));
                Assert.Equal(malformedPath, Assert.Single(metadata.RootElement.GetProperty("inputRecords").EnumerateArray()).GetProperty("path").GetString());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ExportAsync_PreservesDuplicateRequestedInputsForTraceability()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string imagePath = Path.Combine(root, "same.png");
                SaveBitmap(imagePath, CreateBitmap(8, 8, 120));
                string csvPath = Path.Combine(root, "batch.csv");

                BatchRoiAnalysisExportSummary summary = await BatchRoiAnalysisExportService.ExportAsync(
                    csvPath,
                    [imagePath, imagePath],
                    [new CircleRoi { Label = "target" }],
                    pixelSize: 1,
                    physicalUnit: "px");

                Assert.Equal(2, summary.RequestedFileCount);
                Assert.Equal(2, summary.ProcessedFileCount);
                Assert.Equal(2, summary.ExportedRowCount);
                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(csvPath, ".metadata.json")));
                Assert.Equal(2, metadata.RootElement.GetProperty("inputRecords").GetArrayLength());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ExportAsync_WritesSameWorkpieceRepeatabilityWhenContextIsSpecified()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                // 三个"轮次"就是三张图；几何 ROI 不依赖检测，因此在灰度图上也能逐图产出测量值。
                string[] imagePaths = new string[3];
                for (int i = 0; i < imagePaths.Length; i++)
                {
                    imagePaths[i] = Path.Combine(root, $"round-{i}.png");
                    SaveBitmap(imagePaths[i], CreateBitmap(20, 16, 90));
                }

                string csvPath = Path.Combine(root, "batch.csv");
                // 半径 3 px，pixelSize=1（=3 个单位）。图上无边缘可检测，但几何 ROI 直接出值，
                // 三张图完全一致 ⇒ σ = 0 ⇒ 重复性结论成立且不占公差。
                var roi = new CircleRoi
                {
                    Center = new PointD(8, 7),
                    Radius = 3,
                    Label = "内径",
                    Tolerance = new MeasurementTolerance { Nominal = 3, TolerancePlus = 0.5, ToleranceMinus = 0.5 }
                };

                BatchRoiAnalysisExportSummary summary = await BatchRoiAnalysisExportService.ExportAsync(
                    csvPath,
                    imagePaths,
                    [roi],
                    pixelSize: 1,
                    physicalUnit: "mm",
                    repeatabilityOptions: new RepeatabilityStudyOptions(RepeatabilityStudyContext.SameWorkpiece, "part-001"));

                string repeatabilityPath = Path.ChangeExtension(csvPath, ".repeatability.md");
                Assert.True(File.Exists(repeatabilityPath), "多图批次必须产出重复性报告，否则 GroupRepeatability 永远没有调用方。");
                Assert.Equal(repeatabilityPath, summary.RepeatabilityPath);
                Assert.Equal(3, summary.RepeatabilitySourceCount);
                Assert.Equal(1, summary.RepeatabilityConclusiveItemCount);
                Assert.Equal(0, summary.RepeatabilityToleranceConsumedItemCount);

                string report = File.ReadAllText(repeatabilityPath);
                Assert.Contains("# 重复性研究报告", report);
                Assert.Contains("参与图像数: 3", report);
                Assert.Contains("内径", report);
                // 完全一致的三轮 ⇒ 不该出现"警告：…占满公差带"的告警行。
                Assert.DoesNotContain("**警告", report);
                Assert.Contains("未发现重复性占满公差带的项目", report);
                // 跨图同名项同时构成过程能力样本，随重复性报告一并交付。
                Assert.Contains("## 过程能力 (SPC)", report);

                // 结论也必须进 metadata，供自动化消费（否则只有人读得到）。
                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(csvPath, ".metadata.json")));
                JsonElement repeatability = metadata.RootElement.GetProperty("repeatability");
                Assert.Equal(3, repeatability.GetProperty("sourceCount").GetInt32());
                Assert.Equal(1, repeatability.GetProperty("conclusiveItemCount").GetInt32());
                Assert.True(repeatability.GetProperty("hasConclusiveResult").GetBoolean());
                JsonElement item = Assert.Single(repeatability.GetProperty("items").EnumerateArray());
                Assert.Equal("内径", item.GetProperty("label").GetString());
                Assert.Equal(3, item.GetProperty("sampleCount").GetInt32());

                JsonElement capability = Assert.Single(metadata.RootElement.GetProperty("processCapability").EnumerateArray());
                Assert.Equal(3, capability.GetProperty("sampleCount").GetInt32());
                // 三轮完全一致 ⇒ σ = 0 ⇒ 能力指数无定义，必须是 null 而不是无穷大。
                Assert.Equal(JsonValueKind.Null, capability.GetProperty("cp").ValueKind);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ExportAsync_RepeatabilityIgnoresUndetectedRois()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string[] imagePaths = new string[2];
                for (int i = 0; i < imagePaths.Length; i++)
                {
                    imagePaths[i] = Path.Combine(root, $"round-{i}.png");
                    SaveBitmap(imagePaths[i], CreateBitmap(20, 16, 90));
                }

                string csvPath = Path.Combine(root, "batch.csv");
                // 卡尺 ROI 在纯色图上必然检测失败 ⇒ 没有测得值 ⇒ 绝不能当成"0"进入重复性样本，
                // 否则会把"完全测不到"粉饰成"重复性极好"。
                var caliper = new CaliperMeasureRoi
                {
                    P1 = new PointD(4, 8),
                    P2 = new PointD(12, 8),
                    Label = "边缘",
                    Tolerance = new MeasurementTolerance { Nominal = 8, TolerancePlus = 0.1, ToleranceMinus = 0.1 }
                };

                BatchRoiAnalysisExportSummary summary = await BatchRoiAnalysisExportService.ExportAsync(
                    csvPath,
                    imagePaths,
                    [caliper],
                    pixelSize: 1,
                    physicalUnit: "mm");

                Assert.Equal(0, summary.RepeatabilityConclusiveItemCount);
                Assert.Equal(2, summary.RepeatabilitySourceCount);
                Assert.Equal(0, summary.RepeatabilityToleranceConsumedItemCount);

                string report = File.ReadAllText(Path.ChangeExtension(csvPath, ".repeatability.md"));
                Assert.Contains("无法给出同一工件重复性结论", report);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ExportAsync_DefaultContextReportsSourceVariationAndKeepsTemplateItemsSeparate()
        {
            string root = Path.Combine(Path.GetTempPath(), $"image-viewer-batch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                string[] imagePaths = new string[2];
                for (int i = 0; i < imagePaths.Length; i++)
                {
                    imagePaths[i] = Path.Combine(root, $"round-{i}.png");
                    SaveBitmap(imagePaths[i], CreateBitmap(20, 16, 90));
                }

                string csvPath = Path.Combine(root, "batch.csv");
                CircleRoi[] rois =
                [
                    new() { Center = new PointD(5, 5), Radius = 2, Label = "duplicate" },
                    new() { Center = new PointD(8, 5), Radius = 3, Label = "duplicate" },
                    new() { Center = new PointD(11, 5), Radius = 4 },
                    new() { Center = new PointD(14, 5), Radius = 5 }
                ];

                BatchRoiAnalysisExportSummary summary = await BatchRoiAnalysisExportService.ExportAsync(
                    csvPath, imagePaths, rois, pixelSize: 1, physicalUnit: "mm");

                Assert.Equal(2, summary.RepeatabilitySourceCount);
                Assert.Equal(0, summary.RepeatabilityConclusiveItemCount);
                string report = File.ReadAllText(Path.ChangeExtension(csvPath, ".repeatability.md"));
                Assert.Contains("未指定，反映源间变化", report);
                Assert.Contains("无法给出同一工件重复性结论", report);

                using JsonDocument metadata = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(csvPath, ".metadata.json")));
                JsonElement repeatability = metadata.RootElement.GetProperty("repeatability");
                Assert.Equal("Unspecified", repeatability.GetProperty("workpieceContext").GetString());
                JsonElement[] items = repeatability.GetProperty("items").EnumerateArray().ToArray();
                Assert.Equal(4, items.Length);
                Assert.All(items, item => Assert.Equal(2, item.GetProperty("sampleCount").GetInt32()));
                Assert.Equal(2, items.Count(item => item.GetProperty("label").GetString() == "duplicate"));
                Assert.Equal("roi:0", items[0].GetProperty("measurementItemKey").GetString());
                Assert.Equal("mm", items[0].GetProperty("unit").GetString());

                string csv = File.ReadAllText(csvPath);
                Assert.Contains(",SourceIndex,MeasurementItemKey", csv);
                Assert.Contains(",0,\"roi:0\"", csv);
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(csvPath))).ToLowerInvariant(),
                    metadata.RootElement.GetProperty("artifacts").GetProperty("csvSha256").GetString());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static BitmapSource CreateBitmap(int width, int height, byte value)
        {
            return BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Gray8,
                null,
                Enumerable.Repeat(value, width * height).ToArray(),
                width);
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
