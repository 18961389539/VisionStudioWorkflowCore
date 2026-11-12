using OpenCvSharp;

namespace VisionStudio.Engine.Camera;

/// <summary>
/// 相机帧缓冲池（2026-11-12）。
///
/// 问题：采集每帧都 <c>new Mat(width, height, CV_8UC3)</c> 再从 SDK 缓冲 MemoryCopy。
/// 5 MP × 3 B ≈ 15 MB/帧，10 fps 就是 150 MB/s 的托管大对象分配——这些 Mat 全部落在 LOH，
/// gen2 回收跟不上时表现为"每隔几秒卡一下"，而 l3 长稳只看工作集趋势，抓不到分配速率。
///
/// 复用点选在 <see cref="CameraFrameHub"/> 的引用计数归零处：此时帧已经没有任何租约，
/// 归还的不是"还在被读的图"。池按 (宽, 高, 类型) 分桶，容量按**字节**封顶，
/// 避免多相机高分辨率场景把内存留在池里。
///
/// 安全前提：相机驱动每次都会写满整幅图（MemoryCopy / PixelDataConverter.Convert），
/// 因此复用的 Mat 不需要清零；非相机路径（几何运算的中间图）不要走这里。
/// </summary>
public static class FrameMatPool
{
    /// <summary>同一尺寸最多保留的帧数，与 CameraFrameHub 的环容量对齐（4）。</summary>
    public const int MaxPerShape = 4;

    /// <summary>池内总字节上限。8 台 5 MP 相机同时跑时不会把整机内存吃光。</summary>
    public const long MaxPooledBytes = 192L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly Dictionary<(int Width, int Height, MatType Type), Stack<Mat>> Free = new();
    private static long _pooledBytes;
    private static int _pooled;
    private static long _rented;
    private static long _reused;
    private static long _returned;
    private static long _dropped;

    public static long Rented => Interlocked.Read(ref _rented);
    public static long Reused => Interlocked.Read(ref _reused);
    public static long Returned => Interlocked.Read(ref _returned);
    public static long Dropped => Interlocked.Read(ref _dropped);
    public static long PooledBytes => Interlocked.Read(ref _pooledBytes);
    public static int Pooled => Volatile.Read(ref _pooled);

    public static Mat Rent(int width, int height, MatType type)
    {
        var key = (width, height, type);
        lock (Gate)
        {
            if (Free.TryGetValue(key, out var stack) && stack.Count > 0)
            {
                var reused = stack.Pop();
                _pooled--;
                _pooledBytes -= checked((long)reused.Width * reused.Height * reused.ElemSize());
                Interlocked.Increment(ref _reused);
                return reused;
            }
        }
        Interlocked.Increment(ref _rented);
        return new Mat(height, width, type);
    }

    public static void Return(Mat? mat)
    {
        if (mat is null) return;
        var bytes = checked((long)mat.Width * mat.Height * mat.ElemSize());
        if (bytes > MaxPooledBytes)
        {
            Interlocked.Increment(ref _dropped);
            mat.Dispose();
            return;
        }

        var key = (mat.Width, mat.Height, mat.Type());
        lock (Gate)
        {
            if (!Free.TryGetValue(key, out var stack))
            {
                stack = new Stack<Mat>(MaxPerShape);
                Free[key] = stack;
            }
            if (stack.Count < MaxPerShape && _pooledBytes + bytes <= MaxPooledBytes)
            {
                stack.Push(mat);
                _pooled++;
                _pooledBytes += bytes;
                Interlocked.Increment(ref _returned);
                return;
            }
        }
        Interlocked.Increment(ref _dropped);
        mat.Dispose();
    }

    /// <summary>健康接口用的单行摘要；没有它，池只会变成"看不见的第二套内存账本"。</summary>
    public static string Snapshot()
        => $"rented={Rented} reused={Reused} returned={Returned} dropped={Dropped} pooled={Pooled} bytes={PooledBytes}";
}
