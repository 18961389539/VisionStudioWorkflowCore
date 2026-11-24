using VisionStudio.Api.Infrastructure;

namespace VisionStudio.Api.Hosting;

internal sealed class DataRootOwnership(IHostEnvironment environment, IConfiguration configuration)
    : IHostedLifecycleService, IDisposable
{
    private FileStream? _lock;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = VisionStudioDataRoot.Resolve(environment.ContentRootPath, configuration);
        Directory.CreateDirectory(root);
        try
        {
            // Leave the file in place. Recreating it could allow two processes to lock different files.
            _lock = new FileStream(Path.Combine(root, ".visionstudio-host.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"Cannot acquire exclusive ownership of VisionStudio data root '{root}'. Verify that no other host is using it and the directory is writable.",
                exception);
        }
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => Interlocked.Exchange(ref _lock, null)?.Dispose();
}
