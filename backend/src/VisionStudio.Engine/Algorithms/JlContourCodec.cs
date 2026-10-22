using JLVisionLib;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VisionStudio.Abstractions;

namespace VisionStudio.Engine.Algorithms;

/// <summary>Native XLD boundary. Coordinates stay double precision; drawing alone may round pixels.</summary>
internal static class JlContourCodec
{
    internal static VisionContour Segment(int index, VisionPoint2D start, VisionPoint2D end)
    {
        var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        return new(index, new[] { start, end }, -1, false, 0, length, 0,
            new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)),
            new((start.X + end.X) / 2, (start.Y + end.Y) / 2, length / 2)) { Closed = false };
    }

    internal static JlXLDCont ToNative(IReadOnlyList<VisionContour> contours)
    {
        if (contours.Count > 10000 || contours.Sum(contour => (long)contour.Points.Count) > 2000000)
            throw new InvalidOperationException("轮廓数量超过 10000 或总点数超过 200 万。");
        JlXLDCont? result = null;
        try
        {
            foreach (var contour in contours)
            {
                if (contour.Points.Count < 2) throw new InvalidOperationException("轮廓至少需要两个点。");
                if (contour.Points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
                    throw new InvalidOperationException("轮廓坐标必须为有限数值。");
                var item = new JlXLDCont();
                try
                {
                    if (contour.Attributes is { Count: > 0 })
                    {
                        var snapshot = contour.NativeSnapshot;
                        if (snapshot is null || snapshot.Format != "jlvision-xld-v1" || snapshot.Data is not { Length: > 0 and <= 67108864 } ||
                            snapshot.Fingerprint != Fingerprint(contour.Points, contour.Closed, contour.Attributes, snapshot.Data))
                            throw new InvalidOperationException("轮廓的原生属性快照缺失或与点列不一致；请重新提取轮廓。不会静默丢弃线宽等逐点属性。");
                        item.DeserializeXld(snapshot.Data);
                    }
                    else
                    {
                        using var rows = new JlTuple(contour.Points.Select(p => p.Y).ToArray());
                        using var columns = new JlTuple(contour.Points.Select(p => p.X).ToArray());
                        item.GenContourPolygonXld(rows, columns);
                    }
                }
                catch { item.Dispose(); throw; }
                if (result is null) result = item;
                else
                {
                    using (item)
                    {
                        var joined = result.ConcatObj(item);
                        result.Dispose(); result = joined;
                    }
                }
            }
            return result ?? new JlXLDCont();
        }
        catch { result?.Dispose(); throw; }
    }

    internal static IReadOnlyList<VisionContour> FromNative(JlXLDCont native, CancellationToken token, int maxPoints = 2000000)
    {
        var output = new List<VisionContour>();
        var count = native.CountObj();
        if (count > 10000) throw new InvalidOperationException("轮廓数量超过 10000。");
        long total = 0;
        for (var index = 1; index <= count; index++)
        {
            token.ThrowIfCancellationRequested();
            using var item = native.SelectObj(index);
            item.GetContourXld(out var rows, out var columns);
            using (rows) using (columns)
            {
                if (rows.Length < 2) continue;
                if (rows.Length > maxPoints) throw new InvalidOperationException($"单条轮廓点数超过 {maxPoints}，不会静默抽样降低精度。");
                total += rows.Length;
                if (total > 2000000) throw new InvalidOperationException("轮廓总点数超过 200 万。");
                var points = Enumerable.Range(0, rows.Length).Select(i => new VisionPoint2D(columns[i].D, rows[i].D)).ToArray();
                if (points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) throw new InvalidOperationException("原生轮廓包含非有限坐标。");
                var closed = Math.Abs(points[0].X - points[^1].X) < 1e-6 && Math.Abs(points[0].Y - points[^1].Y) < 1e-6;
                var perimeter = 0d; var area = 0d;
                for (var i = 1; i < points.Length; i++)
                {
                    var a = points[i - 1]; var b = points[i];
                    perimeter += Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
                    area += a.X * b.Y - b.X * a.Y;
                }
                area = closed ? Math.Abs(area) / 2 : 0;
                var attributes = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
                using (var names = item.QueryContourAttribsXld())
                {
                    for (var k = 0; k < names.Length; k++)
                    {
                        var name = names[k].S;
                        using var values = item.GetContourAttribXld(name);
                        if (values.Length != points.Length) throw new InvalidOperationException($"轮廓属性 {name} 的长度与点数不一致。");
                        attributes[name] = Enumerable.Range(0, points.Length).Select(i => values[i].D).ToArray();
                    }
                }
                item.SmallestCircleXld(out double row, out double column, out double radius);
                if (!double.IsFinite(area) || !double.IsFinite(perimeter) || !double.IsFinite(row) || !double.IsFinite(column) || !double.IsFinite(radius))
                    throw new InvalidOperationException("轮廓坐标范围过大，无法计算有限的几何属性，请先调整坐标单位或缩放。");
                var x = points.Min(p => p.X); var y = points.Min(p => p.Y);
                VisionContourNativeSnapshot? snapshot = null;
                if (attributes.Count > 0)
                {
                    var data = item.SerializeXld();
                    if (data.Length > 67108864) throw new InvalidOperationException("单条轮廓原生属性快照超过 64 MB。");
                    snapshot = new("jlvision-xld-v1", Fingerprint(points, closed, attributes, data), data);
                }
                output.Add(new(output.Count, points, -1, false, area, perimeter,
                    perimeter > 0 ? Math.Min(1, 4 * Math.PI * area / (perimeter * perimeter)) : 0,
                    new(x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y), new(column, row, radius)) { Closed = closed, Attributes = attributes.Count > 0 ? attributes : null, NativeSnapshot = snapshot });
            }
        }
        return output;
    }

    private static string Fingerprint(IReadOnlyList<VisionPoint2D> points, bool closed, IReadOnlyDictionary<string, IReadOnlyList<double>> attributes, byte[] data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Number(double value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
            hash.AppendData(bytes);
        }
        Number(points.Count); Number(closed ? 1 : 0);
        foreach (var point in points) { Number(point.X); Number(point.Y); }
        foreach (var (name, values) in attributes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (values.Count != points.Count || values.Any(value => !double.IsFinite(value)))
                throw new InvalidOperationException($"逐点属性 {name} 的长度或数值无效。");
            var nameBytes = Encoding.UTF8.GetBytes(name); Number(nameBytes.Length); hash.AppendData(nameBytes);
            foreach (var value in values) Number(value);
        }
        Number(data.Length); hash.AppendData(data);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
