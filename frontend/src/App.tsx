import { lazy, Suspense, useEffect, useState } from 'react';
import { Alert, Button } from 'antd';
import { apiJson } from './apiClient';
import { apiUrl } from './endpoints';
import { useSecurityAccount } from './security';
import { useI18n } from './i18n/provider';
import ErrorBoundary from './components/ErrorBoundary';

const EditorShell = lazy(() => import('./EditorShell'));
const ProductionShell = lazy(() => import('./ProductionShell'));
const SpcDashboard = lazy(() => import('./components/SpcDashboard'));

function LoadingView() {
  const { t } = useI18n();
  return <div className="panel-loading" role="status" aria-live="polite">
    <span className="panel-loading-label">{t('app.loadingView')}</span>
  </div>;
}

type HostState = { runtimeOnly: boolean; error?: never } | { error: string; runtimeOnly?: never };

function HostView({ view, stationId, kiosk }: { view: string | null; stationId?: string; kiosk: boolean }) {
  const { enabled, user } = useSecurityAccount();
  const { t } = useI18n();
  const [host, setHost] = useState<HostState>();
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    const controller = new AbortController();
    setHost(undefined);
    apiJson(apiUrl('host-mode'), {
      signal: controller.signal, timeoutMs: 15_000, errorLabel: 'Host-mode request failed',
      parse: (raw) => {
        if (typeof raw !== 'object' || raw === null || !('runtimeOnly' in raw) || typeof raw.runtimeOnly !== 'boolean')
          throw new Error('Invalid host-mode response: expected runtimeOnly to be a boolean');
        return raw.runtimeOnly;
      },
    }).then((runtimeOnly) => {
      if (!controller.signal.aborted) setHost({ runtimeOnly });
    }).catch((error: unknown) => {
      if (!controller.signal.aborted)
        setHost({ error: error instanceof Error ? error.message : 'Host-mode request failed' });
    });
    return () => controller.abort();
  }, [attempt]);

  if (!host) return <LoadingView />;
  if (host.error !== undefined) return <Alert type="error" showIcon title={t('app.hostModeUnavailable')}
    description={host.error} action={<Button onClick={() => setAttempt((value) => value + 1)}>{t('app.retryHostMode')}</Button>} />;
  const production = host.runtimeOnly || (enabled && user?.role === 'Operator') || view === 'operator' || view === 'stations';
  return <ErrorBoundary key={production ? 'production' : 'editor'}>
    <Suspense fallback={<LoadingView />}>
      {production
        ? <ProductionShell runtimeOnly={host.runtimeOnly} initialView={view === 'stations' ? 'stations' : 'operator'}
          stationId={stationId} kiosk={kiosk} />
        : <EditorShell />}
    </Suspense>
  </ErrorBoundary>;
}

export default function App() {
  const query = new URLSearchParams(window.location.search);
  const view = query.get('view');
  if (view === 'spc') return <ErrorBoundary variant="panel" scope="SPC 看板">
    <Suspense fallback={<LoadingView />}><SpcDashboard open onClose={() => undefined} embedded /></Suspense>
  </ErrorBoundary>;
  return <HostView view={view} stationId={view === 'operator' ? query.get('stationId') ?? undefined : undefined}
    kiosk={view === 'operator' && ['1', 'true'].includes(query.get('kiosk') ?? '')} />;
}
