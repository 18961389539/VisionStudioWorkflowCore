using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCvSharp;

namespace VisionStudio.Engine.Algorithms;

/// <summary>A serializable companion contract binds both coordinate images to one calibrated map.</summary>
internal sealed record JlPlaneMapContract(int Version, string Producer, int SourceWidth, int SourceHeight,
    int Width, int Height, string Unit, double PixelSize, string CalibrationFingerprint, string RowsFingerprint, string ColumnsFingerprint)
{
    internal static string Create(string producer, int sourceWidth, int sourceHeight, string unit, double pixelSize,
        string calibration, string pose, Mat rows, Mat columns) => JsonSerializer.Serialize(new JlPlaneMapContract(
            1, producer, sourceWidth, sourceHeight, rows.Width, rows.Height, unit, pixelSize,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(calibration + "\n" + pose))), Fingerprint(rows), Fingerprint(columns)));

    internal static JlPlaneMapContract Validate(string json, Mat source, Mat rows, Mat columns)
    {
        JlPlaneMapContract? info;
        try { info = JsonSerializer.Deserialize<JlPlaneMapContract>(json); }
        catch (JsonException ex) { throw new InvalidOperationException("映射描述无效；请连接同一映射生成节点的 mapInfo。", ex); }
        if (info is null || info.Version != 1 || string.IsNullOrWhiteSpace(info.Producer) ||
            info.Unit is not ("m" or "cm" or "mm" or "um") || !double.IsFinite(info.PixelSize) || info.PixelSize <= 0 ||
            string.IsNullOrWhiteSpace(info.CalibrationFingerprint))
            throw new InvalidOperationException("映射描述缺失或版本无效；请重新生成映射。");
        if (source.Width != info.SourceWidth || source.Height != info.SourceHeight)
            throw new InvalidOperationException("原图尺寸与映射生成时不一致；请使用相同相机分辨率并重新生成映射。");
        if (rows.Type() != MatType.CV_32FC1 || columns.Type() != MatType.CV_32FC1 ||
            rows.Width != info.Width || rows.Height != info.Height || columns.Width != info.Width || columns.Height != info.Height)
            throw new InvalidOperationException("映射行列图必须是与描述一致的同尺寸单通道实数图。");
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
