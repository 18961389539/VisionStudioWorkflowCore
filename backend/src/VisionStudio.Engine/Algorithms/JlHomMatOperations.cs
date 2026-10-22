using JLVisionLib;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Algorithms;

public sealed class JlHomMatArgs
{
    public double Tx { get; init; }
    public double Ty { get; init; }
    public double Phi { get; init; }
    public double Theta { get; init; }
    public double Sx { get; init; } = 1;
    public double Sy { get; init; } = 1;
    public double Px { get; init; }
    public double Py { get; init; }
    public double Qx { get; init; } = 10;
    public double Qy { get; init; } = 10;
    public string Mode { get; init; } = string.Empty;
}

public sealed record JlHomMatResult(double Value, double[,]? Matrix, IReadOnlyList<(string Name, double Value)>? Extra = null);

public sealed record JlHomMatOperator(string Key, string Label, string Note, Func<JlHomMatArgs, JlHomMatResult> Run);

/// <summary>
/// JLVision 二维齐次变换算子表（<see cref="JlHomMat2D"/>）：所有矩阵都由节点参数构造，
/// 输出既给 3x3 矩阵也给平台 Transform2D，可直接接到 roi.transform 或机器人偏移换算。
/// </summary>
public static class JlHomMatOperations
{
    private static readonly Dictionary<string, JlHomMatOperator> Table = new(StringComparer.Ordinal)
    {
        ["identity"] = new("identity", "Identity", "无参数", a => From(Identity())),
        ["translate"] = new("translate", "Translate", "tx, ty", a =>
        {
            var m = Identity();
            return From(m.HomMat2dTranslate(a.Tx, a.Ty));
        }),
        ["translateLocal"] = new("translateLocal", "Translate (local)", "tx, ty", a =>
        {
            var m = Identity();
            return From(m.HomMat2dTranslateLocal(a.Tx, a.Ty));
        }),
        ["rotate"] = new("rotate", "Rotate about point", "phi, px, py", a =>
        {
            var m = Identity();
            return From(m.HomMat2dRotate(a.Phi, a.Px, a.Py));
        }),
        ["rotateLocal"] = new("rotateLocal", "Rotate (local)", "phi", a =>
        {
            var m = Identity();
            return From(m.HomMat2dRotateLocal(a.Phi));
        }),
        ["scale"] = new("scale", "Scale about point", "sx, sy, px, py", a =>
        {
            var m = Identity();
            return From(m.HomMat2dScale(a.Sx, a.Sy, a.Px, a.Py));
        }),
        ["scaleLocal"] = new("scaleLocal", "Scale (local)", "sx, sy", a =>
        {
            var m = Identity();
            return From(m.HomMat2dScaleLocal(a.Sx, a.Sy));
        }),
        ["slant"] = new("slant", "Slant", "theta, mode=axis(x|y), px, py", a =>
        {
            var m = Identity();
            return From(m.HomMat2dSlant(a.Theta, string.IsNullOrWhiteSpace(a.Mode) ? "x" : a.Mode, a.Px, a.Py));
        }),
        ["slantLocal"] = new("slantLocal", "Slant (local)", "theta, mode=axis(x|y)", a =>
        {
            var m = Identity();
            return From(m.HomMat2dSlantLocal(a.Theta, string.IsNullOrWhiteSpace(a.Mode) ? "x" : a.Mode));
        }),
        ["reflect"] = new("reflect", "Reflect about line", "px, py, qx, qy", a =>
        {
            var m = Identity();
            return From(m.HomMat2dReflect(a.Px, a.Py, a.Qx, a.Qy));
        }),
        ["reflectLocal"] = new("reflectLocal", "Reflect about point (local)", "px, py", a =>
        {
            var m = Identity();
            return From(m.HomMat2dReflectLocal(a.Px, a.Py));
        }),
        ["invert"] = new("invert", "Invert transform", "tx, ty, phi（先旋转再平移）", a =>
        {
            var m = Identity();
            var r = m.HomMat2dRotateLocal(a.Phi);
            var t = r.HomMat2dTranslate(a.Tx, a.Ty);
            return From(t.HomMat2dInvert());
        }),
        ["compose"] = new("compose", "Compose translate + rotate", "tx, ty, phi", a =>
        {
            var m = Identity();
            var t = m.HomMat2dTranslate(a.Tx, a.Ty);
            var r = t.HomMat2dRotateLocal(a.Phi);
            return From(r);
        }),
        ["determinant"] = new("determinant", "Determinant", "tx, ty, sx, sy", a =>
        {
            var m = Identity();
            var t = m.HomMat2dTranslate(a.Tx, a.Ty);
            var s = t.HomMat2dScaleLocal(a.Sx, a.Sy);
            return new(s.HomMat2dDeterminant(), null);
        }),
        ["toAffinePar"] = new("toAffinePar", "To affine parameters", "tx, ty, phi, sx, sy", a =>
        {
            var m = Identity();
            var t = m.HomMat2dTranslate(a.Tx, a.Ty);
            var r = t.HomMat2dRotateLocal(a.Phi);
            var s = r.HomMat2dScaleLocal(a.Sx, a.Sy);
            var sx = s.HomMat2dToAffinePar(out double sy, out double phi, out double theta, out double tx, out double ty);
            return new(sx, null,
            [
                ("sx", sx), ("sy", sy),
                ("phiDeg", phi * 180d / Math.PI), ("thetaDeg", theta * 180d / Math.PI),
                ("tx", tx), ("ty", ty)
            ]);
        }),
        ["transPoint"] = new("transPoint", "Transform a point", "tx, ty, phi, px, py", a =>
        {
            var m = Identity();
            var t = m.HomMat2dTranslate(a.Tx, a.Ty);
            var r = t.HomMat2dRotateLocal(a.Phi);
            var qx = r.AffineTransPoint2d(a.Px, a.Py, out double qy);
            return new(qx, null, [("row", qy), ("column", qx)]);
        }),
        ["transPixel"] = new("transPixel", "Transform a pixel", "tx, ty, phi, px, py", a =>
        {
            var m = Identity();
            var t = m.HomMat2dTranslate(a.Tx, a.Ty);
            var r = t.HomMat2dRotateLocal(a.Phi);
            r.AffineTransPixel(a.Py, a.Px, out double row, out double column);
            return new(row, null, [("row", row), ("column", column)]);
        }),

        // ── 矩阵组合与由点对估计的变换 ───────────────────────────────
        ["compose"] = new("compose", "Compose (right multiply)", "tx, ty, phi", a =>
        {
            var m = Identity();
            var t = m.HomMat2dTranslate(a.Tx, a.Ty);
            var r = t.HomMat2dRotateLocal(a.Phi);
            var right = Identity();
            return From(right.HomMat2dCompose(r));
        }),
        ["vectorToRigid"] = new("vectorToRigid", "Rigid transform from point pairs", "px,py → qx,qy（由点对估计）", a =>
            FromPointPairs(a, (m, px, py, qx, qy) => m.VectorToRigid(px, py, qx, qy))),
        ["vectorToSimilarity"] = new("vectorToSimilarity", "Similarity transform from point pairs", "px,py → qx,qy（由点对估计）", a =>
            FromPointPairs(a, (m, px, py, qx, qy) => m.VectorToSimilarity(px, py, qx, qy))),
        ["vectorToAniso"] = new("vectorToAniso", "Anisotropic transform from point pairs", "px,py → qx,qy（由点对估计）", a =>
            FromPointPairs(a, (m, px, py, qx, qy) => m.VectorToAniso(px, py, qx, qy))),
        ["vectorToHomMat2d"] = new("vectorToHomMat2d", "Affine transform from point pairs", "px,py → qx,qy（由点对估计）", a =>
            FromPointPairs(a, (m, px, py, qx, qy) => m.VectorToHomMat2d(px, py, qx, qy))),

        // ── 投影变换（数值输出，不产生矩阵）─────────────────────────
        ["projectiveTransPixel"] = new("projectiveTransPixel", "Projective transform a pixel", "tx, ty, phi, px, py", a =>
        {
            var m = Identity();
            var t = m.HomMat2dTranslate(a.Tx, a.Ty);
            var r = t.HomMat2dRotateLocal(a.Phi);
            r.ProjectiveTransPixel(a.Py, a.Px, out double row, out double column);
            return new(row, null, [("row", row), ("column", column)]);
        }),
        ["projectiveTransPoint2d"] = new("projectiveTransPoint2d", "Projective transform a point", "tx, ty, phi, px, py", a =>
        {
            var m = Identity();
            var t = m.HomMat2dTranslate(a.Tx, a.Ty);
            var r = t.HomMat2dRotateLocal(a.Phi);
            var qx = r.ProjectiveTransPoint2d(a.Px, a.Py, 1d, out double qy, out double qw);
            return new(qx, null, [("row", qy), ("column", qx), ("w", qw)]);
        })
    };

    public static IReadOnlyDictionary<string, JlHomMatOperator> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlHomMatOperator Resolve(string key)
        => Table.TryGetValue(key, out var op)
            ? op
            : throw new InvalidOperationException(
                $"Unknown JLVision hom-mat operator '{key}'. Known operators: {string.Join(", ", Keys)}.");

    private static JlHomMat2D Identity()
    {
        var matrix = new JlHomMat2D();
        matrix.HomMat2dIdentity();
        return matrix;
    }

    /// <summary>由三对点估计变换：参数只有单点，这里补齐成三点的退化对应（px..py → qx..qy 平移方向）。</summary>
    private static JlHomMatResult FromPointPairs(
        JlHomMatArgs a, Action<JlHomMat2D, JlTuple, JlTuple, JlTuple, JlTuple> build)
    {
        var matrix = new JlHomMat2D();
        matrix.HomMat2dIdentity();
        using var px = new JlTuple(new[] { a.Px, a.Px + 1, a.Px });
        using var py = new JlTuple(new[] { a.Py, a.Py, a.Py + 1 });
        using var qx = new JlTuple(new[] { a.Qx, a.Qx + 1, a.Qx });
        using var qy = new JlTuple(new[] { a.Qy, a.Qy, a.Qy + 1 });
        build(matrix, px, py, qx, qy);
        return From(matrix);
    }

    /// <summary>把仿射矩阵展开成 3x3（Qx = H·P，最后一行为 0,0,1），同时给出平台 Transform2D。</summary>
    private static JlHomMatResult From(JlHomMat2D matrix)
    {
        {
            var sx = matrix.HomMat2dToAffinePar(out double sy, out double phi, out double theta, out double tx, out double ty);
            var values = new double[3, 3];
            values[0, 0] = sx * Math.Cos(phi);
            values[0, 1] = -sy * Math.Sin(phi + theta);
            values[0, 2] = tx;
            values[1, 0] = sx * Math.Sin(phi);
            values[1, 1] = sy * Math.Cos(phi + theta);
            values[1, 2] = ty;
            values[2, 2] = 1d;
            return new JlHomMatResult(sx, values,
                [("sx", sx), ("sy", sy), ("phiDeg", phi * 180d / Math.PI), ("tx", tx), ("ty", ty)]);
        }
    }

    /// <summary>把 3x3 矩阵包装成平台 Transform2D。</summary>
    public static VisionTransform2D ToPlatform(double[,] values, string sourceFrame, string targetFrame)
        => new(sourceFrame, targetFrame, "px",
            values[0, 0], values[0, 1], values[0, 2],
            values[1, 0], values[1, 1], values[1, 2],
            values[2, 0], values[2, 1], values[2, 2]);
}
