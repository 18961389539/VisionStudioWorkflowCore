using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisionStudio.Wpf.Hosting;

namespace VisionStudio.Wpf.ViewModels;

public abstract partial class DesktopWorkspaceViewModel(InProcessVisionHost host) : ObservableObject
{
    private readonly List<IAsyncRelayCommand> _commands = [];
    protected InProcessVisionHost Host { get; } = host;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isClosing;

    [ObservableProperty]
    private bool _isStatusConfirmed;

    [ObservableProperty]
    private string _statusMessage = "状态尚未确认";

    [ObservableProperty]
    private DateTimeOffset? _lastUpdatedAt;

    public bool CanOperate => !IsBusy && !IsClosing;
    public string FreshnessText => IsStatusConfirmed
        ? $"状态已确认 · {LastUpdatedAt:HH:mm:ss}"
        : "状态未知 / 陈旧";

    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    partial void OnIsClosingChanged(bool value) => NotifyCommands();
    partial void OnIsStatusConfirmedChanged(bool value) => OnPropertyChanged(nameof(FreshnessText));
    partial void OnLastUpdatedAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(FreshnessText));

    protected void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanOperate));
        foreach (var command in _commands) command.NotifyCanExecuteChanged();
    }

    protected IAsyncRelayCommand CreateCommand(string operation, Func<CancellationToken, Task> action, Func<bool>? condition = null)
    {
        var command = new AsyncRelayCommand(async cancellationToken =>
        {
            if (!CanOperate)
            {
                StatusMessage = "请等待当前操作完成。";
                return;
            }
            IsBusy = true;
            try { await action(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                IsStatusConfirmed = false;
                StatusMessage = $"{operation}已取消，状态需要重新确认";
            }
            catch (Exception exception)
            {
                IsStatusConfirmed = false;
                Host.ReportError(exception, operation);
                StatusMessage = $"{operation}失败，状态未确认：{exception.Message}";
            }
            finally { IsBusy = false; }
        }, () => CanOperate && (condition?.Invoke() ?? true));
        _commands.Add(command);
        return command;
    }

    protected void ConfirmSnapshot()
    {
        LastUpdatedAt = DateTimeOffset.Now;
        IsStatusConfirmed = true;
        NotifyCommands();
    }

    public async Task PrepareCloseAsync()
    {
        IsClosing = true;
        foreach (var command in _commands) command.Cancel();
        await Task.WhenAll(_commands.Select(command => command.ExecutionTask ?? Task.CompletedTask));
    }
}
