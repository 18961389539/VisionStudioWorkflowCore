using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ImageViewer.Abstractions;
using ImageViewer.Localization;

namespace ImageViewer.Controls
{
    internal sealed class ImageViewerAutoSaveController : IDisposable
    {
        private readonly ImageViewerAutoSaveWorkflow _workflow;
        private readonly Func<ImageViewerPersistenceSnapshot> _captureSnapshot;
        private readonly Func<string, ImageViewerPersistenceSnapshot, CancellationToken, Task> _saveRecoveryAsync;
        private readonly IImageViewerPeriodicTaskScheduler _autoSaveScheduler;
        private readonly ImageViewerRecoveryIndex _recoveryIndex;
        private readonly string _autoSaveDirectory;
        private Task _pendingWrite = Task.CompletedTask;
        private FileStream? _ownerLease;
        private CancellationTokenSource _lifecycleCancellationTokenSource = new();
        private string? _currentProjectPath;
        private int _suspensionCount;
        private bool _lastAutoSaveFailed;
        private volatile bool _disposed;

        public ImageViewerAutoSaveController(
            ImageViewerAutoSaveWorkflow workflow,
            Func<ImageViewerPersistenceSnapshot> captureSnapshot,
            Func<string, ImageViewerPersistenceSnapshot, CancellationToken, Task> saveRecoveryAsync,
            IImageViewerPeriodicTaskSchedulerFactory periodicTaskSchedulerFactory,
            IImageViewerSessionStoragePolicy sessionStoragePolicy,
            string documentId,
            ImageViewerRecoveryIndex recoveryIndex)
        {
            _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
            _captureSnapshot = captureSnapshot ?? throw new ArgumentNullException(nameof(captureSnapshot));
            _saveRecoveryAsync = saveRecoveryAsync ?? throw new ArgumentNullException(nameof(saveRecoveryAsync));
            ArgumentNullException.ThrowIfNull(periodicTaskSchedulerFactory);
            ArgumentNullException.ThrowIfNull(sessionStoragePolicy);
            ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

            _autoSaveDirectory = sessionStoragePolicy.AutoSaveDirectory;
            _recoveryIndex = recoveryIndex ?? throw new ArgumentNullException(nameof(recoveryIndex));
            DocumentId = documentId;
            SessionInstanceId = Guid.NewGuid().ToString("N");
            TimeSpan autoSaveInterval = sessionStoragePolicy.AutoSaveInterval;
            _autoSaveScheduler = periodicTaskSchedulerFactory.Create(
                AutoSaveAsync,
                _workflow.Dispatcher,
                DispatcherPriority.Background,
                autoSaveInterval);
            _autoSaveScheduler.Start();
        }

        /// <summary>
        /// 本查看器实例的稳定文档身份。
        /// Chinese: 未保存项目时用它区分不同窗口/文档的自动保存文件，避免多窗口写入同一路径互相覆盖。
        /// English: Stable per-viewer identity used in untitled auto-save file names.
        /// </summary>
        public string DocumentId { get; }

        /// <summary>
        /// 本"会话实例"的标识（每次构造新生成，与 <see cref="DocumentId"/> 无关）。
        /// Chinese: DocumentId 会因为文档获得项目路径而改变含义，不适合表达"这个窗口还开着吗"。
        /// 会话实例 id 一旦生成就不再变化，用于在恢复索引里标记"这份快照归谁所有"。
        /// English: Per-instance id used to mark ownership of auto-save snapshots. Unlike DocumentId it never
        /// changes for the lifetime of the window, so it can answer "is this snapshot still in use?".
        /// </summary>
        public string SessionInstanceId { get; }

        public void Start()
        {
            if (_disposed)
            {
                return;
            }

            if (_lifecycleCancellationTokenSource.IsCancellationRequested)
            {
                _lifecycleCancellationTokenSource.Dispose();
                _lifecycleCancellationTokenSource = new CancellationTokenSource();
            }

            _autoSaveScheduler.Start();
        }

        public void StopScheduling()
        {
            if (_disposed)
            {
                return;
            }
            _lifecycleCancellationTokenSource.Cancel();
            _autoSaveScheduler.StopScheduling();
        }

        public Task DrainAsync(CancellationToken cancellationToken = default)
        {
            return _autoSaveScheduler.DrainAsync(cancellationToken);
        }

        public Task DrainPendingWriteAsync() => _pendingWrite;

        internal IDisposable SuspendWrites()
        {
            Interlocked.Increment(ref _suspensionCount);
            return new WriteSuspension(this);
        }

        private sealed class WriteSuspension(ImageViewerAutoSaveController owner) : IDisposable
        {
            private ImageViewerAutoSaveController? _owner = owner;

            public void Dispose()
            {
                ImageViewerAutoSaveController? controller = Interlocked.Exchange(ref _owner, null);
                if (controller != null)
                {
                    Interlocked.Decrement(ref controller._suspensionCount);
                }
            }
        }

        public Task CloseCompletion { get; private set; } = Task.CompletedTask;

        public bool IsEnabled { get; private set; } = true;

        public void SetCurrentProject(string? filePath)
        {
            _currentProjectPath = string.IsNullOrWhiteSpace(filePath)
                ? null
                : Path.GetFullPath(filePath);
        }

        public string GetAutoSaveFilePath()
        {
            return Path.Combine(_autoSaveDirectory, $"{GetAutoSaveFileName()}.ivsession");
        }

        public void Toggle()
        {
            IsEnabled = !IsEnabled;
            // 重新开启时重置失败标记，避免上次失败长时间抑制新失败提示
            if (IsEnabled)
            {
                _lastAutoSaveFailed = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            StopScheduling();
            _disposed = true;
            _autoSaveScheduler.Dispose();
            CloseCompletion = FinishCloseAsync(_lifecycleCancellationTokenSource);
        }

        private async Task FinishCloseAsync(CancellationTokenSource cancellation)
        {
            try
            {
                // File commits and this continuation do not require the dispatcher to stay alive.
                await _pendingWrite.ConfigureAwait(false);
                _recoveryIndex.DeleteOwnedSnapshots(SessionInstanceId);
            }
            finally
            {
                _ownerLease?.Dispose();
                cancellation.Dispose();
            }
        }

        private Task AutoSaveAsync()
        {
            // The scheduler starts callbacks on the viewer's dispatcher.
            if (_disposed || !IsEnabled || Volatile.Read(ref _suspensionCount) != 0 ||
                !_pendingWrite.IsCompleted || !_workflow.HasContent())
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingWrite = completion.Task;
            return WriteSnapshotAsync(completion, _lifecycleCancellationTokenSource.Token);
        }

        private async Task WriteSnapshotAsync(TaskCompletionSource completion, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_autoSaveDirectory);
                _ownerLease ??= _recoveryIndex.TryAcquireOwnerLease(SessionInstanceId);
                string filePath = GetAutoSaveFilePath();
                string? projectName = Path.GetFileName(_currentProjectPath);
                ImageViewerPersistenceSnapshot snapshot = _captureSnapshot();
                await _saveRecoveryAsync(filePath, snapshot, cancellationToken).ConfigureAwait(false);
                _lastAutoSaveFailed = false;
                // 登记到恢复索引：DocumentId 每次启动都变，不登记的话重启后找不到这份未保存文档。
                // 同时带上所有者与心跳：另一个窗口据此判断"这份快照还在被使用"，不该提示恢复它。
                DateTimeOffset now = DateTimeOffset.UtcNow;
                string? imageName = snapshot.InMemoryImage != null
                    ? UiText.Get("RecoveryPickerInMemoryImage")
                    : Path.GetFileName(snapshot.ImagePath);
                _recoveryIndex.Register(filePath, now, SessionInstanceId, now, projectName,
                    imageName, hasOwnerLease: _ownerLease != null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _workflow.LogNonCriticalError("Auto save failed", ex);
                if (_lastAutoSaveFailed)
                {
                    return;
                }

                _lastAutoSaveFailed = true;
                if (!_disposed && !cancellationToken.IsCancellationRequested)
                {
                    if (_workflow.Dispatcher.CheckAccess())
                    {
                        _workflow.ShowStatusHint(UiText.Get("StatusAutoSaveFailed"), StatusHintKind.Error);
                    }
                    else
                    {
                        _ = _workflow.Dispatcher.InvokeAsync(() =>
                        {
                            if (!_disposed && !cancellationToken.IsCancellationRequested)
                            {
                                _workflow.ShowStatusHint(UiText.Get("StatusAutoSaveFailed"), StatusHintKind.Error);
                            }
                        });
                    }
                }
            }
            finally
            {
                completion.TrySetResult();
            }
        }

        private string GetAutoSaveFileName()
        {
            if (string.IsNullOrWhiteSpace(_currentProjectPath))
            {
                // 未保存项目：用文档 ID 区分实例，避免多窗口共享同一 Autosave 目录时互相覆盖。
                return $"autosave-{DocumentId}";
            }

            string baseName = Path.GetFileNameWithoutExtension(_currentProjectPath) ?? "autosave";
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                baseName = baseName.Replace(invalidChar, '_');
            }

            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "autosave";
            }

            // The hash distinguishes paths; the instance suffix distinguishes editors of the same project.
            if (baseName.Length > 48)
            {
                baseName = baseName[..48];
            }

            string pathHash = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(_currentProjectPath)))
                .ToLowerInvariant()[..12];
            return $"autosave-{baseName}-{pathHash}-{SessionInstanceId}";
        }
    }
}
