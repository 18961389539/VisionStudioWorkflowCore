using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace VisionStudio.Wpf.Tests;

[CollectionDefinition("WPF UI")]
public sealed class WpfUiCollection : ICollectionFixture<WpfDispatcherFixture>;

public sealed class WpfDispatcherFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    public WpfDispatcherFixture()
    {
        var started = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var application = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.InitializeComponent();
            started.SetResult(dispatcher);
            Dispatcher.Run();
        })
        { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcher = started.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    public Task RunAsync(Func<Task> action) =>
        _dispatcher.InvokeAsync(action).Task.Unwrap().WaitAsync(TimeSpan.FromMinutes(4));

    public void Dispose()
    {
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        Assert.True(_thread.Join(TimeSpan.FromSeconds(30)), "WPF dispatcher did not stop.");
    }
}
