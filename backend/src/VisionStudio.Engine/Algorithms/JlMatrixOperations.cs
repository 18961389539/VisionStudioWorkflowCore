using System.Text.Json;
using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

/// <summary>矩阵算子入参：A/B 两个矩阵（JSON 二维数组）+ 标量与模式。</summary>
public sealed class JlMatrixArgs
{
    public required JlMatrix A { get; init; }
    public JlMatrix? B { get; init; }
    public double Scalar { get; init; } = 1;
    public int Row { get; init; }
    public int Column { get; init; }
    public int Rows { get; init; } = 2;
    public int Columns { get; init; } = 2;
    public string Mode { get; init; } = string.Empty;
    public string Mode2 { get; init; } = string.Empty;
}

/// <summary>矩阵算子结果：主值 + 结果矩阵（可选）+ 具名分量。</summary>
public sealed record JlMatrixResult(double Value, double[,]? Matrix = null, IReadOnlyList<(string Name, double Value)>? Extra = null);

public sealed record JlMatrixOperator(string Key, string Label, string Note, Func<JlMatrixArgs, JlMatrixResult> Run, bool NeedsB = false);

/// <summary>
/// JLVision 矩阵算子表（<see cref="JlMatrix"/>）：线性代数能力，输入输出都用 JSON 二维数组，
/// 求解、对称特征值、正交分解等可用于标定与单应矩阵计算，矩阵 JSON 端口支持直接接线。
/// </summary>
public static class JlMatrixOperations
{
    private static readonly Dictionary<string, JlMatrixOperator> Table = new(StringComparer.Ordinal)
    {
        ["transpose"] = new("transpose", "Transpose", "无参数", a => From(a.A.TransposeMatrix())),
        ["invert"] = new("invert", "Invert", "mode=matrixType（默认 general）, scalar=epsilon", a =>
            From(a.A.InvertMatrix(Type(a.Mode), a.Scalar <= 0 ? 1e-6 : a.Scalar))),
        ["determinant"] = new("determinant", "Determinant", "mode=matrixType", a => new(a.A.DeterminantMatrix(Type(a.Mode)))),
        ["abs"] = new("abs", "Absolute values", "无参数", a => From(a.A.AbsMatrix())),
        ["sqrt"] = new("sqrt", "Element square root", "无参数", a => From(a.A.SqrtMatrix())),
        ["add"] = new("add", "Matrix addition", "矩阵 B", a => From(a.A.AddMatrix(a.B!)), NeedsB: true),
        ["sub"] = new("sub", "Matrix subtraction", "矩阵 B", a => From(a.A.SubMatrix(a.B!)), NeedsB: true),
        ["mult"] = new("mult", "Matrix multiplication", "矩阵 B, mode=multType（AB|ATB|ABT|ATBT）", a =>
            From(a.A.MultMatrix(a.B!, MultType(a.Mode))), NeedsB: true),
        ["multElement"] = new("multElement", "Element-wise multiplication", "矩阵 B", a => From(a.A.MultElementMatrix(a.B!)), NeedsB: true),
        ["divElement"] = new("divElement", "Element-wise division", "矩阵 B", a => From(a.A.DivElementMatrix(a.B!)), NeedsB: true),
        ["scale"] = new("scale", "Scale by scalar", "scalar", a => From(a.A.ScaleMatrix(a.Scalar))),
        ["pow"] = new("pow", "Matrix power", "scalar=幂次, mode=matrixType", a => From(a.A.PowMatrix(Type(a.Mode), a.Scalar))),
        ["powScalarElement"] = new("powScalarElement", "Element-wise power", "scalar=指数", a => From(a.A.PowScalarElementMatrix(a.Scalar))),
        ["sum"] = new("sum", "Sum", "mode=sumType（默认 columns）", a =>
            From(a.A.SumMatrix(string.IsNullOrWhiteSpace(a.Mode) ? "columns" : a.Mode))),
        ["max"] = new("max", "Maximum", "mode=maxType（默认 columns）", a =>
            From(a.A.MaxMatrix(string.IsNullOrWhiteSpace(a.Mode) ? "columns" : a.Mode))),
        ["min"] = new("min", "Minimum", "mode=minType（默认 columns）", a =>
            From(a.A.MinMatrix(string.IsNullOrWhiteSpace(a.Mode) ? "columns" : a.Mode))),
        ["mean"] = new("mean", "Mean", "mode=meanType（默认 columns）", a =>
            From(a.A.MeanMatrix(string.IsNullOrWhiteSpace(a.Mode) ? "columns" : a.Mode))),
        ["size"] = new("size", "Matrix size", "无参数", a =>
        {
            a.A.GetSizeMatrix(out int rows, out int columns);
            return new(rows, null, [("rows", rows), ("columns", columns)]);
        }),
        ["getValue"] = new("getValue", "Single element", "row, column", a => new(a.A.GetValueMatrix(a.Row, a.Column))),
        ["getSubMatrix"] = new("getSubMatrix", "Sub matrix", "row, column, rows, columns", a =>
            From(a.A.GetSubMatrix(a.Row, a.Column, Math.Max(1, a.Rows), Math.Max(1, a.Columns)))),
        ["getDiagonal"] = new("getDiagonal", "Diagonal", "rows=对角线序号", a => From(a.A.GetDiagonalMatrix(a.Rows))),
        ["repeat"] = new("repeat", "Repeat matrix", "rows, columns", a =>
            From(a.A.RepeatMatrix(Math.Max(1, a.Rows), Math.Max(1, a.Columns)))),
        ["solve"] = new("solve", "Solve linear system", "矩阵 B=右端项, mode=matrixLHSType, scalar=epsilon", a =>
            From(a.A.SolveMatrix(string.IsNullOrWhiteSpace(a.Mode) ? "general" : a.Mode,
                a.Scalar <= 0 ? 1e-6 : a.Scalar, a.B!)), NeedsB: true),
        ["eigenvaluesSymmetric"] = new("eigenvaluesSymmetric", "Eigenvalues (symmetric)", "mode=computeEigenvectors（默认 false）", a =>
        {
            var values = a.A.EigenvaluesSymmetricMatrix(string.IsNullOrWhiteSpace(a.Mode) ? "false" : a.Mode, out var vectors);
            using (vectors) return From(values);
        }),
        ["orthogonalDecompose"] = new("orthogonalDecompose", "Orthogonal decomposition", "mode=类型（默认 qr）", a =>
        {
            // ComputeOrthogonalMatrix 必须为 true：否则封装返回空句柄，后续 get_size_matrix 会报 #2453。
            var orthogonal = a.A.OrthogonalDecomposeMatrix(string.IsNullOrWhiteSpace(a.Mode) ? "qr" : a.Mode, "full", "true", out var triangular);
            using (triangular) return From(orthogonal);
        }),
        ["decompose"] = new("decompose", "Matrix decomposition", "mode=matrixType（默认 general）", a =>
        {
            var first = a.A.DecomposeMatrix(string.IsNullOrWhiteSpace(a.Mode) ? "general" : a.Mode, out var second);
            using (second) return From(first);
        }),
        ["create"] = new("create", "Create constant matrix", "rows, columns, scalar", a => Create(a)),

        // ── 原位运算（Mod 变体，直接改写 A 后回读）──────────────────
        ["absMod"] = new("absMod", "Absolute values (in place)", "无参数", a => InPlace(a, m => m.AbsMatrixMod())),
        ["addMod"] = new("addMod", "Matrix addition (in place)", "矩阵 B", a => InPlace(a, m => m.AddMatrixMod(a.B!)), NeedsB: true),
        ["subMod"] = new("subMod", "Matrix subtraction (in place)", "矩阵 B", a => InPlace(a, m => m.SubMatrixMod(a.B!)), NeedsB: true),
        ["multMod"] = new("multMod", "Matrix multiplication (in place)", "矩阵 B, mode=multType", a =>
            InPlace(a, m => m.MultMatrixMod(a.B!, MultType(a.Mode))), NeedsB: true),
        ["multElementMod"] = new("multElementMod", "Element-wise multiply (in place)", "矩阵 B", a => InPlace(a, m => m.MultElementMatrixMod(a.B!)), NeedsB: true),
        ["divElementMod"] = new("divElementMod", "Element-wise divide (in place)", "矩阵 B", a => InPlace(a, m => m.DivElementMatrixMod(a.B!)), NeedsB: true),
        ["invertMod"] = new("invertMod", "Invert (in place)", "mode=matrixType, scalar=epsilon", a =>
            InPlace(a, m => m.InvertMatrixMod(Type(a.Mode), a.Scalar <= 0 ? 1e-6 : a.Scalar))),
        ["transposeMod"] = new("transposeMod", "Transpose (in place)", "无参数", a => InPlace(a, m => m.TransposeMatrixMod())),
        ["sqrtMod"] = new("sqrtMod", "Element square root (in place)", "无参数", a => InPlace(a, m => m.SqrtMatrixMod())),
        ["scaleMod"] = new("scaleMod", "Scale (in place)", "scalar", a => InPlace(a, m => m.ScaleMatrixMod(a.Scalar))),
        ["powMod"] = new("powMod", "Matrix power (in place)", "scalar=幂次, mode=matrixType", a =>
            InPlace(a, m => m.PowMatrixMod(Type(a.Mode), a.Scalar))),
        ["powElementMod"] = new("powElementMod", "Element-wise power (in place)", "矩阵 B=指数矩阵", a =>
            InPlace(a, m => m.PowElementMatrixMod(a.B!)), NeedsB: true),
        ["powScalarElementMod"] = new("powScalarElementMod", "Power by scalar (in place)", "scalar=指数", a =>
            InPlace(a, m => m.PowScalarElementMatrixMod(a.Scalar))),
        ["setValue"] = new("setValue", "Set single element", "row, column, scalar", a =>
            InPlace(a, m => m.SetValueMatrix(a.Row, a.Column, a.Scalar))),
        ["setDiagonal"] = new("setDiagonal", "Set diagonal", "rows=向量长度, count=对角线序号, scalar", a =>
        {
            using var vector = new JlMatrix();
            vector.CreateMatrix(1, Math.Max(1, a.Rows), a.Scalar);
            return InPlace(a, m => m.SetDiagonalMatrix(vector, a.Rows));
        }),

        // ── 深拷贝 / 全量 / 广义对称特征值 ─────────────────────────
        ["copy"] = new("copy", "Copy matrix", "无参数", a => From(a.A.CopyMatrix())),
        ["getFull"] = new("getFull", "Full matrix (flat)", "无参数", a =>
        {
            using var flat = a.A.GetFullMatrix();
            var values = new double[1, Math.Max(1, flat.Length)];
            for (var i = 0; i < flat.Length; i++) values[0, i] = flat[i].D;
            return new JlMatrixResult(flat.Length, values, [("count", flat.Length)]);
        }),
        ["generalizedEigenvaluesSymmetric"] = new("generalizedEigenvaluesSymmetric", "Generalized eigenvalues (symmetric)",
            "矩阵 B, mode=computeEigenvectors", a =>
            {
                var values = a.A.GeneralizedEigenvaluesSymmetricMatrix(a.B!, string.IsNullOrWhiteSpace(a.Mode) ? "false" : a.Mode, out var vectors);
                using (vectors) return From(values);
            }, NeedsB: true)
    };

    public static IReadOnlyDictionary<string, JlMatrixOperator> All => Table;

    public static IReadOnlyList<string> Keys { get; } = Table.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    public static JlMatrixOperator Resolve(string key)
        => Table.TryGetValue(key, out var op)
            ? op
            : throw new InvalidOperationException(
                $"Unknown JLVision matrix operator '{key}'. Known operators: {string.Join(", ", Keys)}.");

    internal const int MaxElements = 1000000;
    internal static void RequireBudget(long rows, long columns)
    {
        if (rows < 1 || columns < 1 || rows > MaxElements || columns > MaxElements / rows)
            throw new InvalidOperationException($"矩阵结果最多支持 {MaxElements} 个元素，当前请求 {rows}×{columns}；请降低尺寸或重复次数。");
    }

    internal static void ValidateOutputBudget(string operation, JlMatrixArgs args)
    {
        args.A.GetSizeMatrix(out int rows, out int columns);
        switch (operation)
        {
            case "create": RequireBudget(args.Rows, args.Columns); break;
            case "repeat": RequireBudget((long)rows * args.Rows, (long)columns * args.Columns); break;
            case "getSubMatrix": RequireBudget(args.Rows, args.Columns); break;
            case "mult": case "multMod":
                args.B!.GetSizeMatrix(out int br, out int bc);
                var mode = MultType(args.Mode);
                RequireBudget(mode is "ATB" or "ATBT" ? columns : rows, mode is "ABT" or "ATBT" ? br : bc);
                break;
            case "solve":
                args.B!.GetSizeMatrix(out _, out int rightColumns);
                RequireBudget(columns, rightColumns);
                break;
            case "orthogonalDecompose":
                // Full Q can be square even for a narrow rectangular input.
                var decomposition = string.IsNullOrWhiteSpace(args.Mode) ? "qr" : args.Mode.Trim().ToLowerInvariant();
                if (decomposition is "qr" or "ql") RequireBudget(rows, rows);
                else if (decomposition is "lq" or "rq") RequireBudget(columns, columns);
                else { RequireBudget(rows, rows); RequireBudget(columns, columns); }
                break;
        }
    }

    private static string Type(string mode) => string.IsNullOrWhiteSpace(mode) ? "general" : mode;

    /// <summary>实测 mult_matrix 只接受 AB / ATB / ABT / ATBT。</summary>
    private static string MultType(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "ATB" => "ATB",
        "ABT" => "ABT",
        "ATBT" => "ATBT",
        _ => "AB"
    };

    private static JlMatrixResult Create(JlMatrixArgs a)
    {
        RequireBudget(a.Rows, a.Columns);
        using var matrix = new JlMatrix();
        matrix.CreateMatrix(a.Rows, a.Columns, a.Scalar);
        return From(matrix, disposeSource: false);
    }

    /// <summary>把原生矩阵读成 double[,]；<paramref name="disposeSource"/> 为真时负责释放。</summary>
    private static JlMatrixResult From(JlMatrix matrix, bool disposeSource = true)
    {
        try
        {
            matrix.GetSizeMatrix(out int rows, out int columns);
            RequireBudget(rows, columns);
            var values = new double[rows, columns];
            for (var r = 0; r < rows; r++)
                for (var c = 0; c < columns; c++)
                    values[r, c] = matrix.GetValueMatrix(r, c);
            return new JlMatrixResult(rows * columns, values, [("rows", rows), ("columns", columns)]);
        }
        finally
        {
            if (disposeSource) matrix.Dispose();
        }
    }

    /// <summary>原位改写 A（Mod 变体）后回读；A 的所有权仍在节点一侧，这里不释放。</summary>
    private static JlMatrixResult InPlace(JlMatrixArgs a, Action<JlMatrix> mutate)
    {
        mutate(a.A);
        return From(a.A, disposeSource: false);
    }

    /// <summary>把 JSON 二维数组解析成矩阵；空或非法内容抛可读错误。</summary>
    public static JlMatrix ParseMatrix(string json, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException($"Matrix parameter '{parameterName}' is empty; provide a JSON array of rows.");
        double[][] rows;
        try { rows = JsonSerializer.Deserialize<double[][]>(json) ?? []; }
        catch (JsonException ex) { throw new InvalidOperationException($"Matrix parameter '{parameterName}' is not a JSON array of numbers: {ex.Message}", ex); }
        if (rows.Length == 0 || rows[0] is null || rows[0].Length == 0)
            throw new InvalidOperationException($"Matrix parameter '{parameterName}' must contain at least one row and one column.");
        var columns = rows[0].Length;
        if (rows.Any(r => r is null || r.Length != columns))
            throw new InvalidOperationException($"Matrix parameter '{parameterName}' has ragged rows.");
        if ((long)rows.Length * columns > 1000000 || rows.SelectMany(r => r).Any(v => !double.IsFinite(v)))
            throw new InvalidOperationException($"Matrix parameter '{parameterName}' must contain at most one million finite numbers.");

        var matrix = new JlMatrix();
        try
        {
            matrix.CreateMatrix(rows.Length, columns, 0d);
            var flat = new double[rows.Length * columns];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < columns; c++)
                    flat[r * columns + c] = rows[r][c];
            using var tuple = new JlTuple(flat);
            matrix.SetFullMatrix(tuple);
            return matrix;
        }
        catch { matrix.Dispose(); throw; }
    }

    public static string ToJson(double[,] values)
    {
        var rows = values.GetLength(0);
        var columns = values.GetLength(1);
        var text = new System.Text.StringBuilder("[");
        for (var r = 0; r < rows; r++)
        {
            if (r > 0) text.Append(',');
            text.Append('[');
            for (var c = 0; c < columns; c++)
            {
                if (c > 0) text.Append(',');
                text.Append(values[r, c].ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            text.Append(']');
        }
        text.Append(']');
        return text.ToString();
    }
}
