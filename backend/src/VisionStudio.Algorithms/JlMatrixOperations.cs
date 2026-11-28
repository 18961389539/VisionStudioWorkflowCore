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
        ["setDiagonal"] = new("setDiagonal", "Set diagonal", "rows=向量长度且同时作为对角线序号, scalar（count 未使用）", a =>
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

    internal static void ValidateOperation(string operation, JlMatrixArgs args)
    {
        args.A.GetSizeMatrix(out var rows, out var columns);
        if ((operation is "invert" or "invertMod" or "determinant" or "eigenvaluesSymmetric" or "solve" or "pow" or "powMod") && rows != columns)
            throw new InvalidOperationException($"Matrix operator '{operation}' requires a square matrix; received {rows}x{columns}.");
        if (operation == "generalizedEigenvaluesSymmetric")
        {
            args.B!.GetSizeMatrix(out var otherRows, out var otherColumns);
            if (rows != columns || otherRows != rows || otherColumns != columns)
                throw new InvalidOperationException("Generalized symmetric eigenvalues require equally sized square matrices.");
        }
        if (operation is "add" or "sub" or "multElement" or "divElement" or "addMod" or "subMod" or "multElementMod" or "divElementMod" or "powElementMod")
        {
            args.B!.GetSizeMatrix(out var otherRows, out var otherColumns);
            if (rows != otherRows || columns != otherColumns)
                throw new InvalidOperationException($"Matrix operator '{operation}' requires matching dimensions; received {rows}x{columns} and {otherRows}x{otherColumns}.");
        }
        if (operation is "mult" or "multMod")
        {
            args.B!.GetSizeMatrix(out var otherRows, out var otherColumns);
            var mode = MultType(args.Mode);
            var innerA = mode is "ATB" or "ATBT" ? rows : columns;
            var innerB = mode is "ABT" or "ATBT" ? otherRows : otherColumns;
            if (innerA != innerB)
                throw new InvalidOperationException($"Matrix multiplication dimensions do not conform for {mode}: {rows}x{columns} and {otherRows}x{otherColumns}.");
        }
        if (operation == "solve")
        {
            args.B!.GetSizeMatrix(out var rightRows, out _);
            if (rightRows != rows)
                throw new InvalidOperationException($"Matrix solve requires matrixB row count {rows}; received {rightRows}.");
            if (TryConditionNumber(args.A) is null)
                throw new InvalidOperationException("jlvision.matrixOp.singularMatrix:solve: coefficient matrix is singular.");
        }
        if (operation is "getValue" or "getSubMatrix" or "setValue")
        {
            if (args.Row < 0 || args.Column < 0 || args.Row >= rows || args.Column >= columns)
                throw new InvalidOperationException($"Matrix operator '{operation}' index ({args.Row},{args.Column}) is outside {rows}x{columns}.");
        }
        if (operation == "getSubMatrix" && (args.Rows < 1 || args.Columns < 1 || args.Row + args.Rows > rows || args.Column + args.Columns > columns))
            throw new InvalidOperationException($"Matrix sub-matrix range exceeds {rows}x{columns} bounds.");
        if (operation is "divElement" or "divElementMod")
        {
            args.B!.GetSizeMatrix(out var otherRows, out var otherColumns);
            for (var row = 0; row < otherRows; row++)
            for (var column = 0; column < otherColumns; column++)
                if (args.B.GetValueMatrix(row, column) == 0)
                    throw new InvalidOperationException($"jlvision.matrixOp.divideByZero:{operation}: matrixB[{row},{column}] is zero.");
        }
        if (operation is "invert" or "invertMod" && TryConditionNumber(args.A) is null)
            throw new InvalidOperationException($"jlvision.matrixOp.singularMatrix:{operation}: coefficient matrix is singular.");
    }

    /// <summary>Infinity-norm condition number for a square matrix; null means singular.</summary>
    internal static double? TryConditionNumber(JlMatrix matrix)
    {
        matrix.GetSizeMatrix(out var n, out var columns);
        if (n != columns || n < 1) return null;
        var augmented = new double[n, n * 2];
        for (var row = 0; row < n; row++)
        {
            for (var column = 0; column < n; column++)
                augmented[row, column] = matrix.GetValueMatrix(row, column);
            augmented[row, n + row] = 1;
        }
        var scale = 0d;
        for (var row = 0; row < n; row++)
        {
            var rowSum = 0d;
            for (var column = 0; column < n; column++) rowSum += Math.Abs(augmented[row, column]);
            scale = Math.Max(scale, rowSum);
        }
        if (scale == 0 || !double.IsFinite(scale)) return null;
        for (var pivot = 0; pivot < n; pivot++)
        {
            var bestRow = pivot;
            var bestValue = Math.Abs(augmented[pivot, pivot]);
            for (var row = pivot + 1; row < n; row++)
            {
                var value = Math.Abs(augmented[row, pivot]);
                if (value > bestValue) { bestValue = value; bestRow = row; }
            }
            if (bestValue <= double.Epsilon * scale * n) return null;
            if (bestRow != pivot)
                for (var column = 0; column < n * 2; column++)
                    (augmented[pivot, column], augmented[bestRow, column]) = (augmented[bestRow, column], augmented[pivot, column]);
            var divisor = augmented[pivot, pivot];
            for (var column = 0; column < n * 2; column++) augmented[pivot, column] /= divisor;
            for (var row = 0; row < n; row++)
            {
                if (row == pivot) continue;
                var factor = augmented[row, pivot];
                if (factor == 0) continue;
                for (var column = 0; column < n * 2; column++) augmented[row, column] -= factor * augmented[pivot, column];
            }
        }
        var inverseNorm = 0d;
        for (var row = 0; row < n; row++)
        {
            var rowSum = 0d;
            for (var column = n; column < n * 2; column++) rowSum += Math.Abs(augmented[row, column]);
            inverseNorm = Math.Max(inverseNorm, rowSum);
        }
        var condition = scale * inverseNorm;
        return double.IsFinite(condition) ? condition : double.PositiveInfinity;
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
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                throw new InvalidOperationException($"Matrix parameter '{parameterName}' must contain at least one row and one column.");
            var rowCount = root.GetArrayLength();
            var columns = 0;
            foreach (var row in root.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() == 0)
                    throw new InvalidOperationException($"Matrix parameter '{parameterName}' must be a rectangular JSON array of numbers.");
                if (columns == 0) columns = row.GetArrayLength();
                else if (row.GetArrayLength() != columns)
                    throw new InvalidOperationException($"Matrix parameter '{parameterName}' has ragged rows.");
            }
            // Check the element budget before allocating the managed double[][] or native matrix.
            if ((long)rowCount * columns > MaxElements)
                throw new InvalidOperationException($"Matrix parameter '{parameterName}' must contain at most {MaxElements} finite numbers; received {rowCount}x{columns}.");
            var rows = new double[rowCount][];
            var rowIndex = 0;
            foreach (var row in root.EnumerateArray())
            {
                var values = new double[columns];
                var columnIndex = 0;
                foreach (var value in row.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                        throw new InvalidOperationException($"Matrix parameter '{parameterName}' contains a non-numeric or non-finite element at [{rowIndex},{columnIndex}].");
                    values[columnIndex++] = number;
                }
                rows[rowIndex++] = values;
            }

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
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Matrix parameter '{parameterName}' is not a JSON array of numbers: {ex.Message}", ex);
        }
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
