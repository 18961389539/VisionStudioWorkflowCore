using JLVisionLib;

namespace VisionStudio.Engine.Algorithms;

/// <summary>一维计量方法：在矩形/圆弧测量对象上执行的边缘测量方式。</summary>
public enum JlMeasureMethod
{
    MeasurePos,
    MeasurePairs,
    MeasureThresh,
    MeasureProjection
}

/// <summary>
/// JLVision 一维计量（<see cref="JlMeasure"/>）的执行封装：构造测量对象 → 执行方法 → 解析结果。
/// 覆盖 GenMeasureRectangle2 / GenMeasureArc / MeasurePos / MeasurePairs / MeasureThresh / MeasureProjection /
/// GetMeasureParam / TranslateMeasure / ResetFuzzyMeasure / CloseMeasure。
/// </summary>
public static class JlMeasureRunner
{
    /// <summary>按方法执行测量并返回行形式的边缘结果。</summary>
    public static List<Dictionary<string, double>> Run(
        JlMeasure measure,
        JlMeasureMethod method,
        JlImage image,
        double sigma,
        double threshold,
        string transition,
        string select)
    {
        ArgumentNullException.ThrowIfNull(measure);
        ArgumentNullException.ThrowIfNull(image);
        // 实测封装的输出元组长度不同步（wid 可能是 0、distance 可能比 row 短），越界统一读成 NaN。
        static double D(JlTuple tuple, int index) => index < tuple.Length ? tuple[index].D : double.NaN;
        var rows = new List<Dictionary<string, double>>();
        switch (method)
        {
            case JlMeasureMethod.MeasurePos:
                measure.MeasurePos(image, sigma, threshold, transition, select,
                    out var row, out var column, out var amplitude, out var distance);
                using (row) using (column) using (amplitude) using (distance)
                {
                    var count = Math.Max(Math.Max(row.Length, column.Length), Math.Max(amplitude.Length, distance.Length));
                    for (var i = 0; i < count; i++)
                        rows.Add(new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["row"] = D(row, i), ["column"] = D(column, i),
                            ["amplitude"] = D(amplitude, i), ["distance"] = D(distance, i)
                        });
                }
                break;
            case JlMeasureMethod.MeasurePairs:
                measure.MeasurePairs(image, sigma, threshold, transition, select,
                    out var rowFirst, out var columnFirst, out var amplitudeFirst,
                    out var rowSecond, out var columnSecond, out var amplitudeSecond,
                    out var pairDistance, out var pairWidth);
                using (rowFirst) using (columnFirst) using (amplitudeFirst)
                using (rowSecond) using (columnSecond) using (amplitudeSecond)
                using (pairDistance) using (pairWidth)
                {
                    var pairs = rowFirst.Length;
                    for (var i = 0; i < pairs; i++)
                        rows.Add(new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["rowFirst"] = D(rowFirst, i), ["columnFirst"] = D(columnFirst, i), ["amplitudeFirst"] = D(amplitudeFirst, i),
                            ["rowSecond"] = D(rowSecond, i), ["columnSecond"] = D(columnSecond, i), ["amplitudeSecond"] = D(amplitudeSecond, i),
                            ["distance"] = D(pairDistance, i), ["width"] = D(pairWidth, i)
                        });
                }
                break;
            case JlMeasureMethod.MeasureThresh:
                measure.MeasureThresh(image, sigma, threshold, select,
                    out var rowThresh, out var columnThresh, out var threshDistance);
                using (rowThresh) using (columnThresh) using (threshDistance)
                {
                    var thresholds = Math.Max(Math.Max(rowThresh.Length, columnThresh.Length), threshDistance.Length);
                    for (var i = 0; i < thresholds; i++)
                        rows.Add(new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            ["row"] = D(rowThresh, i), ["column"] = D(columnThresh, i), ["distance"] = D(threshDistance, i)
                        });
                }
                break;
            case JlMeasureMethod.MeasureProjection:
            {
                using var projection = measure.MeasureProjection(image);
                for (var i = 0; i < projection.Length; i++)
                    rows.Add(new Dictionary<string, double>(StringComparer.Ordinal) { ["projection"] = projection[i].D });
                break;
            }
        }
        return rows;
    }
}
