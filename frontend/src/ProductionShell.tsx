import { lazy, Suspense, useState } from 'react';
import { Button } from 'antd';
import { SecurityAccountBadge, useSecurityAccount } from './security';
import ProductionPanel from './components/ProductionPanel';
import ErrorBoundary from './components/ErrorBoundary';

const DiagnosticsPanel = lazy(() => import('./components/DiagnosticsPanel'));
const StoragePanel = lazy(() => import('./components/StoragePanel'));
const SecurityAdminPanel = lazy(() => import('./components/SecurityAdminPanel'));

type Props = { runtimeOnly: boolean; initialView: 'operator' | 'stations'; stationId?: string; kiosk: boolean };

export default function ProductionShell({ runtimeOnly, initialView, stationId, kiosk }: Props) {
  const { enabled, user } = useSecurityAccount();
  const canAdmin = !enabled || user?.role === 'Administrator';
  const [diagnosticsOpen, setDiagnosticsOpen] = useState(false);
  const [storageOpen, setStorageOpen] = useState(false);
  const [securityOpen, setSecurityOpen] = useState(false);
  return <>
    <ErrorBoundary variant="panel" scope="生产运行">
      <ProductionPanel open onClose={() => undefined} embedded runtimeOnly={runtimeOnly} kiosk={kiosk}
        stationId={stationId} initialView={initialView}
        headerActions={!kiosk ? <>
          {canAdmin && <Button onClick={() => setSecurityOpen(true)}>安全管理</Button>}
          {canAdmin && <Button onClick={() => setStorageOpen(true)}>存储与备份</Button>}
          <Button type="primary" ghost onClick={() => setDiagnosticsOpen(true)}>诊断</Button>
          <SecurityAccountBadge />
        </> : undefined} />
    </ErrorBoundary>
    {!kiosk && diagnosticsOpen && <ErrorBoundary variant="panel" scope="诊断" onDismiss={() => setDiagnosticsOpen(false)}>
      <Suspense fallback={<div className="panel-loading" role="status" aria-live="polite"><span className="panel-loading-label">正在加载诊断面板…</span></div>}>
        <DiagnosticsPanel open={diagnosticsOpen} onClose={() => setDiagnosticsOpen(false)} />
      </Suspense>
    </ErrorBoundary>}
    {!kiosk && canAdmin && storageOpen && <ErrorBoundary variant="panel" scope="存储维护" onDismiss={() => setStorageOpen(false)}>
      <Suspense fallback={<div className="panel-loading" role="status" aria-live="polite"><span className="panel-loading-label">正在加载存储维护…</span></div>}>
        <StoragePanel open={storageOpen} onClose={() => setStorageOpen(false)} />
      </Suspense>
    </ErrorBoundary>}
    {!kiosk && canAdmin && securityOpen && <ErrorBoundary variant="panel" scope="安全与运维管理" onDismiss={() => setSecurityOpen(false)}>
      <Suspense fallback={<div className="panel-loading" role="status" aria-live="polite"><span className="panel-loading-label">正在加载安全与运维管理…</span></div>}>
        <SecurityAdminPanel open={securityOpen} onClose={() => setSecurityOpen(false)} />
      </Suspense>
    </ErrorBoundary>}
  </>;
}
