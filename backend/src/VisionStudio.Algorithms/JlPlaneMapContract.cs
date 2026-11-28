using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

/// <summary>A serializable companion contract binds both coordinate images to one calibrated map.</summary>
internal sealed record JlPlaneMapContract(int Version, string Producer, int SourceWidth, int SourceHeight,
    int Width, int Height, string Unit, double PixelSize, string CalibrationFingerprint, string RowsFingerprint, string ColumnsFingerprint,
    string? CalibrationVersion = null, string? SourceFrame = null)
{
    internal static string Create(string producer, int sourceWidth, int sourceHeight, string unit, double pixelSize,
        string calibration, string pose, Mat rows, Mat columns, string? calibrationVersion = null, string? sourceFrame = null) => JsonSerializer.Serialize(new JlPlaneMapContract(
            1, producer, sourceWidth, sourceHeight, rows.Width, rows.Height, unit, pixelSize,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(calibration + "\n" + pose))), Fingerprint(rows), Fingerprint(columns),
            calibrationVersion, sourceFrame));

    internal static string ComputeCalibrationFingerprint(string calibration) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(calibration)));

    internal static JlPlaneMapContract Validate(string json, Mat source, Mat rows, Mat columns,
        string? currentSourceFrame = null, string? currentCalibrationFingerprint = null, string? currentCalibrationVersion = null)
    {
        JlPlaneMapContract? info;
        try { info = JsonSerializer.Deserialize<JlPlaneMapContract>(json); }
        catch (JsonException ex) { throw new InvalidOperationException("映射描述无效；请连接同一映射生成节点的 mapInfo。", ex); }
        if (info is null || info.Version != 1 || string.IsNullOrWhiteSpace(info.Producer) ||
            info.Unit is not ("m" or "cm" or "mm" or "um") || !double.IsFinite(info.PixelSize) || info.PixelSize <= 0 ||
            string.IsNullOrWhiteSpace(info.CalibrationFingerprint))
            throw new InvalidOperationException("映射描述缺失或版本无效；请重新生成映射。");
        if (source.Width != info.SourceWidth || source.Height != info.SourceHeight)
            throw new InvalidOperationException($"mapInfo source-size mismatch: expected {info.SourceWidth}x{info.SourceHeight}, actual {source.Width}x{source.Height}; regenerate the map for this camera resolution.");
        if (rows.Type() != MatType.CV_32FC1 || columns.Type() != MatType.CV_32FC1)
            throw new InvalidOperationException($"mapInfo data-type mismatch: expected CV_32FC1/CV_32FC1, actual {rows.Type()}/{columns.Type()}.");
        if (rows.Width != info.Width || rows.Height != info.Height || columns.Width != info.Width || columns.Height != info.Height)
            throw new InvalidOperationException($"mapInfo map-size mismatch: expected {info.Width}x{info.Height}, actual rows {rows.Width}x{rows.Height}, columns {columns.Width}x{columns.Height}.");
        if (currentSourceFrame is not null && !string.Equals(currentSourceFrame, info.SourceFrame, StringComparison.Ordinal))
            throw new InvalidOperationException($"mapInfo source-frame mismatch: expected '{info.SourceFrame ?? "missing"}', actual '{currentSourceFrame}'.");
        if (currentCalibrationFingerprint is not null && !string.Equals(currentCalibrationFingerprint, info.CalibrationFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"mapInfo calibration-version/fingerprint mismatch: expected '{info.CalibrationFingerprint}', actual '{currentCalibrationFingerprint}'.");
        if (currentCalibrationVersion is not null && info.CalibrationVersion is not null &&
            !string.Equals(currentCalibrationVersion, info.CalibrationVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"mapInfo calibration-version mismatch: expected '{info.CalibrationVersion}', actual '{currentCalibrationVersion}'.");
        if (Fingerprint(rows) != info.RowsFingerprint || Fingerprint(columns) != info.ColumnsFingerprint)
            throw new InvalidOperationException("映射行列图与描述不匹配，可能混用了两组映射或修改过坐标图；请配套连接 mapRows/mapColumns/mapInfo。");
        return info;
    }

    private static string Fingerprint(Mat mat)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var row = new byte[checked((int)(mat.Cols * mat.ElemSize()))];
        var rows = mat.Rows;
        for (var y = 0; y < rows; y++)
        {
            Marshal.Copy(mat.Ptr(y), row, 0, row.Length);
            hash.AppendData(row);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
