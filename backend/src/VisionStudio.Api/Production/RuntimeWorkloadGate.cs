using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

/// <summary>Reserves production or benchmark access before either workload starts asynchronous preparation.</summary>
public sealed class RuntimeWorkloadGate
{
    private readonly object _sync = new();
    private bool _production;
    private int _benchmarks;

    public IDisposable AcquireProduction()
    {
        lock (_sync)
        {
            if (_production)
                throw new ApiConflictException("Production Runtime already owns the workload reservation.");
            if (_benchmarks != 0)
                throw new ApiConflictException("A plugin performance benchmark is starting or running. Cancel or finish it before starting Production Runtime.");
            _production = true;
            return new Lease(ReleaseProduction);
        }
    }

    public IDisposable AcquireBenchmark()
    {
        lock (_sync)
        {
            if (_production)
                throw new ApiConflictException("Stop Production Runtime before starting a plugin performance benchmark.");
            _benchmarks++;
            return new Lease(ReleaseBenchmark);
        }
    }

    private void ReleaseProduction()
    {
        lock (_sync) _production = false;
    }

    private void ReleaseBenchmark()
    {
        lock (_sync) _benchmarks--;
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
