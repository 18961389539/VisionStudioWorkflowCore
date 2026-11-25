using OpenCvSharp;
using VisionStudio.Engine.Camera;

namespace VisionStudio.Engine.Tests;

/// <summary>
/// 帧池（2026-11-12）：相机每帧 15 MB 的 Mat 不再走 LOH。这里只钉住"归还后确实复用同一块
/// 本机内存"与"桶满时退化为 Dispose"两条契约——池的容量上限由常量表达，不需要逐字节测试。
/// </summary>
public sealed class FrameMatPoolTests
{
    [Fact]
    public void ReturnedBuffer_IsReusedByTheNextRentOfTheSameShape()
    {
        // 用不常见尺寸，避免与并行测试的相机帧串味。
        var first = FrameMatPool.Rent(37, 19, MatType.CV_8UC1);
        var pointer = first.Data;
        first.SetTo(Scalar.All(7));

        FrameMatPool.Return(first);
        using var second = FrameMatPool.Rent(37, 19, MatType.CV_8UC1);

        Assert.Equal(pointer, second.Data);
        Assert.Equal(37, second.Width);
        Assert.Equal(19, second.Height);
    }

    [Fact]
    public void DifferentShape_DoesNotReuseTheBuffer()
    {
        var first = FrameMatPool.Rent(41, 23, MatType.CV_8UC1);
        var pointer = first.Data;
        FrameMatPool.Return(first);

        using var other = FrameMatPool.Rent(41, 24, MatType.CV_8UC1);
        Assert.NotEqual(pointer, other.Data);
    }

    [Fact]
    public void ReturnNull_IsIgnored()
    {
        FrameMatPool.Return(null);
    }

    [Fact]
    public void TrimAndClear_ReleasePooledBuffers()
    {
        FrameMatPool.Clear();
        Assert.Equal(0, FrameMatPool.Pooled);
        Assert.Equal(0L, FrameMatPool.PooledBytes);

        var a = FrameMatPool.Rent(64, 32, MatType.CV_8UC1);
        var b = FrameMatPool.Rent(64, 32, MatType.CV_8UC1);
        FrameMatPool.Return(a);
        FrameMatPool.Return(b);

        Assert.Equal(2, FrameMatPool.Pooled);
        Assert.Equal(2L * 64 * 32, FrameMatPool.PooledBytes);

        var trimmed = FrameMatPool.Trim(64 * 32);
        Assert.Equal(1, trimmed);
        Assert.Equal(1, FrameMatPool.Pooled);
        Assert.Equal(64L * 32, FrameMatPool.PooledBytes);

        var cleared = FrameMatPool.Clear();
        Assert.Equal(1, cleared);
        Assert.Equal(0, FrameMatPool.Pooled);
        Assert.Equal(0L, FrameMatPool.PooledBytes);
    }
}
