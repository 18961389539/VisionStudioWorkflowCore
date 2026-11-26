using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.Core.Analysis;
using ImageViewer.Models;
using ImageViewer.Utils;

namespace ImageViewer.Services;

/// <summary>Runs the real image detectors against isolated before/after ROI clones.</summary>
internal static class DetectionRecommendationPreviewService
{
    private const double MaximumRelativeScoreDrop = 0.10;
    private const double MaximumRelativeResidualIncrease = 0.10;
    private const double MaximumValidRatioDrop = 0.05;

    public static DetectionRecommendationPreview Create(
        BitmapSource bitmap,
        RoiBase roi,
        ImageAnalysisQualityProfile quality,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(roi);
        ArgumentNullException.ThrowIfNull(quality);
        quality.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        DetectionParameterProposal proposal = DetectionParameterRecommendationService.IsSupported(roi)
            ? DetectionParameterRecommendationService.Recommend(bitmap, roi.Clone())
            : new DetectionParameterProposal(
                false,
                "RecommendationUnsupportedRoi",
                null,
                [],
                new DetectionParameterEvidence(0, 0, 0, 0, []));
        cancellationToken.ThrowIfCancellationRequested();

        if (!proposal.Success || proposal.SuggestedRoi is null)
        {
            DetectionTrial skipped = FailedTrial(proposal.FailureKey);
            string proposalFailure = string.IsNullOrWhiteSpace(proposal.FailureKey)
                ? "RecommendationTrialFailed"
                : proposal.FailureKey;
            return new DetectionRecommendationPreview(proposal, skipped with { StatusKey = proposalFailure },
                skipped with { StatusKey = proposalFailure }, false, proposalFailure);
        }

        DetectionTrial before = RunTrial(bitmap, roi, quality, cancellationToken);
        DetectionTrial after = RunTrial(bitmap, proposal.SuggestedRoi, quality, cancellationToken);

        string blockedKey = GetApplyBlockedKey(roi, proposal, before, after);
        return new DetectionRecommendationPreview(proposal, before, after, string.IsNullOrEmpty(blockedKey), blockedKey);
    }

    private static string GetApplyBlockedKey(RoiBase originalRoi, DetectionParameterProposal proposal, DetectionTrial before, DetectionTrial after)
    {
        if (!HasEffectiveChanges(originalRoi, proposal))
        {
            return "RecommendationNoParameterChange";
        }

        if (!HasReliableEvidence(proposal.Evidence))
        {
            return "RecommendationEvidenceInsufficient";
        }

        if (!after.Success)
        {
            return after.StatusKey;
        }

        // A failed baseline may be a useful recovery case. If the baseline worked, however,
        // preserve its existing quality and reject recommendations that materially worsen it.
        if (before.Success && MateriallyWorse(before, after))
        {
            return "RecommendationCandidateWorsened";
        }

        return string.Empty;
    }

    private static bool HasEffectiveChanges(RoiBase originalRoi, DetectionParameterProposal proposal)
    {
        if (proposal.Changes.Length == 0)
        {
            return false;
        }

        foreach (DetectionParameterChange change in proposal.Changes)
        {
            System.Reflection.PropertyInfo? originalProperty = originalRoi.GetType().GetProperty(change.PropertyName);
            System.Reflection.PropertyInfo? candidateProperty = proposal.SuggestedRoi!.GetType().GetProperty(change.PropertyName);
            if (originalProperty is not null && candidateProperty is not null)
            {
                if (!Equals(originalProperty.GetValue(originalRoi), candidateProperty.GetValue(proposal.SuggestedRoi)))
                {
                    return true;
                }

                continue;
            }

            if (!string.Equals(change.Before, change.After, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasReliableEvidence(DetectionParameterEvidence evidence) =>
        evidence.SampleCount >= 3 && double.IsFinite(evidence.Contrast) && evidence.Contrast > 0 &&
        double.IsFinite(evidence.Noise) && evidence.Noise >= 0 &&
        double.IsFinite(evidence.DominantPolarityRatio) && evidence.DominantPolarityRatio >= 0;

    private static bool MateriallyWorse(DetectionTrial before, DetectionTrial after)
    {
        if (before.DetectedRoi is BlobAnalysisRoi beforeBlob && after.DetectedRoi is BlobAnalysisRoi afterBlob)
        {
            int beforeCount = beforeBlob.DetectedBlobs.Count;
            int afterCount = afterBlob.DetectedBlobs.Count;
            int beforeLargestArea = beforeBlob.DetectedBlobs.Count == 0 ? 0 : beforeBlob.DetectedBlobs.Max(blob => blob.Area);
            int afterLargestArea = afterBlob.DetectedBlobs.Count == 0 ? 0 : afterBlob.DetectedBlobs.Max(blob => blob.Area);
            return afterCount > Math.Max(beforeCount + 1, beforeCount * 2)
                || beforeLargestArea > 0 && (afterLargestArea < beforeLargestArea * 0.5 || afterLargestArea > beforeLargestArea * 2.0);
        }

        double beforeRatio = (double)before.ValidSamples / Math.Max(1, before.TotalSamples);
        double afterRatio = (double)after.ValidSamples / Math.Max(1, after.TotalSamples);
        if (afterRatio + MaximumValidRatioDrop < beforeRatio)
        {
            return true;
        }

        if (before.Score is double beforeScore && after.Score is double afterScore &&
            afterScore + 1e-9 < beforeScore * (1 - MaximumRelativeScoreDrop))
        {
            return true;
        }

        return before.Residual is double beforeResidual && after.Residual is double afterResidual &&
            afterResidual > beforeResidual * (1 + MaximumRelativeResidualIncrease) + 0.05;
    }

    private static DetectionTrial RunTrial(
        BitmapSource bitmap,
        RoiBase source,
        ImageAnalysisQualityProfile quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RoiBase candidate = source.Clone();
        CaliperRoiDetection.Clear(candidate);
        if (candidate is BlobAnalysisRoi blobCandidate)
        {
            blobCandidate.DetectedBlobs = [];
        }
        cancellationToken.ThrowIfCancellationRequested();

        switch (candidate)
        {
            case CaliperMeasureRoi dual:
                // The core detector calls EnsureCaliperRegion on dual-edge ROIs. That is
                // useful for an explicitly run measurement but would turn derived sample
                // geometry into a persistent user edit when this preview is accepted.
                PointD configuredCenter = dual.CaliperCenter;
                double configuredAngle = dual.CaliperAngleDegrees;
                double configuredRegionLength = dual.CaliperRegionLength;
                bool hadExplicitRegion = dual.HasExplicitCaliperRegion;
                bool detected = CaliperRoiDetection.TryDetectAndApply(bitmap, dual, out var dualResult, quality);
                RestoreDualGeometry(dual, configuredCenter, configuredAngle, configuredRegionLength, hadExplicitRegion);
                if (!detected)
                {
                    return FailedTrial("RecommendationTrialFailed", candidate);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new DetectionTrial(
                    true,
                    "RecommendationTrialSucceeded",
                    (dualResult.Edge1AverageScore + dualResult.Edge2AverageScore) / 2,
                    Math.Max(dualResult.Edge1ResidualRms, dualResult.Edge2ResidualRms),
                    dualResult.ValidCaliperCount,
                    dual.CaliperCount,
                    dualResult.Edge1Points.Concat(dualResult.Edge2Points).ToArray(),
                    candidate);

            case LineCaliperMeasureRoi line:
                if (!CaliperRoiDetection.TryDetectAndApply(bitmap, line, out var lineResult, quality))
                {
                    return FailedTrial("RecommendationTrialFailed", candidate);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new DetectionTrial(
                    true,
                    "RecommendationTrialSucceeded",
                    lineResult.AverageScore,
                    lineResult.ResidualRms,
                    lineResult.ValidCaliperCount,
                    line.CaliperCount,
                    lineResult.EdgePoints,
                    candidate);

            case CircularCaliperMeasureRoi circle:
                if (!CaliperRoiDetection.TryDetectAndApply(bitmap, circle, out var circleResult, quality))
                {
                    return FailedTrial("RecommendationTrialFailed", candidate);
                }

                cancellationToken.ThrowIfCancellationRequested();
                bool qualityAccepted = circle is ArcCaliperMeasureRoi || circle.QualityStatus != CircularCaliperQualityStatus.Failed;
                return new DetectionTrial(
                    qualityAccepted,
                    qualityAccepted ? "RecommendationTrialSucceeded" : "RecommendationQualityGateFailed",
                    circleResult.AverageScore,
                    circleResult.ResidualRms,
                    circleResult.ValidCaliperCount,
                    circle.CaliperCount,
                    circleResult.EdgePoints,
                    candidate);

            case BlobAnalysisRoi blob:
                cancellationToken.ThrowIfCancellationRequested();
                List<BlobFeature> blobs = BlobAnalysisService.DetectBlobs(
                    bitmap,
                    blob,
                    blob.UseOtsu,
                    blob.ManualThreshold,
                    blob.DetectDark,
                    blob.MinArea,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                blob.DetectedBlobs = blobs;
                cancellationToken.ThrowIfCancellationRequested();
                ImageContourExtractionResult contour = ImageContourExtractionService.Extract(bitmap, blob);
                cancellationToken.ThrowIfCancellationRequested();
                bool blobSuccess = blobs.Count > 0 && contour.Success && contour.OuterContour.Length >= 3;
                return new DetectionTrial(
                    blobSuccess,
                    blobSuccess ? "RecommendationTrialSucceeded" : "RecommendationTrialFailed",
                    null,
                    null,
                    blobSuccess ? 1 : 0,
                    1,
                    blobSuccess ? contour.OuterContour : [],
                    candidate);

            default:
                return FailedTrial("RecommendationTrialUnsupported", candidate);
        }
    }

    private static void RestoreDualGeometry(
        CaliperMeasureRoi roi,
        PointD center,
        double angleDegrees,
        double regionLength,
        bool hasExplicitRegion)
    {
        roi.CaliperCenter = center;
        roi.CaliperAngleDegrees = angleDegrees;
        roi.CaliperRegionLength = regionLength;
        roi.HasExplicitCaliperRegion = hasExplicitRegion;
    }

    private static DetectionTrial FailedTrial(string statusKey, RoiBase? detectedRoi = null) =>
        new(false, string.IsNullOrWhiteSpace(statusKey) ? "RecommendationTrialFailed" : statusKey, null, null, 0, 0, [], detectedRoi);
}
