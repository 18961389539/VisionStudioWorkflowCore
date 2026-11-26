using System;
using System.Collections.Generic;

namespace ImageViewer.Controls
{
    public partial class ImageViewer
    {
        private ImageViewerLifetimeRegistrationCollection CreateLifetimeRegistrations(ImageViewerControlComposition controlComposition)
        {
            ArgumentNullException.ThrowIfNull(controlComposition);

            var registrations = new ImageViewerLifetimeRegistrationCollection();

            // Cleanups run in reverse registration order, so the host lease is registered first in order to be
            // released last: the control tears itself down before the runtime it borrows can be disposed, and an
            // exception thrown by an earlier cleanup cannot strand the lease.
            registrations.AddCleanup(ReleaseHostLease);

            // 注册表可能被多个 viewer 共享：释放时断订阅，否则注册表会一直持有本控件的 ViewModel 与可视树。
            registrations.AddCleanup(_hostState.DetachPluginRegistry);
            registrations.AddAttachment(
                () => _hostState.RoiStateMigrated += OnRoiStateMigrated,
                () => _hostState.RoiStateMigrated -= OnRoiStateMigrated);
            registrations.AddAttachment(
                () =>
                {
                    controlComposition.SessionController.StateChanged += OnSessionStateChanged;
                    OnSessionStateChanged(controlComposition.SessionController, EventArgs.Empty);
                },
                () => controlComposition.SessionController.StateChanged -= OnSessionStateChanged);
            registrations.AddAttachment(
                () => RuntimeOptions.PropertyChanged += OnRuntimeOptionsPropertyChanged,
                () => RuntimeOptions.PropertyChanged -= OnRuntimeOptionsPropertyChanged);
            registrations.AddAttachment(controlComposition.Attach, controlComposition.Detach);
            registrations.AddAttachment(InitializeEventHandlers, UnregisterEventHandlers);
            registrations.AddAttachment(() => Loaded += OnLoaded, () => Loaded -= OnLoaded);
            registrations.AddAttachment(() => Unloaded += OnUnloaded, () => Unloaded -= OnUnloaded);
            registrations.AddCleanup(controlComposition.Dispose);
            registrations.AddCleanup(_analysisState.DisposeAnalysisWork);
            registrations.AddCleanup(_infoPanelStatisticsScheduler.Dispose);
            registrations.AddCleanup(CancelPendingInfoPanelUpdate);
            registrations.AddCleanup(_analysisRefreshScheduler.Dispose);
            registrations.AddCleanup(_viewportOverlayRefreshScheduler.Dispose);
            registrations.AddCleanup(DisposeAdvancedMeasurementDialog);
            registrations.AddCleanup(DisposeProcessCapabilityDialog);
            registrations.AddCleanup(DisposePrintableReportDialog);
            registrations.AddCleanup(DisposeDetectionRecommendations);
            return registrations;
        }
    }

    internal sealed class ImageViewerLifetime : IDisposable
    {
        private readonly ImageViewerLifetimeRegistrationCollection _registrations;
        private bool _isAttached;
        private bool _isDisposed;

        public ImageViewerLifetime(ImageViewerLifetimeRegistrationCollection registrations)
        {
            _registrations = registrations ?? throw new ArgumentNullException(nameof(registrations));
        }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _registrations.Attach();
            _isAttached = true;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _registrations.Dispose();
            _isAttached = false;
        }
    }

    internal interface IImageViewerLifetimeRegistration : IDisposable
    {
        void Attach();
    }

    internal sealed class ImageViewerLifetimeRegistrationCollection : IDisposable
    {
        private readonly List<IImageViewerLifetimeRegistration> _attachments = [];
        private readonly List<Action> _cleanupActions = [];
        private bool _isAttached;
        private bool _isDisposed;

        public void Add(IImageViewerLifetimeRegistration registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            _attachments.Add(registration);
        }

        public void AddAttachment(Action attach, Action? detach = null)
        {
            Add(new DelegatingLifetimeRegistration(attach, detach));
        }

        public void AddCleanup(Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            _cleanupActions.Add(cleanup);
        }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            var attachedRegistrations = new Stack<IImageViewerLifetimeRegistration>();
            try
            {
                foreach (IImageViewerLifetimeRegistration registration in _attachments)
                {
                    registration.Attach();
                    attachedRegistrations.Push(registration);
                }

                _isAttached = true;
            }
            catch (Exception ex)
            {
                _isAttached = false;
                List<Exception> rollbackExceptions = RollbackAttachedRegistrations(attachedRegistrations);
                if (rollbackExceptions.Count == 0)
                {
                    throw;
                }

                rollbackExceptions.Insert(0, ex);
                throw new AggregateException("Failed to attach lifetime registrations.", rollbackExceptions);
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            var exceptions = new List<Exception>();

            if (_isAttached)
            {
                for (int index = _attachments.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        _attachments[index].Dispose();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                }

                _isAttached = false;
            }

            for (int index = _cleanupActions.Count - 1; index >= 0; index--)
            {
                try
                {
                    _cleanupActions[index]();
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }

            if (exceptions.Count > 0)
            {
                throw new AggregateException("One or more lifetime registrations failed to detach or clean up.", exceptions);
            }
        }

        private static List<Exception> RollbackAttachedRegistrations(Stack<IImageViewerLifetimeRegistration> attachedRegistrations)
        {
            var rollbackExceptions = new List<Exception>();
            while (attachedRegistrations.Count > 0)
            {
                try
                {
                    attachedRegistrations.Pop().Dispose();
                }
                catch (Exception ex)
                {
                    rollbackExceptions.Add(ex);
                }
            }

            return rollbackExceptions;
        }
    }

    internal sealed class DelegatingLifetimeRegistration : IImageViewerLifetimeRegistration
    {
        private readonly Action _attach;
        private readonly Action? _detach;
        private bool _isAttached;
        private bool _isDisposed;

        public DelegatingLifetimeRegistration(Action attach, Action? detach)
        {
            _attach = attach ?? throw new ArgumentNullException(nameof(attach));
            _detach = detach;
        }

        public void Attach()
        {
            if (_isAttached)
            {
                return;
            }

            _attach();
            _isAttached = true;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            if (_isAttached)
            {
                _detach?.Invoke();
                _isAttached = false;
            }
        }
    }
}
