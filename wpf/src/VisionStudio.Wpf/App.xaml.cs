using System.Windows;
using VisionStudio.Wpf.Hosting;

namespace VisionStudio.Wpf;

/// <summary>
/// WPF 应用入口：启动进程内视觉引擎宿主（方案 C），并在窗口关闭时优雅释放宿主资源。
/// </summary>
public partial class App : Application
{
    private InProcessVisionHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var smoke = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        try
        {
            _host = await InProcessVisionHost.StartNewAsync();
            if (smoke)
            {
                if (e.Args.Length != 3 || e.Args[0] != "--smoke-test" || e.Args[1] != "--report")
                    throw new ArgumentException("Smoke 参数必须为 --smoke-test --report <绝对报告路径>。");
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var smokeWindow = new MainWindow(_host, interactive: false);
                MainWindow = smokeWindow;
                smokeWindow.Show();
                smokeWindow.UpdateLayout();
                await DesktopTrialSmoke.RunAsync(_host, System.IO.Path.GetFullPath(e.Args[2]));
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                smokeWindow.Closed += (_, _) => closed.SetResult();
                smokeWindow.Close();
                await closed.Task;
                Shutdown(0);
                return;
            }
            var window = new MainWindow(_host);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _host?.ReportError(ex, "启动主窗口");
            if (_host is not null)
            {
                try { await _host.DisposeAsync(); }
                catch (Exception cleanupError)
                {
                    _host.ReportError(cleanupError, "启动失败后的清理");
                    ex = new AggregateException("启动失败且宿主清理失败", ex, cleanupError);
                }
            }
            var baseEx = ex.GetBaseException();
            if (smoke)
            {
                Console.Error.WriteLine(ex);
                Shutdown(-1);
                return;
            }
            MessageBox.Show(
                $"启动进程内视觉宿主或主窗口失败：\n{baseEx.Message}\n{ex.Message}",
                "VisionStudio 启动错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

}
