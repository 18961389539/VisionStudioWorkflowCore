using ImageViewer.Models;

namespace ImageViewer.Services;

internal sealed record DetectionParameterChange(string PropertyName, string NameKey, string Before, string After, string ReasonKey);
internal sealed record DetectionParameterEvidence(double Contrast, double Noise, int SampleCount, double DominantPolarityRatio, string[] Notes);
internal sealed record DetectionParameterProposal(bool Success, string FailureKey, RoiBase? SuggestedRoi,
    DetectionParameterChange[] Changes, DetectionParameterEvidence Evidence);
internal sealed record DetectionTrial(bool Success, string StatusKey, double? Score, double? Residual,
    int ValidSamples, int TotalSamples, PointD[] Points, RoiBase? DetectedRoi);
internal sealed record DetectionRecommendationPreview(DetectionParameterProposal Proposal, DetectionTrial Before,
    DetectionTrial After, bool CanApply, string ApplyBlockedKey);
