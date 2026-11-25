using VisionStudio.Engine.Nodes;

namespace VisionStudio.Api;

public sealed record CalibrationWizardCreateRequest(
    string Pattern = "chessboard",
    int Columns = 7,
    int Rows = 6,
    double SpacingMm = 10,
    double MinBlurVariance = 40,
    double MinCoverage = 0.25,
    double MaxGridError = 2,
    double SaddleThreshold = 10,
    int? ImageWidth = null,
    int? ImageHeight = null);

public sealed record CalibrationWizardAddViewRequest(
    string? ViewJson = null,
    string? CalibrationDataJson = null,
    string? Label = null,
    bool? Enable = null,
    double? BlurVariance = null);

public sealed record CalibrationWizardSetEnabledRequest(bool Enabled);

public sealed record CalibrationWizardSolveRequest(
    double MaxViewRms = 0,
    double MaxRms = 0,
    double MaxPoseViewRms = 0,
    string CameraModel = "pinhole",
    double TelecentricMagnification = 0.2,
    double TelecentricPixelSizeUm = 3.45);

public sealed record CalibrationWizardPreviewRequest(string CameraId, bool AddIfAccepted = false, string? Label = null);

public sealed record CalibrationWizardImportRequest(string DocumentJson);
public sealed record CreateCameraIntrinsicAssetFromSessionRequest(string SessionId, string Id, string Name, string? Description = null, string? Note = null);
public sealed record SaveCameraIntrinsicVersionFromSessionRequest(string SessionId, string? Note = null);

public sealed record CalibrationWizardDisableOutliersRequest(int[]? ViewIndexes = null, bool UseSolveOutlier = true);

public sealed record CalibrationHandEyeSolveRequest(
    string SamplesJson,
    string Mode = "eyeInHand",
    double MaxTranslationError = 0,
    double MaxAngleErrorDeg = 0,
    string SourceFrame = "Camera",
    string TargetFrame = "",
    string Unit = "mm",
    string SourceUnit = "mm",
    double AngularWeight = 50,
    double VisionScale = 1,
    string RobustLoss = "none");

public sealed record HandEyeWizardSessionDocument(
    string FormatVersion,
    string SessionId,
    string Name,
    string UpdatedAt,
    string SamplesJson,
    string Mode,
    double AngularWeight,
    double VisionScale,
    string RobustLoss,
    double MaxTranslationError,
    double MaxAngleErrorDeg,
    string SourceFrame,
    string TargetFrame,
    string Unit,
    string SourceUnit,
    string? LastSolveSummaryJson)
{
    public const string CurrentVersion = "handeye-wizard-v1";
}

public sealed record HandEyeWizardSaveRequest(
    string? SessionId,
    string Name,
    string SamplesJson,
    string Mode = "eyeInHand",
    double AngularWeight = 50,
    double VisionScale = 1,
    string RobustLoss = "none",
    double MaxTranslationError = 0,
    double MaxAngleErrorDeg = 0,
    string SourceFrame = "Camera",
    string TargetFrame = "",
    string Unit = "mm",
    string SourceUnit = "mm",
    string? LastSolveSummaryJson = null);

public sealed record CreateHandEyeAssetFromSessionRequest(string SessionId, string Id, string Name, string? Description = null, string? Note = null);
public sealed record SaveHandEyeVersionFromSessionRequest(string SessionId, string? Note = null);

public sealed record CalibrationHandEye3DSolveRequest(
    string SamplesJson,
    string Mode = "eyeInHand",
    string Method = "DANIILIDIS",
    double MaxTranslationError = 0,
    double MaxRotationErrorDeg = 0,
    double RotationWeight = 50,
    string SourceFrame = "Camera",
    string TargetFrame = "",
    string Unit = "mm",
    string OutlierRejection = "none",
    string CrossCheckMethod = "TSAI",
    double MinTranslationSpan = 0);

public sealed record HandEye3DWizardSessionDocument(
    string FormatVersion,
    string SessionId,
    string Name,
    string UpdatedAt,
    string SamplesJson,
    string Mode,
    string Method,
    double RotationWeight,
    string OutlierRejection,
    string CrossCheckMethod,
    double MaxTranslationError,
    double MaxRotationErrorDeg,
    double MinTranslationSpan,
    string SourceFrame,
    string TargetFrame,
    string Unit,
    string? LastSolveSummaryJson)
{
    public const string CurrentVersion = "handeye3d-wizard-v1";
}

public sealed record HandEye3DWizardSaveRequest(
    string? SessionId,
    string Name,
    string SamplesJson,
    string Mode = "eyeInHand",
    string Method = "DANIILIDIS",
    double RotationWeight = 50,
    string OutlierRejection = "none",
    string CrossCheckMethod = "TSAI",
    double MaxTranslationError = 0,
    double MaxRotationErrorDeg = 0,
    double MinTranslationSpan = 0,
    string SourceFrame = "Camera",
    string TargetFrame = "",
    string Unit = "mm",
    string? LastSolveSummaryJson = null);

public sealed record CreateHandEye3DAssetFromSessionRequest(string SessionId, string Id, string Name, string? Description = null, string? Note = null);
public sealed record SaveHandEye3DVersionFromSessionRequest(string SessionId, string? Note = null);

/// <summary>
/// HTTP-facing guided calibration wizards: intrinsics session store (memory + disk),
/// board detect from upload/camera preview, solve, export/import, and planar hand-eye.
/// </summary>
public sealed record CalibrationDualCameraSolveRequest(
    string PairsJson,
    string InputMode = "poses",
    string Method = "average",
    double MaxTranslationError = 0,
    double MaxRotationErrorDeg = 0,
    double RotationWeight = 50,
    string SourceFrame = "CameraA",
    string TargetFrame = "CameraB",
    string Unit = "mm",
    int ImageWidth = 0,
    int ImageHeight = 0);

public sealed record CalibrationWizardSolvers(
    CalibrationDetectBoardNode DetectBoard,
    CameraIntrinsicCalibrationNode Intrinsics,
    PlanarHandEyeCalibrationNode HandEye,
    HandEye3DCalibrationNode HandEye3D,
    DualCameraExtrinsicsNode DualCamera);

public sealed record CalibrationWizardAssetStores(
    CameraIntrinsicAssetStore Intrinsics,
    HandEyeCalibrationAssetStore? HandEye = null,
    HandEye3DCalibrationAssetStore? HandEye3D = null);
