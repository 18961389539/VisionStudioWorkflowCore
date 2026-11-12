namespace VisionStudio.Engine.Algorithms;

// 2026-11-12 从单文件 JLVisionMatching.cs（2809 行）按域拆出，纯移动、未改方法体。
// 嵌套类型、常量与特征点空间索引。
internal static partial class JLVisionMatching
{
    private const string ShapeModelSetPrefix = "shape-model-set-v1:";

    private sealed record ShapeModelSetItem(string Data, int Width, int Height, double OriginX, double OriginY,
        double? SourceOriginX = null, double? SourceOriginY = null);
    private sealed record ShapeModelSetEnvelope(int Version, ShapeModelSetItem[] Models);
    private readonly record struct ShapeFeaturePoint(double X, double Y);
    private sealed record ShapeContrastReference(ShapeFeaturePoint[] Points, double TrainingMeanContrast,
        double TrainingOriginX, double TrainingOriginY);
    private readonly record struct TrainingPoseAlignment(double X, double Y, double Angle, double ScaleX, double ScaleY, double Score);
    private sealed record ConsensusContour(ShapeFeaturePoint[] Points, double Support);
    private sealed class ShapeFeatureSpatialIndex
    {
        private readonly double _cellSize;
        private readonly Dictionary<(int X, int Y), List<ShapeFeaturePoint>> _cells = [];

        public ShapeFeatureSpatialIndex(IEnumerable<ShapeFeaturePoint> points, double cellSize)
        {
            _cellSize = cellSize;
            foreach (var point in points)
            {
                var key = Cell(point.X, point.Y);
                if (!_cells.TryGetValue(key, out var cell)) _cells[key] = cell = [];
                cell.Add(point);
            }
        }

        public bool TryNearest(ShapeFeaturePoint query, double maxDistance, out ShapeFeaturePoint nearest)
        {
            nearest = default;
            var key = Cell(query.X, query.Y);
            var maxDistanceSquared = maxDistance * maxDistance;
            var bestDistanceSquared = maxDistanceSquared;
            var found = false;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    if (_cells.TryGetValue((key.X + dx, key.Y + dy), out var cell))
                        foreach (var point in cell)
                        {
                            var deltaX = point.X - query.X;
                            var deltaY = point.Y - query.Y;
                            var distanceSquared = deltaX * deltaX + deltaY * deltaY;
                            if (distanceSquared > bestDistanceSquared) continue;
                            bestDistanceSquared = distanceSquared;
                            nearest = point;
                            found = true;
                        }
            return found;
        }

        private (int X, int Y) Cell(double x, double y) => ((int)Math.Floor(x / _cellSize), (int)Math.Floor(y / _cellSize));
    }
    private readonly record struct ShapeMatchCandidate(double X, double Y, double Angle, double ScaleX, double ScaleY, double Score,
        int Width, int Height, double OriginX, double OriginY, int Index, double? ClutterScore = null,
        ShapeContrastReference? ContrastReference = null)
    {
        public double Scale => Math.Sqrt(ScaleX * ScaleY);
    }
}
