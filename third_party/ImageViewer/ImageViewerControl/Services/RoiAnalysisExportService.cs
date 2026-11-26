using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Core.Persistence;
using ImageViewer.Controls;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services
{
    public static class RoiAnalysisExportService
    {
        private static readonly JsonSerializerOptions MetadataJsonOptions = new() { WriteIndented = true };

        public static string BuildSummary(IEnumerable<RoiBase> rois, BitmapSource? bitmap, double pixelSize, string? physicalUnit, CameraCalibration? calibration = null)
        {
            ArgumentNullException.ThrowIfNull(rois);
            ImageViewerValidation.ValidatePixelSize(pixelSize);

            var roiList = rois.ToList();
            var lines = new List<string>
            {
                $"Total ROIs: {roiList.Count}"
            };

            foreach (var group in roiList.GroupBy(roi => roi.RoiTypeName).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                lines.Add($"- {group.Key}: {group.Count()}");
            }

            var usable = roiList.Where(roi => RoiCalibrationHelper.CanUseMeasurements(calibration, RoiCalibrationHelper.AuditRoi(roi, calibration))).ToList();
            string unit = string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit;
            if (usable.Count != roiList.Count)
            {
                string reason = calibration is { IsValid: false } ? "invalid calibration" : "undistortion did not converge";
                lines.Add($"ROIs excluded from measurement totals ({reason}): {roiList.Count - usable.Count}");
                if (usable.Count == 0)
                {
                    lines.Add("Measurement totals: n/a");
                    lines.AddRange(BuildPrecisionLines(roiList, pixelSize, unit, calibration));
                    return string.Join(Environment.NewLine, lines);
                }
            }
            double totalLineLength = usable.OfType<LineMeasureRoi>().Sum(line => GeometryUtils.Distance(line.P1.ToWpfPoint(), line.P2.ToWpfPoint()) * RoiCalibrationHelper.GetLengthCorrection(line, calibration) * pixelSize);
            double totalPolylineLength = usable.OfType<PolylineRoi>().Sum(poly => GeometryUtils.PolylineLength(poly.Points.ToWpfPointArray()) * RoiCalibrationHelper.GetLengthCorrection(poly, calibration)) * pixelSize;
            double totalPolygonArea = usable.OfType<PolygonRoi>().Where(poly => poly.IsClosed && poly.Points.Count >= 3).Sum(poly => GeometryUtils.PolygonArea(poly.Points.ToWpfPointArray()) * RoiCalibrationHelper.GetAreaCorrection(poly, calibration)) * pixelSize * pixelSize;
            double totalRectArea = usable.OfType<RotatedRect>().Sum(rect => rect.Width * rect.Height * RoiCalibrationHelper.GetAreaCorrection(rect, calibration)) * pixelSize * pixelSize;
            double totalEllipseArea = usable.OfType<EllipseRoi>().Sum(ellipse => Math.PI * ellipse.RadiusX * ellipse.RadiusY * RoiCalibrationHelper.GetAreaCorrection(ellipse, calibration)) * pixelSize * pixelSize;
            double totalCircleArea = usable.OfType<CircleRoi>().Sum(circle => Math.PI * circle.Radius * circle.Radius * RoiCalibrationHelper.GetAreaCorrection(circle, calibration)) * pixelSize * pixelSize;
            totalCircleArea += usable.OfType<ThreePointCircleMeasureRoi>().Where(circle => circle.IsValid).Sum(circle => Math.PI * circle.Radius * circle.Radius * RoiCalibrationHelper.GetAreaCorrection(circle, calibration)) * pixelSize * pixelSize;
            double totalCenterDistance = usable.OfType<CenterDistanceMeasureRoi>().Sum(distance => distance.CenterDistance * RoiCalibrationHelper.GetLengthCorrection(distance, calibration)) * pixelSize;

            lines.Add(string.Empty);
            lines.Add($"Line Length Total: {totalLineLength:F2} {unit}");
            lines.Add($"Center Distance Total: {totalCenterDistance:F2} {unit}");
            lines.Add($"Polyline Length Total: {totalPolylineLength:F2} {unit}");
            lines.Add($"Area Total: {totalPolygonArea + totalRectArea + totalEllipseArea + totalCircleArea:F2} {unit}²");
            if (usable.OfType<AngleMeasureRoi>().Any())
            {
                lines.Add($"Average Angle: {usable.OfType<AngleMeasureRoi>().Average(angle => GeometryUtils.SmallestAngle(angle.P1.ToWpfPoint(), angle.Vertex.ToWpfPoint(), angle.P2.ToWpfPoint())):F2}°");
            }

            if (bitmap != null)
            {
                int statisticsCount = roiList.Count(roi => ImageAnalysisService.TryCalculateStatistics(bitmap, roi, out _));
                lines.Add($"ROIs With Statistics: {statisticsCount}");
            }

            lines.AddRange(BuildPrecisionLines(roiList, pixelSize, unit, calibration));

            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// 生成结构化测量报告（Markdown）。
        /// Chinese: 与 CSV 同目录、同基名（`*.report.md`），凑成"数据 + 可读报告"一套交付物。
        /// 报告头部包含标定身份与前置检查结论，因为这说明"这些数字凭什么可信"。
        /// English: Produces the human-readable half of the deliverable. The header carries calibration
        /// identity and readiness verdict so the numbers can be justified, not just quoted.
        /// </summary>
        public static string BuildReport(
            IEnumerable<RoiBase> rois,
            BitmapSource? bitmap,
            double pixelSize,
            string? physicalUnit,
            CameraCalibration? calibration,
            string? sourceName = null,
            string? sourceSha256 = null,
            bool hasKnownReference = false)
        {
            ArgumentNullException.ThrowIfNull(rois);

            var roiList = rois.ToList();
            MeasurementReport report = BuildReportModel(
                roiList,
                pixelSize,
                physicalUnit,
                calibration,
                sourceName,
                sourceSha256,
                hasKnownReference);

            IReadOnlyList<SpcMeasurementSeries> spcSeries = SpcMeasurementSeriesBuilder.BuildFromRois(roiList, pixelSize, physicalUnit, calibration);
            return MeasurementReportRenderer.RenderMarkdown(report, SpcReportFormatter.BuildMarkdown(spcSeries));
        }

        /// <summary>
        /// 构建报告的结构化模型（不含渲染）。
        /// Chinese: Markdown 导出与可打印报告共用同一份模型，避免“打印稿”和“文件稿”的数字来自两次独立评估。
        /// English: One model for both Markdown and printable output so the numbers are evaluated exactly once.
        /// </summary>
        public static MeasurementReport BuildReportModel(
            IEnumerable<RoiBase> rois,
            double pixelSize,
            string? physicalUnit,
            CameraCalibration? calibration,
            string? sourceName = null,
            string? sourceSha256 = null,
            bool hasKnownReference = false)
        {
            ArgumentNullException.ThrowIfNull(rois);

            var items = new List<MeasurementReportItem>();
            DistortionAudit distortionAudit = DistortionAudit.Empty;
            foreach (RoiBase roi in rois)
            {
                RoiInspectionResult inspection = RoiInspectionEvaluator.Evaluate(roi, pixelSize, calibration);
                distortionAudit = distortionAudit.Combine(inspection.DistortionAudit);
                string unit = DescribeUnit(roi, physicalUnit);
                items.Add(new MeasurementReportItem(
                    DescribeRoi(roi),
                    DescribeQuantity(roi),
                    unit,
                    inspection.MeasuredValue,
                    inspection.NominalValue,
                    inspection.TolerancePlus,
                    inspection.ToleranceMinus,
                    inspection.DetectionStatus.ToString(),
                    inspection.SpecificationStatus.ToString()));
            }

            return MeasurementReportBuilder.Build(
                items,
                pixelSize,
                physicalUnit,
                calibration,
                sourceName: sourceName,
                sourceSha256: sourceSha256,
                hasKnownReference: hasKnownReference,
                title: null,
                generatedAtUtc: null,
                distortionAudit: distortionAudit);
        }

        /// <summary>报告落到 CSV 同目录、同基名的 <c>.report.md</c>。</summary>
        internal static string GetReportPath(string csvFilePath) =>
            Path.ChangeExtension(csvFilePath, ".report.md");

        /// <summary>重复性研究落到 CSV 同目录、同基名的 <c>.repeatability.md</c>。</summary>
        internal static string GetRepeatabilityPath(string csvFilePath) =>
            Path.ChangeExtension(csvFilePath, ".repeatability.md");

        /// <summary>
        /// 由批次结果构建重复性研究。
        /// Chinese: 单张图每个 ROI 只有一个值，离散度无从谈起；只有把同一 ROI 在多张图上的结果
        /// 聚起来（分组键 = 标签，回退到类型名），重复性才成立。检测失败/未测到的项**不参与**分组，
        /// 否则"漏测"会被当成 0 拉低离散度、把不可靠的测量方案粉饰成稳定。
        /// English: Aggregates batch rows into a repeatability study. Rows without a measured value are
        /// excluded rather than coerced to 0 — a missing detection is not a measurement of zero.
        /// </summary>
        internal static RepeatabilityStudy BuildRepeatabilityStudy(
            IEnumerable<RoiAnalysisBatchCsvRow> rows,
            string? physicalUnit,
            RepeatabilityStudyOptions? options = null,
            IEnumerable<int>? sourceIndices = null)
        {
            ArgumentNullException.ThrowIfNull(rows);

            var rowList = rows.ToList();
            var samples = new List<RepeatabilityStudyBuilder.RoundSample>(rowList.Count);
            var toleranceByKey = new Dictionary<string, (double? Nominal, double? Plus, double? Minus)?>(StringComparer.Ordinal);
            var quantityByKey = new Dictionary<string, string>(StringComparer.Ordinal);
            var unitByKey = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int index = 0; index < rowList.Count; index++)
            {
                RoiAnalysisBatchCsvRow row = rowList[index];
                // Batch identity follows the template slot across input images. Labels describe
                // the item to a person, but may be duplicated or blank and are not grouping keys.
                string key = row.MeasurementItemKey ?? $"unidentified-row:{index}";
                string label = row.DisplayLabel;

                toleranceByKey.TryAdd(key, row.Tolerance);
                quantityByKey.TryAdd(key, row.Quantity);
                unitByKey.TryAdd(key, row.Unit);

                RoiInspectionResult inspection = row.Inspection;
                if (inspection.MeasuredValue is not { } measured || !double.IsFinite(measured))
                {
                    // 未测得值不计入样本：留空是"不知道"，不是"等于 0"。
                    continue;
                }

                samples.Add(new RepeatabilityStudyBuilder.RoundSample(key, measured, row.SourceIndex, label));
            }

            return RepeatabilityStudyBuilder.Build(
                samples,
                key => toleranceByKey.TryGetValue(key, out var tolerance) ? tolerance : null,
                key => quantityByKey.TryGetValue(key, out string? quantity) ? quantity : null,
                physicalUnit,
                options: options,
                sourceIndices: rowList.Select(row => row.SourceIndex).Concat(sourceIndices ?? []),
                unitLookup: key => unitByKey.TryGetValue(key, out string? itemUnit) ? itemUnit : null);
        }

        internal static string DescribeUnit(RoiBase roi, string? physicalUnit) => roi switch
        {
            ParallelismMeasureRoi or PerpendicularityMeasureRoi or AngleMeasureRoi => "°",
            _ => string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit
        };

        private static (double? Nominal, double? Plus, double? Minus)? ToToleranceInput(MeasurementTolerance? tolerance) =>
            tolerance is { IsEnabled: true }
                ? (tolerance.Nominal, tolerance.TolerancePlus, tolerance.ToleranceMinus)
                : null;

        /// <summary>
        /// 角度类 ROI 的主测量量的单位文案。
        /// Chinese: 方向公差（平行度/垂直度）与同心度在 2D 图上比较的是角度/直径差，与长度量不同，
        /// 报告必须写清楚量名与单位，否则"0.05"到底是毫米还是度无法判断。
        /// </summary>
        internal static string DescribeQuantity(RoiBase roi) => roi switch
        {
            ParallelismMeasureRoi => "平行度角度偏差",
            PerpendicularityMeasureRoi => "垂直度角度偏差",
            ConcentricityMeasureRoi => "同心度公差带直径 (2e)",
            AngleMeasureRoi => "夹角",
            CaliperMeasureRoi => "宽度(边缘中心距)",
            LineCaliperMeasureRoi => "长度",
            ArcCaliperMeasureRoi => "弧长",
            CircularCaliperMeasureRoi => "半径",
            CircleRoi => "半径",
            ArcMeasureRoi => "半径",
            ThreePointCircleMeasureRoi => "半径",
            LineMeasureRoi => "长度",
            CenterDistanceMeasureRoi => "中心距",
            PointToLineDistanceRoi => "点到线距离",
            PointToCircleDistanceRoi => "点到圆距离",
            PolygonRoi polygon when polygon.IsClosed && polygon.Points.Count >= 3 => "面积",
            _ => "测量值"
        };

        /// <summary>
        /// 追加精度摘要。
        /// Chinese: 单次测量的结果必须带上"这次测量能信到什么程度"；有公差配置的 ROI 会给出
        /// 标定不确定度与综合不确定度，把"支持亚像素"与"测得准"分开说清楚。
        /// English: Appends the precision summary so single-shot results carry their uncertainty.
        /// </summary>
        private static IEnumerable<string> BuildPrecisionLines(IReadOnlyList<RoiBase> rois, double pixelSize, string unit, CameraCalibration? calibration)
        {
            var toleranceRois = rois.Where(roi => roi.Tolerance is { IsEnabled: true }).ToList();
            if (toleranceRois.Count == 0)
            {
                yield break;
            }

            double? calibrationUncertaintyPixels = calibration is { IsValid: true } && calibration.MeasurementUncertaintyPixels > 0
                ? calibration.MeasurementUncertaintyPixels
                : null;
            double? reprojectionErrorRms = calibration is { } c && c.ReprojectionErrorRms > 0 ? c.ReprojectionErrorRms : null;
            DistortionAudit summaryAudit = RoiCalibrationHelper.AuditRois(toleranceRois, calibration);

            yield return string.Empty;
            yield return $"[Precision] calibrated={calibration is { IsValid: true }} " +
                $"calibrationUncertainty={(calibrationUncertaintyPixels.HasValue ? $"{calibrationUncertaintyPixels.Value * pixelSize:F4} {unit}" : "n/a")} " +
                $"reprojectionRms={(reprojectionErrorRms.HasValue ? $"{reprojectionErrorRms.Value:F4} px" : "n/a")} " +
                $"undistortion={DescribeDistortionAudit(calibration, summaryAudit)}";

            foreach (var roi in toleranceRois)
            {
                RoiInspectionResult inspection = RoiInspectionEvaluator.Evaluate(roi, pixelSize, calibration);
                if (inspection.MeasuredValue is not { } measured)
                {
                    yield return $"[Precision] {DescribeRoi(roi)}: measured=n/a (single sample, no repeatability)";
                    continue;
                }

                // 单次测量无重复性可言，因此只报告标定侧不确定度，并明确说明重复性样本数为 1。
                // 若该 ROI 的测量点反解未收敛，取"标定不确定度 ⊕ 反解残差"中的较大值，
                // 而不是继续声称一个已经被残差突破的精度。
                DistortionAudit roiAudit = RoiCalibrationHelper.AuditRoi(roi, calibration);
                double? effectiveUncertaintyPixels = roiAudit.HasPoints
                    ? roiAudit.MaximumUncertaintyPixels
                    : calibrationUncertaintyPixels;
                double? combined = effectiveUncertaintyPixels.HasValue
                    ? effectiveUncertaintyPixels.Value * pixelSize
                    : null;
                string distortionSuffix = roiAudit.HasNonConvergedPoints
                    ? $" undistortion=not-converged({roiAudit.NonConvergedPointCount}/{roiAudit.PointCount},maxResidual={roiAudit.MaximumResidualPixels:F4}px)"
                    : string.Empty;
                yield return $"[Precision] {DescribeRoi(roi)}: measured={measured:F4} {DescribeUnit(roi, unit)} " +
                    $"combinedUncertainty(k=2)={(combined.HasValue ? $"{combined.Value:F4} {unit}" : "n/a")} " +
                    $"repeatability=1 sample{distortionSuffix}";
            }
        }

        private static string DescribeDistortionAudit(CameraCalibration? calibration, DistortionAudit audit)
        {
            if (calibration is not { IsEnabled: true, IsValid: true } || !audit.HasPoints)
            {
                return "n/a";
            }

            return audit.HasNonConvergedPoints
                ? $"not-converged({audit.NonConvergedPointCount}/{audit.PointCount},maxResidual={audit.MaximumResidualPixels:F4}px)"
                : "converged";
        }

        internal static string DescribeRoi(RoiBase roi)
        {
            return string.IsNullOrWhiteSpace(roi.Label) ? roi.RoiTypeName : roi.Label;
        }

        public static void SaveCsv(string filePath, IEnumerable<RoiBase> rois, BitmapSource? bitmap, double pixelSize, string? physicalUnit, CameraCalibration? calibration = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(rois);
            ImageViewerAtomicFile.WriteAllText(filePath, BuildCsv(rois, bitmap, pixelSize, physicalUnit, calibration), Encoding.UTF8);
        }

        public static Task SaveCsvAsync(string filePath, IEnumerable<RoiBase> rois, BitmapSource? bitmap, double pixelSize, string? physicalUnit, CameraCalibration? calibration = null, CancellationToken cancellationToken = default)
        {
            return SaveCsvAsync(filePath, rois, bitmap, pixelSize, physicalUnit, calibration, exportContext: null, cancellationToken: cancellationToken);
        }

        public static Task SaveCsvAsync(string filePath, IEnumerable<RoiBase> rois, BitmapSource? bitmap, double pixelSize, string? physicalUnit, CameraCalibration? calibration, RoiAnalysisExportContext? exportContext, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(rois);
            ImageViewerValidation.ValidatePixelSize(pixelSize);
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            filePath = Path.GetFullPath(filePath);
            RoiBase[] capturedRois = CaptureRois(rois);
            BitmapSource? capturedBitmap = bitmap == null || bitmap.IsFrozen
                ? bitmap : (BitmapSource)bitmap.GetCurrentValueAsFrozen();
            CameraCalibration? capturedCalibration = calibration?.Clone();
            RoiDocument? roiDocument = null;
            ImageViewerImageDecoder.ImageFileTrace? fileTrace = null;
            if (exportContext != null)
            {
                fileTrace = ImageViewerImageSourceRegistry.GetFileTrace(bitmap);
                exportContext = exportContext with
                {
                    SourcePath = ImageViewerImageSourceRegistry.TryGetPath(bitmap, out string registeredPath)
                        ? registeredPath : exportContext.SourcePath,
                    RenderSettings = exportContext.RenderSettings == null
                        ? null : new Dictionary<string, string>(exportContext.RenderSettings, StringComparer.Ordinal)
                };
                if (exportContext.PluginRegistry != null)
                {
                    roiDocument = RoiPersistenceService.CreateDocument(
                        capturedRois, pixelSize, physicalUnit, exportContext.PluginRegistry, qualityProfile: exportContext.QualityProfile);
                }
            }

            var snapshot = new ExportSnapshot(
                capturedRois, capturedBitmap, pixelSize, physicalUnit, capturedCalibration, exportContext, roiDocument, fileTrace);
            return Task.Run(() => SaveCsvSnapshotAsync(filePath, snapshot, cancellationToken), cancellationToken);
        }

        internal static RoiBase[] CaptureRois(IEnumerable<RoiBase> rois) =>
            rois.Select(roi =>
            {
                RoiBase copy = roi.Clone();
                if (ReferenceEquals(copy, roi))
                {
                    throw new InvalidOperationException("An ROI clone must be independent of its source before exporting.");
                }

                copy.MeasurementId = roi.MeasurementId;
                copy.AdvancedMeasurements = roi.AdvancedMeasurements;
                return copy;
            }).ToArray();

        private static async Task SaveCsvSnapshotAsync(string filePath, ExportSnapshot snapshot, CancellationToken cancellationToken)
        {
            BitmapSource? bitmap = snapshot.Bitmap == null ? null : ImageViewerPixelAccess.NormalizeForIntensity(snapshot.Bitmap);
            string csv = BuildCsv(snapshot.Rois, bitmap, snapshot.PixelSize, snapshot.PhysicalUnit, snapshot.Calibration, cancellationToken);
            if (snapshot.Context == null)
            {
                await ImageViewerExportArtifactSet.WriteAllTextAsync(
                    new Dictionary<string, string> { [filePath] = csv }, cancellationToken).ConfigureAwait(false);
                return;
            }

            string metadataPath = Path.ChangeExtension(filePath, ".metadata.json");
            string reportPath = GetReportPath(filePath);
            string exportBatchId = Guid.NewGuid().ToString("N");
            string csvHash = ImageViewerExportArtifactSet.ComputeSha256Utf8Text(csv);
            if (bitmap == null)
            {
                snapshot = snapshot with
                {
                    FileTrace = await ReadReferencedFileTraceAsync(snapshot.Context.SourcePath, cancellationToken).ConfigureAwait(false)
                };
            }

            string? fileHash = snapshot.FileTrace?.Sha256.ToLowerInvariant();
            string? pixelHash = fileHash == null && bitmap != null
                ? ImageViewerPixelAccess.ComputePixelBufferSha256(bitmap, cancellationToken) : null;
            string? sourceName = string.IsNullOrWhiteSpace(snapshot.Context.SourcePath)
                ? null : Path.GetFileName(snapshot.Context.SourcePath);
            if (pixelHash != null)
            {
                sourceName = sourceName == null ? "内存图像（像素缓冲区）" : $"{sourceName}（像素缓冲区）";
            }

            string report = BuildReport(
                snapshot.Rois,
                bitmap,
                snapshot.PixelSize,
                snapshot.PhysicalUnit,
                snapshot.Calibration,
                sourceName: sourceName,
                sourceSha256: fileHash ?? pixelHash);
            report += $"\n\n<!-- exportBatchId: {exportBatchId} -->\n";
            string reportHash = ImageViewerExportArtifactSet.ComputeSha256Utf8Text(report);
            cancellationToken.ThrowIfCancellationRequested();
            string metadata = BuildMetadataJson(snapshot, bitmap, fileHash, pixelHash, csvHash, exportBatchId, reportHash);

            await ImageViewerExportArtifactSet.WriteAllTextAsync(
                new Dictionary<string, string>
                {
                    [filePath] = csv,
                    [metadataPath] = metadata,
                    [reportPath] = report
                },
                cancellationToken).ConfigureAwait(false);
        }

        // Geometry-only exports may explicitly reference a file even though no bitmap was analyzed.
        private static async Task<ImageViewerImageDecoder.ImageFileTrace?> ReadReferencedFileTraceAsync(string? path, CancellationToken cancellationToken)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using FileStream stream = File.OpenRead(path);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return ImageViewerImageDecoder.ReadFileTrace(stream, Convert.ToHexString(hash));
        }

        private static string BuildMetadataJson(
            ExportSnapshot snapshot,
            BitmapSource? bitmap,
            string? fileHash,
            string? pixelHash,
            string csvHash,
            string exportBatchId,
            string reportHash)
        {
            var (rois, _, pixelSize, physicalUnit, calibration, exportContext, roiDocument, fileTrace) = snapshot;

            var metadata = new Dictionary<string, object?>
            {
                ["exportFormatVersion"] = 2,
                ["exportBatchId"] = exportBatchId,
                ["artifactSet"] = new Dictionary<string, object?>
                {
                    ["batchId"] = exportBatchId,
                    ["csvSha256"] = csvHash,
                    ["reportSha256"] = reportHash
                },
                ["exportedAtUtc"] = DateTimeOffset.UtcNow,
                ["applicationVersion"] = typeof(RoiAnalysisExportService).Assembly.GetName().Version?.ToString() ?? "unknown",
                ["analysisAlgorithm"] = FittingAlgorithmMetadata.Version,
                ["fitting"] = new Dictionary<string, object?>
                {
                    ["ellipseAlgorithm"] = FittingAlgorithmMetadata.Ellipse,
                    ["caliperEdgeAlgorithm"] = FittingAlgorithmMetadata.CaliperEdge,
                    ["lineFit"] = FittingAlgorithmMetadata.LineFit,
                    ["lineGeometry"] = FittingAlgorithmMetadata.LineGeometry,
                    ["ellipseLoss"] = RobustFitLoss.Tukey.ToString(),
                    ["ellipseMaxIterations"] = EllipseFitOptions.Default.MaxIterations,
                    ["ellipseMaxRansacSamples"] = EllipseFitOptions.Default.MaxRansacSamples,
                    ["ellipseRandomSeed"] = EllipseFitOptions.Default.RandomSeed,
                    ["fittedEllipses"] = rois.OfType<FittedEllipseRoi>().Where(ellipse => ellipse.HasFitEvidence).Select(ellipse => new Dictionary<string, object?>
                    {
                        ["label"] = ellipse.Label,
                        ["sourcePointCount"] = ellipse.SourcePointCount,
                        ["inlierCount"] = ellipse.FitInlierCount,
                        ["outlierCount"] = ellipse.FitOutlierCount,
                        ["residualRms"] = ellipse.FitResidualRms,
                        ["residualMedian"] = ellipse.FitResidualMedian,
                        ["residualMax"] = ellipse.FitResidualMax,
                        ["noiseScale"] = ellipse.FitNoiseScale,
                        ["aspectRatio"] = ellipse.FitAspectRatio,
                        ["algorithm"] = ellipse.FitAlgorithm
                    }).ToList()
                },
                ["qualityThresholds"] = exportContext?.QualityProfile ?? ImageAnalysisQualityProfile.Default,
                ["result"] = new Dictionary<string, object?>
                {
                    ["csvSha256"] = csvHash,
                    ["inspectionResults"] = rois.Select((roi, index) =>
                    {
                        RoiInspectionResult inspection = RoiInspectionEvaluator.Evaluate(roi, pixelSize, calibration);
                        return new Dictionary<string, object?>
                        {
                            ["index"] = index,
                            ["type"] = roi.RoiTypeName,
                            ["label"] = roi.Label,
                            ["detectionStatus"] = inspection.DetectionStatus.ToString(),
                            ["specificationStatus"] = inspection.SpecificationStatus.ToString(),
                            ["measuredValue"] = inspection.MeasuredValue,
                            ["nominalValue"] = inspection.NominalValue,
                            ["tolerancePlus"] = inspection.TolerancePlus,
                            ["toleranceMinus"] = inspection.ToleranceMinus,
                            ["physicalUnit"] = string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit,
                            ["distortionAudit"] = BuildDistortionMetadata(inspection.DistortionAudit)
                        };
                    }).ToList()
                },
                ["source"] = new Dictionary<string, object?>
                {
                    ["path"] = exportContext?.SourcePath,
                    ["sha256"] = fileHash,
                    ["lengthBytes"] = fileTrace?.Length,
                    ["lastWriteTimeUtc"] = fileTrace?.LastWriteTimeUtc,
                    ["pixelWidth"] = bitmap?.PixelWidth,
                    ["pixelHeight"] = bitmap?.PixelHeight,
                    ["pixelBufferSha256"] = pixelHash,
                    ["pixelBufferFormat"] = pixelHash == null ? null : bitmap?.Format.ToString()
                },
                ["calibration"] = new Dictionary<string, object?>
                {
                    ["pixelSize"] = pixelSize,
                    ["physicalUnit"] = string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit,
                    ["camera"] = calibration
                },
                ["roiCount"] = rois.Count,
                ["roiTypes"] = rois
                    .GroupBy(roi => roi.RoiTypeName)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
                // 过程能力按"同一量名"聚合当前图像上的重复特征，与报告中的 SPC 章节同源。
                ["processCapability"] = SpcReportFormatter.BuildMetadata(
                    SpcMeasurementSeriesBuilder.BuildFromRois(rois, pixelSize, physicalUnit, calibration)),
                ["roiDocument"] = roiDocument == null ? null
                    : JsonSerializer.SerializeToElement(roiDocument, ImageViewerCoreJsonSerializationContext.Default.RoiDocument),
                ["renderSettings"] = exportContext?.RenderSettings
            };

            return JsonSerializer.Serialize(metadata, MetadataJsonOptions);
        }

        public static string BuildCsv(IEnumerable<RoiBase> rois, BitmapSource? bitmap, double pixelSize, string? physicalUnit, CameraCalibration? calibration = null)
            => BuildCsv(rois, bitmap, pixelSize, physicalUnit, calibration, CancellationToken.None);

        private static string BuildCsv(IEnumerable<RoiBase> rois, BitmapSource? bitmap, double pixelSize, string? physicalUnit, CameraCalibration? calibration, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(rois);
            ImageViewerValidation.ValidatePixelSize(pixelSize);

            string unit = string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit;
            var builder = new StringBuilder();
            builder.AppendLine("Type,Label,Metric1,Metric2,Metric3,Mean,Min,Max,StdDev,PixelCount,DetectionStatus,SpecificationStatus,MeasuredValue,NominalValue,TolerancePlus,ToleranceMinus,PhysicalUnit");

            foreach (var roi in rois)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RoiInspectionResult inspection = RoiInspectionEvaluator.Evaluate(roi, pixelSize, calibration);
                var metrics = GetAuditedMetrics(roi, pixelSize, unit, calibration, inspection);
                string mean = string.Empty;
                string min = string.Empty;
                string max = string.Empty;
                string stddev = string.Empty;
                string pixelCount = string.Empty;

                if (bitmap != null && ImageAnalysisService.TryCalculateStatistics(bitmap, roi, out RoiStatistics statistics))
                {
                    mean = statistics.Mean.ToString("F2", CultureInfo.InvariantCulture);
                    min = statistics.Min.ToString(CultureInfo.InvariantCulture);
                    max = statistics.Max.ToString(CultureInfo.InvariantCulture);
                    stddev = statistics.StandardDeviation.ToString("F2", CultureInfo.InvariantCulture);
                    pixelCount = statistics.PixelCount.ToString(CultureInfo.InvariantCulture);
                }

                builder.AppendLine(string.Join(",",
                    Escape(roi.RoiTypeName),
                    Escape(roi.Label),
                    Escape(metrics.Metric1),
                    Escape(metrics.Metric2),
                    Escape(metrics.Metric3),
                    mean,
                    min,
                    max,
                    stddev,
                    pixelCount,
                    Escape(inspection.DetectionStatus.ToString()),
                    Escape(inspection.SpecificationStatus.ToString()),
                    FormatNullable(inspection.MeasuredValue),
                    FormatNullable(inspection.NominalValue),
                    FormatNullable(inspection.TolerancePlus),
                    FormatNullable(inspection.ToleranceMinus),
                    Escape(DescribeUnit(roi, unit))));
            }

            return builder.ToString();
        }

        private static (string Metric1, string Metric2, string Metric3) GetMetrics(RoiBase roi, double pixelSize, string unit, CameraCalibration? calibration)
        {
            double correction = RoiCalibrationHelper.GetLengthCorrection(roi, calibration);
            double areaCorrection = RoiCalibrationHelper.GetAreaCorrection(roi, calibration);
            return roi switch
            {
                RotatedRect rect => ($"Width={rect.Width * correction * pixelSize:F2} {unit}", $"Height={rect.Height * correction * pixelSize:F2} {unit}", $"Angle={rect.Angle:F1}°"),
                FittedEllipseRoi fittedEllipse when fittedEllipse.HasFitEvidence => ($"RadiusX={fittedEllipse.RadiusX * correction * pixelSize:F2} {unit}", $"RadiusY={fittedEllipse.RadiusY * correction * pixelSize:F2} {unit}", $"Angle={fittedEllipse.Angle:F1}°;RMS={fittedEllipse.FitResidualRms * correction * pixelSize:F3};Inliers={fittedEllipse.FitInlierCount}"),
                EllipseRoi ellipse => ($"RadiusX={ellipse.RadiusX * correction * pixelSize:F2} {unit}", $"RadiusY={ellipse.RadiusY * correction * pixelSize:F2} {unit}", $"Angle={ellipse.Angle:F1}°"),
                CaliperMeasureRoi caliper => GetDualEdgeCaliperMetrics(caliper, correction, pixelSize, unit),
                LineCaliperMeasureRoi lineCaliper => (
                    $"Length={GeometryUtils.Distance(lineCaliper.MeasurementP1.ToWpfPoint(), lineCaliper.MeasurementP2.ToWpfPoint()) * correction * pixelSize:F2} {unit}",
                    $"Angle={lineCaliper.AngleDegrees:F2}°",
                    $"Confidence={lineCaliper.Confidence:F3};RMS={lineCaliper.ResidualRms * correction:F3};Valid={lineCaliper.ValidCaliperCount}"),
                ArcCaliperMeasureRoi arcCaliper => (
                    $"Radius={arcCaliper.MeasurementRadius * correction * pixelSize:F2} {unit}",
                    $"Arc={arcCaliper.SweepAngle:F1}°",
                    $"Confidence={arcCaliper.Confidence:F3};RMS={arcCaliper.ResidualRms * correction:F3};Valid={arcCaliper.ValidCaliperCount}"),
                CircularCaliperMeasureRoi circular => (
                    $"Radius={circular.MeasurementRadius * correction * pixelSize:F2} {unit}",
                    $"Quality={circular.QualityStatus}",
                    $"QualityReason={circular.QualityReason};Confidence={circular.Confidence:F3};Coverage={circular.QualityAngularCoverageDegrees:F1};RMS={circular.ResidualRms:F3}"),
                CircleRoi circle => ($"Radius={circle.Radius * correction * pixelSize:F2} {unit}", string.Empty, string.Empty),
                PolygonRoi polygon when polygon.IsClosed && polygon.Points.Count >= 3 => ($"Area={GeometryUtils.PolygonArea(polygon.Points.ToWpfPointArray()) * areaCorrection * pixelSize * pixelSize:F2} {unit}²", $"Perimeter={GeometryUtils.PolygonPerimeter(polygon.Points.ToWpfPointArray()) * correction * pixelSize:F2} {unit}", $"Vertices={polygon.Points.Count};Closed=true"),
                PolygonRoi polygon => ($"Perimeter={GeometryUtils.PolylineLength(polygon.Points.ToWpfPointArray()) * correction * pixelSize:F2} {unit}", $"Vertices={polygon.Points.Count}", "Closed=false"),
                PolylineRoi polyline => ($"Length={GeometryUtils.PolylineLength(polyline.Points.ToWpfPointArray()) * correction * pixelSize:F2} {unit}", $"Segments={Math.Max(0, polyline.Points.Count - 1)}", $"Freehand={polyline.IsFreehand}"),
                PointAnnotationRoi point => ($"X={point.Position.X:F1}", $"Y={point.Position.Y:F1}", string.Empty),
                PointCoordinateMeasureRoi pointCoordinate => ($"X={pointCoordinate.Position.X:F1}", $"Y={pointCoordinate.Position.Y:F1}", pointCoordinate.IsEdgeSnapped ? $"EdgeConfidence={pointCoordinate.EdgeConfidence * 100:F1}%" : string.Empty),
                TextAnnotationRoi text => ($"X={text.Position.X:F1}", $"Y={text.Position.Y:F1}", string.Empty),
                LineMeasureRoi line => ($"Length={GeometryUtils.Distance(line.P1.ToWpfPoint(), line.P2.ToWpfPoint()) * correction * pixelSize:F2} {unit}", $"dX={(line.P2.X - line.P1.X) * correction * pixelSize:F2} {unit}", $"dY={(line.P2.Y - line.P1.Y) * correction * pixelSize:F2} {unit}"),
                AngleMeasureRoi angle => ($"Angle={GeometryUtils.SmallestAngle(angle.P1.ToWpfPoint(), angle.Vertex.ToWpfPoint(), angle.P2.ToWpfPoint()):F1}°", $"Leg1={GeometryUtils.Distance(angle.P1.ToWpfPoint(), angle.Vertex.ToWpfPoint()) * correction * pixelSize:F2} {unit}", $"Leg2={GeometryUtils.Distance(angle.Vertex.ToWpfPoint(), angle.P2.ToWpfPoint()) * correction * pixelSize:F2} {unit}"),
                CenterDistanceMeasureRoi centerDistance => ($"CenterDistance={centerDistance.CenterDistance * correction * pixelSize:F2} {unit}", $"C1=({centerDistance.Center1.X:F1},{centerDistance.Center1.Y:F1})", $"C2=({centerDistance.Center2.X:F1},{centerDistance.Center2.Y:F1})"),
                ThreePointCircleMeasureRoi threePointCircle when threePointCircle.IsValid => ($"Radius={threePointCircle.Radius * correction * pixelSize:F2} {unit}", $"Center=({threePointCircle.Center.X:F1},{threePointCircle.Center.Y:F1})", "Points=3"),
                _ => (string.Empty, string.Empty, string.Empty)
            };
        }

        private static (string Metric1, string Metric2, string Metric3) GetAuditedMetrics(
            RoiBase roi, double pixelSize, string unit, CameraCalibration? calibration, RoiInspectionResult inspection)
            => !RoiCalibrationHelper.CanUseMeasurements(calibration, inspection.DistortionAudit)
                ? (string.Empty, string.Empty, string.Empty)
                : GetMetrics(roi, pixelSize, unit, calibration);

        internal static Dictionary<string, object?>? BuildDistortionMetadata(DistortionAudit? audit)
            => audit is not { HasPoints: true } ? null : new()
            {
                ["pointCount"] = audit.PointCount,
                ["nonConvergedPointCount"] = audit.NonConvergedPointCount,
                ["maximumResidualPixels"] = double.IsFinite(audit.MaximumResidualPixels) ? audit.MaximumResidualPixels : null,
                ["maximumUncertaintyPixels"] = double.IsFinite(audit.MaximumUncertaintyPixels) ? audit.MaximumUncertaintyPixels : null
            };

        private static (string Metric1, string Metric2, string Metric3) GetDualEdgeCaliperMetrics(CaliperMeasureRoi caliper, double correction, double pixelSize, string unit)
        {
            CaliperWidthMeasurementResult width = caliper.WidthMeasurement;
            string requested = $"Requested={GeometryUtils.Distance(caliper.P1.ToWpfPoint(), caliper.P2.ToWpfPoint()) * correction * pixelSize:F2} {unit}";
            string detection = width.IsMeasured
                ? $"Center={width.CenterDistance * correction * pixelSize:F2} {unit};Mean={width.Mean * correction * pixelSize:F2} {unit};Median={width.Median * correction * pixelSize:F2} {unit}"
                : "Detected=n/a";
            string spread = width.IsMeasured
                ? $"Min={width.Minimum * correction * pixelSize:F2} {unit};Max={width.Maximum * correction * pixelSize:F2} {unit};Range={width.Range * correction * pixelSize:F2} {unit};StdDev={width.StandardDeviation * correction * pixelSize:F3} {unit};Samples={width.SampleCount}"
                : $"Valid={caliper.ValidCaliperCount};Confidence={caliper.Confidence:F3};RMS={Math.Max(caliper.Edge1ResidualRms, caliper.Edge2ResidualRms) * correction:F3}";
            return (requested, detection, spread);
        }

        // Extract all bitmap-dependent data before the next input is decoded.
        internal static RoiAnalysisBatchCsvRow CaptureBatchRow(
            string sourcePath, string status, string? error, RoiBase roi, BitmapSource? bitmap,
            double pixelSize, string? physicalUnit, CameraCalibration? calibration, int sourceIndex, string measurementItemKey)
        {
            string unit = string.IsNullOrWhiteSpace(physicalUnit) ? "px" : physicalUnit;
            RoiStatistics? statistics = bitmap != null && ImageAnalysisService.TryCalculateStatistics(bitmap, roi, out RoiStatistics result)
                ? result
                : null;
            RoiInspectionResult inspection = bitmap == null
                ? RoiInspectionEvaluator.EvaluateInputFailure(roi)
                : RoiInspectionEvaluator.Evaluate(roi, pixelSize, calibration);
            return new RoiAnalysisBatchCsvRow(
                sourcePath, status, error, roi.RoiTypeName, roi.Label, DescribeQuantity(roi), DescribeUnit(roi, unit), unit,
                GetAuditedMetrics(roi, pixelSize, unit, calibration, inspection), statistics, inspection, ToToleranceInput(roi.Tolerance),
                sourceIndex, measurementItemKey);
        }

        internal static string BuildBatchCsv(IEnumerable<RoiAnalysisBatchCsvRow> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);

            var builder = new StringBuilder();
            builder.AppendLine("Source,Status,Error,Type,Label,Metric1,Metric2,Metric3,Mean,Min,Max,StdDev,PixelCount,DetectionStatus,SpecificationStatus,MeasuredValue,NominalValue,TolerancePlus,ToleranceMinus,PhysicalUnit,SourceIndex,MeasurementItemKey");
            foreach (RoiAnalysisBatchCsvRow row in rows)
            {
                var metrics = row.Metrics;
                string mean = string.Empty;
                string min = string.Empty;
                string max = string.Empty;
                string stddev = string.Empty;
                string pixelCount = string.Empty;
                if (row.Statistics is { } statistics)
                {
                    mean = statistics.Mean.ToString("F2", CultureInfo.InvariantCulture);
                    min = statistics.Min.ToString(CultureInfo.InvariantCulture);
                    max = statistics.Max.ToString(CultureInfo.InvariantCulture);
                    stddev = statistics.StandardDeviation.ToString("F2", CultureInfo.InvariantCulture);
                    pixelCount = statistics.PixelCount.ToString(CultureInfo.InvariantCulture);
                }

                RoiInspectionResult inspection = row.Inspection;

                builder.AppendLine(string.Join(",",
                    Escape(row.SourcePath),
                    Escape(row.Status),
                    Escape(row.Error),
                    Escape(row.RoiType),
                    Escape(row.RoiLabel),
                    Escape(metrics.Metric1),
                    Escape(metrics.Metric2),
                    Escape(metrics.Metric3),
                    mean,
                    min,
                    max,
                    stddev,
                    pixelCount,
                    Escape(inspection.DetectionStatus.ToString()),
                    Escape(inspection.SpecificationStatus.ToString()),
                    FormatNullable(inspection.MeasuredValue),
                    FormatNullable(inspection.NominalValue),
                    FormatNullable(inspection.TolerancePlus),
                    FormatNullable(inspection.ToleranceMinus),
                    Escape(row.PhysicalUnit),
                    row.SourceIndex.ToString(CultureInfo.InvariantCulture),
                    Escape(row.MeasurementItemKey)));
            }

            return builder.ToString();
        }

        private static string Escape(string? value)
        {
            return $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";
        }

        private static string FormatNullable(double? value)
        {
            return value is { } number && double.IsFinite(number)
                ? number.ToString("G17", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private sealed record ExportSnapshot(
            IReadOnlyList<RoiBase> Rois,
            BitmapSource? Bitmap,
            double PixelSize,
            string? PhysicalUnit,
            CameraCalibration? Calibration,
            RoiAnalysisExportContext? Context,
            RoiDocument? RoiDocument,
            ImageViewerImageDecoder.ImageFileTrace? FileTrace);
    }
}
