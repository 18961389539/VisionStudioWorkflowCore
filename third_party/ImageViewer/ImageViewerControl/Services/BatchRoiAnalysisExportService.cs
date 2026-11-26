using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using ImageViewer.Plugins;

namespace ImageViewer.Services
{
    /// <summary>进度通知。CompletedFileCount 只统计已经落入结果摘要的输入文件。</summary>
    public sealed record BatchRoiAnalysisExportProgress(
        int RequestedFileCount,
        int CompletedFileCount,
        string CurrentPath,
        string Phase,
        int ExportedRowCount);

    /// <summary>
    /// 多图复用当前 ROI 模板执行测量并合并导出。
    /// Chinese: 几何 ROI 直接复用；卡尺 ROI 会在每张图上重新检测，单张失败不会中断整个批次，
    /// 且缺失或不可读输入仍会为每个 ROI 输出带错误状态的结果行。
    /// English: Reuses the current ROI templates across images; caliper ROIs are re-detected per image and
    /// an individual failure does not abort the batch. Missing or unreadable inputs still produce one
    /// error row per ROI so the export remains auditable.
    /// </summary>
    public static class BatchRoiAnalysisExportService
    {
        private static readonly JsonSerializerOptions MetadataJsonOptions = new() { WriteIndented = true };

        public static Task<BatchRoiAnalysisExportSummary> ExportAsync(
            string outputPath,
            IEnumerable<string> imagePaths,
            IEnumerable<RoiBase> roiTemplates,
            double pixelSize,
            string? physicalUnit,
            CameraCalibration? calibration = null,
            RoiPluginRegistry? pluginRegistry = null,
            IReadOnlyDictionary<string, string>? renderSettings = null,
            IProgress<BatchRoiAnalysisExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return ExportAsync(
                outputPath,
                imagePaths,
                roiTemplates,
                pixelSize,
                physicalUnit,
                calibration,
                pluginRegistry,
                renderSettings,
                qualityProfile: null,
                progress: progress,
                cancellationToken: cancellationToken);
        }

        public static Task<BatchRoiAnalysisExportSummary> ExportAsync(
            string outputPath,
            IEnumerable<string> imagePaths,
            IEnumerable<RoiBase> roiTemplates,
            double pixelSize,
            string? physicalUnit,
            RepeatabilityStudyOptions repeatabilityOptions,
            CameraCalibration? calibration = null,
            RoiPluginRegistry? pluginRegistry = null,
            IReadOnlyDictionary<string, string>? renderSettings = null,
            IProgress<BatchRoiAnalysisExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return ExportAsync(outputPath, imagePaths, roiTemplates, pixelSize, physicalUnit, calibration,
                pluginRegistry, renderSettings, qualityProfile: null, repeatabilityOptions: repeatabilityOptions,
                progress: progress, cancellationToken: cancellationToken);
        }

        public static Task<BatchRoiAnalysisExportSummary> ExportAsync(
            string outputPath,
            IEnumerable<string> imagePaths,
            IEnumerable<RoiBase> roiTemplates,
            double pixelSize,
            string? physicalUnit,
            CameraCalibration? calibration,
            RoiPluginRegistry? pluginRegistry,
            IReadOnlyDictionary<string, string>? renderSettings,
            ImageAnalysisQualityProfile? qualityProfile,
            IProgress<BatchRoiAnalysisExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return ExportAsync(outputPath, imagePaths, roiTemplates, pixelSize, physicalUnit, calibration,
                pluginRegistry, renderSettings, qualityProfile, repeatabilityOptions: new RepeatabilityStudyOptions(),
                progress: progress, cancellationToken: cancellationToken);
        }

        public static async Task<BatchRoiAnalysisExportSummary> ExportAsync(
            string outputPath,
            IEnumerable<string> imagePaths,
            IEnumerable<RoiBase> roiTemplates,
            double pixelSize,
            string? physicalUnit,
            CameraCalibration? calibration,
            RoiPluginRegistry? pluginRegistry,
            IReadOnlyDictionary<string, string>? renderSettings,
            ImageAnalysisQualityProfile? qualityProfile,
            RepeatabilityStudyOptions repeatabilityOptions,
            IProgress<BatchRoiAnalysisExportProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            ArgumentNullException.ThrowIfNull(imagePaths);
            ArgumentNullException.ThrowIfNull(roiTemplates);
            ImageViewerValidation.ValidatePixelSize(pixelSize);
            ImageAnalysisQualityProfile quality = qualityProfile ?? ImageAnalysisQualityProfile.Default;
            quality.Validate();

            string[] sources = imagePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizePath)
                .ToArray();
            RoiBase[] templates = roiTemplates.Select(roi => roi.Clone()).ToArray();
            if (sources.Length == 0)
            {
                throw new ArgumentException("至少需要一张输入图像。", nameof(imagePaths));
            }

            var rows = new List<RoiAnalysisBatchCsvRow>();
            var errors = new List<string>();
            var inputTraces = new List<BatchInputTrace>(sources.Length);
            bool canceled = false;

            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                string sourcePath = sources[sourceIndex];
                var inputTrace = new BatchInputTrace(sourceIndex, sourcePath);
                bool bitmapLoaded = false;
                RoiAnalysisBatchCsvRow[] sourceRows;
                progress?.Report(new BatchRoiAnalysisExportProgress(sources.Length, inputTraces.Count, sourcePath, "Preparing", rows.Count));
                try
                {
                    BitmapSource bitmap = await ImageViewerImageDecoder.LoadBatchImageAsync(
                        sourcePath, inputTrace.SetFileTrace, cancellationToken).ConfigureAwait(false);
                    bitmapLoaded = true;
                    inputTrace.PixelWidth = bitmap.PixelWidth;
                    inputTrace.PixelHeight = bitmap.PixelHeight;
                    sourceRows = await Task.Run(
                        () => AnalyzeSource(sourceIndex, sourcePath, bitmap, templates, pixelSize, physicalUnit, calibration, quality, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                    inputTrace.RowErrors.AddRange(sourceRows
                        .Where(row => !string.IsNullOrWhiteSpace(row.Error))
                        .Select(row => $"{row.RoiLabel}: {row.Error}"));
                    if (inputTrace.RowErrors.Count == 0)
                    {
                        inputTrace.Status = BatchInputStatus.Processed;
                    }
                    else if (inputTrace.RowErrors.Count < sourceRows.Length)
                    {
                        inputTrace.Status = BatchInputStatus.PartiallyProcessed;
                        inputTrace.Error = "部分 ROI 分析失败。";
                    }
                    else
                    {
                        inputTrace.Status = BatchInputStatus.AnalysisFailed;
                        inputTrace.Error = "所有 ROI 分析失败。";
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }
                catch (Exception ex)
                {
                    bool missing = !inputTrace.Exists && (ex is FileNotFoundException or DirectoryNotFoundException || !File.Exists(sourcePath));
                    inputTrace.Status = bitmapLoaded ? BatchInputStatus.AnalysisFailed
                        : missing ? BatchInputStatus.InputMissing : BatchInputStatus.DecodeFailed;
                    string error = missing ? "找不到输入图像。" : ex.Message;
                    inputTrace.Exists = !missing;
                    inputTrace.Error = error;
                    sourceRows = CreateFailureRows(sourceIndex, sourcePath, inputTrace.Status.ToString(), error, templates, pixelSize, physicalUnit, calibration);
                    errors.Add($"{Path.GetFileName(sourcePath)}: {error}");
                }

                // An interrupted input has no published rows and must not count as completed.
                inputTrace.RowCount = sourceRows.Length;
                inputTraces.Add(inputTrace);
                rows.AddRange(sourceRows);
                progress?.Report(new BatchRoiAnalysisExportProgress(sources.Length, inputTraces.Count, sourcePath, "Completed", rows.Count));
            }

            int fullySuccessfulFileCount = inputTraces.Count(trace => trace.Status == BatchInputStatus.Processed);
            int partiallySuccessfulFileCount = inputTraces.Count(trace => trace.Status == BatchInputStatus.PartiallyProcessed);
            int allRoiFailedFileCount = inputTraces.Count(trace => trace.Status == BatchInputStatus.AnalysisFailed);
            int decodedFileCount = fullySuccessfulFileCount + partiallySuccessfulFileCount + allRoiFailedFileCount;
            string csv = RoiAnalysisExportService.BuildBatchCsv(rows);
            // Persist completed work even when the user cancels. The summary is useful
            // for resuming or auditing a long batch and must not be blocked by the
            // canceled token that triggered the partial export.
            string metadataPath = Path.ChangeExtension(outputPath, ".metadata.json");
            string summaryPath = Path.ChangeExtension(outputPath, ".summary.json");
            // 重复性研究随批次一并产出：多图批次正是重复性的唯一真实来源（单图只有 1 个样本）。
            string repeatabilityPath = RoiAnalysisExportService.GetRepeatabilityPath(outputPath);
            string batchId = Guid.NewGuid().ToString("N");
            RepeatabilityStudy repeatabilityStudy = RoiAnalysisExportService.BuildRepeatabilityStudy(
                rows, physicalUnit, repeatabilityOptions, inputTraces.Select(trace => trace.InputIndex));
            string repeatabilityMarkdown = RepeatabilityStudyRenderer.RenderMarkdown(repeatabilityStudy);
            // 跨图同名项才是真正的过程随时间样本；能力指数附在重复性报告尾部，避免再产出一个只多几行的文件。
            IReadOnlyList<SpcMeasurementSeries> spcSeries = SpcMeasurementSeriesBuilder.BuildFromBatchRows(rows);
            string processCapabilityMarkdown = SpcReportFormatter.BuildMarkdown(spcSeries);
            if (processCapabilityMarkdown.Length > 0)
            {
                repeatabilityMarkdown = repeatabilityMarkdown.TrimEnd() + Environment.NewLine + Environment.NewLine + processCapabilityMarkdown;
            }
            string csvHash = ImageViewerExportArtifactSet.ComputeSha256Utf8Text(csv);
            string repeatabilityHash = ImageViewerExportArtifactSet.ComputeSha256Utf8Text(repeatabilityMarkdown);
            BatchRoiAnalysisExportSummary summary = new(
                sources.Length,
                fullySuccessfulFileCount,
                inputTraces.Count - fullySuccessfulFileCount,
                rows.Count,
                errors)
            {
                DecodedFileCount = decodedFileCount,
                FullySuccessfulFileCount = fullySuccessfulFileCount,
                PartiallySuccessfulFileCount = partiallySuccessfulFileCount,
                AllRoiFailedFileCount = allRoiFailedFileCount,
                InputFailedFileCount = inputTraces.Count - decodedFileCount,
                UnfinishedFileCount = sources.Length - inputTraces.Count,
                IsCanceled = canceled,
                MetadataPath = metadataPath,
                SummaryPath = summaryPath,
                RepeatabilityPath = repeatabilityPath,
                RepeatabilitySourceCount = repeatabilityStudy.SourceCount,
                RepeatabilityConclusiveItemCount = repeatabilityStudy.ConclusiveItemCount,
                RepeatabilityToleranceConsumedItemCount = repeatabilityStudy.ToleranceConsumedCount,
                BatchId = batchId,
                CsvSha256 = csvHash,
                RepeatabilitySha256 = repeatabilityHash
            };
            var metadata = new Dictionary<string, object?>
            {
                ["exportFormatVersion"] = 2,
                ["batch"] = true,
                ["batchId"] = batchId,
                ["status"] = canceled ? "Canceled" : "Completed",
                ["canceled"] = canceled,
                ["exportedAtUtc"] = DateTimeOffset.UtcNow,
                ["result"] = new Dictionary<string, object?>
                {
                    ["csvSha256"] = csvHash,
                    ["requestedFileCount"] = summary.RequestedFileCount,
                    ["processedFileCount"] = summary.ProcessedFileCount,
                    ["failedFileCount"] = summary.FailedFileCount,
                    ["unfinishedFileCount"] = summary.UnfinishedFileCount,
                    ["decodedFileCount"] = summary.DecodedFileCount,
                    ["fullySuccessfulFileCount"] = summary.FullySuccessfulFileCount,
                    ["partiallySuccessfulFileCount"] = summary.PartiallySuccessfulFileCount,
                    ["allRoiFailedFileCount"] = summary.AllRoiFailedFileCount,
                    ["inputFailedFileCount"] = summary.InputFailedFileCount,
                    ["exportedRowCount"] = summary.ExportedRowCount,
                    ["errors"] = summary.Errors
                },
                // Keep the original path-only field for consumers of metadata version 2.
                ["inputs"] = sources,
                // Detailed records provide an auditable entry for every completed input,
                // including missing, unreadable, and successfully processed files.
                ["inputRecords"] = inputTraces.Select(trace => trace.ToMetadata()).ToArray(),
                ["calibration"] = new Dictionary<string, object?>
                {
                    ["pixelSize"] = pixelSize,
                    ["physicalUnit"] = string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit,
                    ["camera"] = calibration
                },
                ["qualityThresholds"] = quality,
                ["distortionAudit"] = RoiAnalysisExportService.BuildDistortionMetadata(
                    rows.Aggregate(DistortionAudit.Empty, (audit, row) => audit.Combine(row.Inspection.DistortionAudit))),
                ["roiCount"] = templates.Length,
                ["processCapability"] = SpcReportFormatter.BuildMetadata(spcSeries),
                ["repeatability"] = new Dictionary<string, object?>
                {
                    ["path"] = repeatabilityPath,
                    ["workpieceContext"] = repeatabilityStudy.Context.Context.ToString(),
                    ["workpieceId"] = repeatabilityStudy.Context.WorkpieceId,
                    ["sourceCount"] = repeatabilityStudy.SourceCount,
                    ["conclusiveItemCount"] = repeatabilityStudy.ConclusiveItemCount,
                    ["inconclusiveItemCount"] = repeatabilityStudy.InconclusiveItemCount,
                    ["toleranceConsumedItemCount"] = repeatabilityStudy.ToleranceConsumedCount,
                    ["hasConclusiveResult"] = repeatabilityStudy.HasConclusiveResult,
                    ["items"] = repeatabilityStudy.Items.Select(item => new Dictionary<string, object?>
                    {
                        ["label"] = item.Label,
                        ["measurementItemKey"] = item.MeasurementItemKey,
                        ["unit"] = item.Unit,
                        ["quantity"] = item.Quantity,
                        ["sampleCount"] = item.Statistics.SampleCount,
                        ["mean"] = item.Statistics.Mean,
                        ["standardDeviation"] = item.Statistics.StandardDeviation,
                        ["expandedUncertainty"] = item.Statistics.ExpandedUncertainty,
                        ["range"] = item.Statistics.Range,
                        ["toleranceBand"] = item.ToleranceBand,
                        ["shareOfTolerancePercent"] = item.RepeatabilityShareOfTolerancePercent
                    }).ToList()
                },
                ["roiTypes"] = templates
                    .GroupBy(roi => roi.RoiTypeName)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                ["renderSettings"] = renderSettings,
                ["hasRoiPersistenceRegistry"] = pluginRegistry != null
            };
            string summaryJson = JsonSerializer.Serialize(summary, MetadataJsonOptions);
            metadata["artifacts"] = new Dictionary<string, object?>
            {
                ["csvSha256"] = csvHash,
                ["repeatabilitySha256"] = repeatabilityHash,
                ["summarySha256"] = ImageViewerExportArtifactSet.ComputeSha256Utf8Text(summaryJson)
            };
            string metadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
            await ImageViewerExportArtifactSet.WriteAllTextAsync(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [outputPath] = csv,
                    [repeatabilityPath] = repeatabilityMarkdown,
                    [metadataPath] = metadataJson,
                    [summaryPath] = summaryJson
                },
                CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new BatchRoiAnalysisExportProgress(sources.Length, inputTraces.Count, string.Empty, canceled ? "Canceled" : "Exported", rows.Count));
            if (canceled)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return summary;
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (ArgumentException)
            {
                // Keep malformed paths as traceable input records; the per-file loop
                // will classify them as InputMissing/DecodeFailed instead of aborting the batch.
                return path;
            }
            catch (NotSupportedException)
            {
                return path;
            }
        }

        private static RoiAnalysisBatchCsvRow[] AnalyzeSource(
            int sourceIndex,
            string sourcePath,
            BitmapSource bitmap,
            IReadOnlyList<RoiBase> templates,
            double pixelSize,
            string? physicalUnit,
            CameraCalibration? calibration,
            ImageAnalysisQualityProfile quality,
            CancellationToken cancellationToken)
        {
            var rows = new RoiAnalysisBatchCsvRow[templates.Count];
            // Caliper detections on the same source share one intensity conversion: decoding
            // and converting the full image per template would dominate large batches.
            IntensityImage? intensity = null;
            for (int i = 0; i < templates.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RoiBase roi = templates[i].Clone();
                string status = "Geometry";
                string? error = null;

                try
                {
                    if (CaliperRoiDetection.Supports(roi))
                    {
                        CaliperRoiDetection.Clear(roi);
                        intensity ??= VisionAlgorithmEntryPoint.CreateIntensity(bitmap);
                        bool measured = CaliperRoiDetection.TryDetectAndApply(bitmap, roi, quality, intensity);
                        status = measured ? "Measured" : "DetectionFailed";
                        error = measured ? null : "未找到足够可靠的边缘。";
                    }
                }
                catch (Exception ex)
                {
                    status = "DetectionFailed";
                    error = ex.Message;
                }

                cancellationToken.ThrowIfCancellationRequested();
                rows[i] = RoiAnalysisExportService.CaptureBatchRow(
                    sourcePath, status, error, roi, bitmap, pixelSize, physicalUnit, calibration, sourceIndex, $"roi:{i}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            return rows;
        }

        private static RoiAnalysisBatchCsvRow[] CreateFailureRows(
            int sourceIndex,
            string sourcePath,
            string status,
            string error,
            IReadOnlyList<RoiBase> templates,
            double pixelSize,
            string? physicalUnit,
            CameraCalibration? calibration)
        {
            return templates
                .Select((template, index) => RoiAnalysisExportService.CaptureBatchRow(
                    sourcePath,
                    status,
                    error,
                    template,
                    bitmap: null,
                    pixelSize,
                    physicalUnit,
                    calibration,
                    sourceIndex,
                    $"roi:{index}"))
                .ToArray();
        }

        // Names are written to inputRecords and to CSV rows for file-level failures.
        private enum BatchInputStatus
        {
            Processed,
            PartiallyProcessed,
            AnalysisFailed,
            InputMissing,
            DecodeFailed
        }

        private sealed class BatchInputTrace
        {
            public BatchInputTrace(int inputIndex, string path)
            {
                InputIndex = inputIndex;
                Path = path;
            }

            public int InputIndex { get; }

            public string Path { get; }

            public bool Exists { get; set; }

            public BatchInputStatus Status { get; set; }

            public string? Error { get; set; }

            public string? MetadataError { get; private set; }

            public string? Sha256 { get; private set; }

            public long? LengthBytes { get; private set; }

            public DateTime? LastWriteTimeUtc { get; private set; }

            public int? PixelWidth { get; set; }

            public int? PixelHeight { get; set; }

            public int RowCount { get; set; }

            public List<string> RowErrors { get; } = new();

            public void SetFileTrace(ImageViewerImageDecoder.ImageFileTrace trace)
            {
                Exists = true;
                Sha256 = trace.Sha256.ToLowerInvariant();
                LengthBytes = trace.Length;
                LastWriteTimeUtc = trace.LastWriteTimeUtc;
                MetadataError = trace.MetadataError;
            }

            public Dictionary<string, object?> ToMetadata()
            {
                return new Dictionary<string, object?>
                {
                    ["inputIndex"] = InputIndex,
                    ["path"] = Path,
                    ["exists"] = Exists,
                    ["status"] = Status.ToString(),
                    ["sha256"] = Sha256,
                    ["lengthBytes"] = LengthBytes,
                    ["lastWriteTimeUtc"] = LastWriteTimeUtc,
                    ["pixelWidth"] = PixelWidth,
                    ["pixelHeight"] = PixelHeight,
                    ["rowCount"] = RowCount,
                    ["error"] = Error,
                    ["metadataError"] = MetadataError,
                    ["rowErrors"] = RowErrors.Count == 0 ? null : RowErrors.ToArray()
                };
            }
        }
    }

    /// <summary>
    /// Summary of a batch export. The legacy processed/failed counts describe fully successful
    /// files versus files with any ROI or input failure; the detailed counters below explain the split.
    /// </summary>
    public sealed record BatchRoiAnalysisExportSummary(
        int RequestedFileCount,
        int ProcessedFileCount,
        int FailedFileCount,
        int ExportedRowCount,
        IReadOnlyList<string> Errors)
    {
        /// <summary>
        /// Number of completed inputs that decoded successfully, including inputs with some or all ROI analysis failures.
        /// </summary>
        public int DecodedFileCount { get; init; }

        /// <summary>
        /// Number of decoded inputs for which every ROI produced a result without an analysis error.
        /// </summary>
        public int FullySuccessfulFileCount { get; init; }

        /// <summary>
        /// Number of decoded inputs for which at least one ROI succeeded and at least one ROI failed.
        /// </summary>
        public int PartiallySuccessfulFileCount { get; init; }

        /// <summary>
        /// Number of decoded inputs for which every ROI analysis failed.
        /// </summary>
        public int AllRoiFailedFileCount { get; init; }

        /// <summary>
        /// Number of inputs that could not be read or decoded.
        /// </summary>
        public int InputFailedFileCount { get; init; }

        /// <summary>Number of inputs with no result because the batch was canceled before or during their processing.</summary>
        public int UnfinishedFileCount { get; init; }

        /// <summary>用户在导出完成前取消了批次；已完成输入仍会保存到输出与摘要文件。</summary>
        public bool IsCanceled { get; init; }

        public string? MetadataPath { get; init; }

        public string? SummaryPath { get; init; }

        /// <summary>重复性研究报告路径（<c>*.repeatability.md</c>）。</summary>
        public string? RepeatabilityPath { get; init; }

        /// <summary>参与重复性研究的图像数。</summary>
        public int RepeatabilitySourceCount { get; init; }

        /// <summary>样本数足够（≥2）因而能给出重复性结论的项数。</summary>
        public int RepeatabilityConclusiveItemCount { get; init; }

        /// <summary>重复性已占满公差带（≥100%）的项数；&gt;0 表示该测量方案的可判定性存在问题。</summary>
        public int RepeatabilityToleranceConsumedItemCount { get; init; }

        /// <summary>Links the CSV, repeatability report, summary, and metadata from one export.</summary>
        public string? BatchId { get; init; }

        /// <summary>SHA-256 of the exact UTF-8-with-BOM CSV bytes.</summary>
        public string? CsvSha256 { get; init; }

        /// <summary>SHA-256 of the exact UTF-8-with-BOM repeatability report bytes.</summary>
        public string? RepeatabilitySha256 { get; init; }
    }
}
