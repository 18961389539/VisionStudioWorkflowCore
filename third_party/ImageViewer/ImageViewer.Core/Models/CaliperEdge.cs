namespace ImageViewer.Models
{
    /// <summary>One subpixel edge found by a one-dimensional caliper.</summary>
    /// <param name="Point">Edge location in image x/y coordinates.</param>
    /// <param name="Score">Absolute gray-value amplitude across the edge.</param>
    /// <param name="Offset">Signed distance from the measure center along the profile axis.</param>
    public readonly record struct CaliperEdge(PointD Point, double Score, double Offset);

    /// <summary>A paired edge measurement (two edges straddling the measure center) and their gap.</summary>
    /// <param name="Edge1">Edge with the smaller offset.</param>
    /// <param name="Edge2">Edge with the larger offset.</param>
    /// <param name="Gap">Distance between the two edges along the profile axis.</param>
    public readonly record struct CaliperEdgePair(CaliperEdge Edge1, CaliperEdge Edge2, double Gap);
}
