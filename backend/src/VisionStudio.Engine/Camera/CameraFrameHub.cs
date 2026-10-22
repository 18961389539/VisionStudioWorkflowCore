using OpenCvSharp;

namespace VisionStudio.Engine.Camera;

/// <summary>
/// Per-camera bounded ring of reference-counted native frames.
/// The ring owns one reference per retained frame; consumers obtain independent leases.
/// Publish uses a generation TaskCompletionSource so all Next waiters are awakened for each new frame.
/// </summary>
public sealed class CameraFrameHub : IDisposable
{
    private readonly object _gate = new();
    private readonly SharedCameraFrame?[] _ring;
    private TaskCompletionSource<long> _nextPublished = NewSignal();
    private int _writeIndex;
    private int _count;
    private long _overwrites;
    private bool _disposed;

    public CameraFrameHub(int capacity = 4)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _ring = new SharedCameraFrame[capacity];
    }

    public int Capacity => _ring.Length;
    public long RingOverwrites => Interlocked.Read(ref _overwrites);

    public void Publish(VisionFrame frame)
    {
        var shared = new SharedCameraFrame(
            frame.CameraId,
            frame.Sequence,
            frame.Timestamp,
            frame.DetachImage(),
            frame.PixelFormat,
            frame.DeviceTimestampNs,
            frame.TriggerId);

        TaskCompletionSource<long>? signal = null;
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                var old = _ring[_writeIndex];
                _ring[_writeIndex] = shared;
                _writeIndex = (_writeIndex + 1) % _ring.Length;
                if (_count < _ring.Length) _count++;
                else Interlocked.Increment(ref _overwrites);
                old?.Release();

                signal = _nextPublished;
                _nextPublished = NewSignal();
            }
        }
        catch
        {
            shared.Release();
            throw;
        }

        signal.TrySetResult(shared.Sequence);
    }

    public CameraFrameLease? TryAcquireLatest()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return AcquireLatestUnsafe();
        }
    }

    public long LatestSequence
    {
        get
        {
            lock (_gate)
            {
                if (_count == 0) return 0;
                var index = (_writeIndex - 1 + _ring.Length) % _ring.Length;
                return _ring[index]?.Sequence ?? 0;
            }
        }
    }

    public async ValueTask<CameraFrameLease> WaitForLatestAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        return await WaitForNextAsync(0, timeout, cancellationToken);
    }

    public async ValueTask<CameraFrameLease> WaitForNextAsync(long afterSequence, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            CameraFrameLease? lease;
            Task<long> signalTask;
            lock (_gate)
            {
                ThrowIfDisposed();
                lease = AcquireLatestUnsafe();
                if (lease is not null && lease.Sequence > afterSequence)
                    return lease;
                lease?.Dispose();
                signalTask = _nextPublished.Task;
            }

            try { await signalTask.WaitAsync(timeoutCts.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for camera frame newer than sequence {afterSequence}.");
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            for (var i = 0; i < _ring.Length; i++)
            {
                _ring[i]?.Release();
                _ring[i] = null;
            }
            _count = 0;
            _writeIndex = 0;
        }
    }

    public void Dispose()
    {
        TaskCompletionSource<long>? signal;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            for (var i = 0; i < _ring.Length; i++)
            {
                _ring[i]?.Release();
                _ring[i] = null;
            }
            _count = 0;
            signal = _nextPublished;
        }
        signal.TrySetException(new ObjectDisposedException(nameof(CameraFrameHub)));
    }

    private CameraFrameLease? AcquireLatestUnsafe()
    {
        if (_count == 0) return null;
        var index = (_writeIndex - 1 + _ring.Length) % _ring.Length;
        return _ring[index]?.Acquire();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CameraFrameHub));
    }

    private static TaskCompletionSource<long> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class SharedCameraFrame
    {
        private Mat? _image;
        private int _references = 1; // ring ownership

        public SharedCameraFrame(string cameraId, long sequence, DateTimeOffset timestamp, Mat image, string pixelFormat, long? deviceTimestampNs, long? triggerId)
        {
            CameraId = cameraId;
            Sequence = sequence;
            Timestamp = timestamp;
            _image = image;
            PixelFormat = pixelFormat;
            DeviceTimestampNs = deviceTimestampNs;
            TriggerId = triggerId;
        }

        public string CameraId { get; }
        public long Sequence { get; }
        public DateTimeOffset Timestamp { get; }
        public string PixelFormat { get; }
        public long? DeviceTimestampNs { get; }
        public long? TriggerId { get; }
        public Mat Image => _image ?? throw new ObjectDisposedException(nameof(SharedCameraFrame));

        public CameraFrameLease Acquire()
        {
            if (_image is null) throw new ObjectDisposedException(nameof(SharedCameraFrame));
            Interlocked.Increment(ref _references);
            return new CameraFrameLease(this);
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) != 0) return;
            Interlocked.Exchange(ref _image, null)?.Dispose();
        }
    }
}

public sealed class CameraFrameLease : IDisposable
{
    private CameraFrameHub.SharedCameraFrame? _frame;

    internal CameraFrameLease(CameraFrameHub.SharedCameraFrame frame) => _frame = frame;

    private CameraFrameHub.SharedCameraFrame Frame => _frame ?? throw new ObjectDisposedException(nameof(CameraFrameLease));
    public string CameraId => Frame.CameraId;
    public long Sequence => Frame.Sequence;
    public DateTimeOffset Timestamp => Frame.Timestamp;
    public string PixelFormat => Frame.PixelFormat;
    public long? DeviceTimestampNs => Frame.DeviceTimestampNs;
    public long? TriggerId => Frame.TriggerId;
    public Mat Image => Frame.Image;

    public void Dispose()
    {
        var frame = Interlocked.Exchange(ref _frame, null);
        frame?.Release();
    }
}
