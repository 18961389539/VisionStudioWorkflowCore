using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ImageViewerControl.Tests
{
    internal static class WpfTestRunner
    {
        public static void Run(Action action)
        {
            Exception? exception = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                    DrainDispatcher();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (exception != null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        public static void RunAsync(Func<Task> action)
        {
            ArgumentNullException.ThrowIfNull(action);

            Exception? exception = null;
            var thread = new Thread(() =>
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                try
                {
                    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
                    Task task = action();
                    _ = task.ContinueWith(
                        _ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                    Dispatcher.Run();
                    task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    exception = ex;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (exception != null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        public static object? InvokePrivate(object target, string methodName, params object?[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Method '{methodName}' was not found.");

            return method.Invoke(target, arguments);
        }

        public static void DrainDispatcher()
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }
}
