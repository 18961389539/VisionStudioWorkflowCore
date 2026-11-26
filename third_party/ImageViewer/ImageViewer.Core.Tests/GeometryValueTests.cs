using ImageViewer.Models;
using Xunit;

namespace ImageViewer.Core.Tests;

public class GeometryValueTests
{
    [Fact]
    public void PointD_DistanceAndVectorOperatorsRemainFrameworkNeutral()
    {
        PointD origin = new(0, 0);
        PointD point = new(3, 4);

        Assert.Equal(5, origin.DistanceTo(point));
        Assert.Equal(new VectorD(3, 4), point - origin);
        Assert.Equal(point, origin + new VectorD(3, 4));
    }

    [Fact]
    public void VectorD_Normalized_LeavesZeroVectorStable()
    {
        Assert.Equal(new VectorD(0, 0), new VectorD(0, 0).Normalized());
        Assert.Equal(new VectorD(0.6, 0.8), new VectorD(3, 4).Normalized());
    }
}
