using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api;

/// <summary>Reserves production or benchmark access before either workload starts asynchronous preparation.</summary>
public sealed class RuntimeWorkloadGate
{
    private readonly object _sync = new();
    private int _productionCount;
    private int _benchmarks;

    public IDisposable AcquireProduction()
    {
        lock (_sync)
        {
            if (_benchmarks != 0)
                throw new ApiConflictException("A plugin performance benchmark is starting or running. Cancel or finish it before starting Production Runtime.");
            _productionCount++;
            return new Lease(ReleaseProduction);
        }
    }

    public IDisposable AcquireBenchmark()
    {
        lock (_sync)
        {
            if (_productionCount > 0)
                throw new ApiConflictException("Stop Production Runtime before starting a plugin performance benchmark.");
            _benchmarks++;
            return new Lease(ReleaseBenchmark);
        }
    }

    private void ReleaseProduction()
    {
        lock (_sync)
        {
            if (_productionCount > 0) _productionCount--;
        }
    }

    private void ReleaseBenchmark()
    {
        lock (_sync)
        {
            if (_benchmarks > 0) _benchmarks--;
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
