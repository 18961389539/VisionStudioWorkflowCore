// 标定视图/请求/模型（2026-11-25 从 Engine/Nodes/CalibrationAdvancedNodes.cs 下沉）。
// 三者此前定义在节点执行器文件顶部，被算法层（CalibrationIntrinsicsWizard、JLVisionCalibration）
// 逆向引用；它们是纯 DTO，与执行无关，因此归入契约程序集。

namespace VisionStudio.Abstractions;

public sealed record CameraCalibrationBoardInfo(
    string Kind,
    int Columns,
    int Rows,
    double SquareLengthMm,
    double? MarkerLengthMm = null,
    string? Dictionary = null,
    bool? LegacyPattern = null);

public sealed record CameraCalibrationView(
    List<List<double>> ObjectPoints,
    List<List<double>> ImagePoints,
    int? ImageWidth = null,
    int? ImageHeight = null,
    CameraCalibrationBoardInfo? Board = null);

public sealed record CameraCalibrationRequest(int ImageWidth, int ImageHeight, List<CameraCalibrationView> Views);

public sealed record CameraCalibrationModel(
    int ImageWidth,
    int ImageHeight,
    double[][] CameraMatrix,
    double[] DistCoeffs,
    double Rms,
    string? NativeCameraType = null,
    double[]? NativeCameraParameters = null,
    string? LensModel = null);
