import type { ContractDto } from './types';

type Station = ContractDto<'ProductionStationOverview'>;
type StationRollout = ContractDto<'StationRecipeRollout'>;
type Receipt = ContractDto<'RecipeDeliveryReceipt'>;

const workflowHash = 'a'.repeat(64);
const demoPrefix = 'demo-station-';

export type DemoInspectionPreview = {
  id: string;
  label: string;
  disposition: 'OK' | 'NG';
  durationMs: number;
  imageUrl: string;
};

export const productionDemoEnabled = import.meta.env.DEV;
export const isDemoStation = (station: Pick<Station, 'id'>) => station.id.startsWith(demoPrefix);

const ago = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

export function demoStations(): Station[] {
  const now = ago(0);
  return [
    {
      id: `${demoPrefix}front`, name: '前段 01 · 镜头检测', operatorUrl: '/?view=operator&station=demo-front',
      connection: 'Online', lastCheckedAt: now, lastSeenAt: ago(0.1),
      status: {
        stationId: `${demoPrefix}front`, state: 'Running', jobId: 'job-lens-inspection',
        job: { name: '镜头划痕检测', publishedVersion: 8, publishedWorkflowHash: workflowHash },
        lockedVersion: 8, lockedWorkflowHash: workflowHash, cycleCount: 1284, okCount: 1269, ngCount: 15,
        lastDisposition: 'OK', lastCycleAt: ago(0.05), activeAlarms: 1, criticalAlarms: 0, pendingAlarms: 1
      },
      target: { jobId: 'job-lens-inspection', version: 8, workflowHash },
      versionCheck: 'Matches', recipeDeliveryEnabled: true, productionControlEnabled: false
    },
    {
      id: `${demoPrefix}assembly`, name: '装配 02 · 孔位检测', operatorUrl: '/?view=operator&station=demo-assembly',
      connection: 'Online', lastCheckedAt: now, lastSeenAt: ago(0.2),
      status: {
        stationId: `${demoPrefix}assembly`, state: 'Stopped', jobId: 'job-hole-inspection',
        job: { name: '孔位与螺纹检测', publishedVersion: 5, publishedWorkflowHash: workflowHash },
        lockedVersion: null, lockedWorkflowHash: null, cycleCount: 846, okCount: 833, ngCount: 13,
        lastDisposition: 'OK', lastCycleAt: ago(2), activeAlarms: 0, criticalAlarms: 0, pendingAlarms: 0
      },
      target: { jobId: 'job-hole-inspection', version: 5, workflowHash },
      versionCheck: 'PublishedOnly', recipeDeliveryEnabled: true, productionControlEnabled: false
    },
    {
      id: `${demoPrefix}pack`, name: '包装 03 · 外观复核', operatorUrl: '/?view=operator&station=demo-pack',
      connection: 'Timeout', lastCheckedAt: now, lastSeenAt: ago(18),
      status: null, target: { jobId: 'job-package-check', version: 3, workflowHash },
      versionCheck: 'Unverified', recipeDeliveryEnabled: true, productionControlEnabled: false
    }
  ];
}

export function demoStationRollouts(): StationRollout[] {
  return [
    {
      batchId: 'demo-rollout-001', jobId: 'job-lens-inspection', jobName: '镜头划痕检测',
      sourceVersion: 8, workflowHash, createdAt: ago(4),
      delivery: { stationId: `${demoPrefix}front`, stationName: '前段 01 · 镜头检测', outcome: 'Received', localVersion: 8, detail: null }
    },
    {
      batchId: 'demo-rollout-002', jobId: 'job-hole-inspection', jobName: '孔位与螺纹检测',
      sourceVersion: 5, workflowHash, createdAt: ago(6),
      delivery: { stationId: `${demoPrefix}assembly`, stationName: '装配 02 · 孔位检测', outcome: 'Received', localVersion: 5, detail: null }
    },
    {
      batchId: 'demo-rollout-003', jobId: 'job-package-check', jobName: '包装外观复核',
      sourceVersion: 3, workflowHash, createdAt: ago(9),
      delivery: { stationId: `${demoPrefix}pack`, stationName: '包装 03 · 外观复核', outcome: 'Unconfirmed', localVersion: null, detail: 'timeout' }
    }
  ];
}

export function demoRecipeDeliveries(): Receipt[] {
  return [
    {
      id: 'demo-receipt-001', stationId: `${demoPrefix}front`, jobId: 'job-lens-inspection',
      jobName: '镜头划痕检测', sourceVersion: 8, workflowHash, localVersion: 8, requiresValidation: false,
      publishedVersion: 7, jobUpdatedAt: ago(1), receivedAt: ago(0.8)
    },
    {
      id: 'demo-receipt-002', stationId: `${demoPrefix}assembly`, jobId: 'job-hole-inspection',
      jobName: '孔位与螺纹检测', sourceVersion: 5, workflowHash, localVersion: 5, requiresValidation: true,
      publishedVersion: null, jobUpdatedAt: ago(7), receivedAt: ago(6.5)
    },
    {
      id: 'demo-receipt-003', stationId: `${demoPrefix}pack`, jobId: 'job-package-check',
      jobName: '包装外观复核', sourceVersion: 3, workflowHash, localVersion: 3, requiresValidation: false,
      publishedVersion: 3, jobUpdatedAt: ago(25), receivedAt: ago(24)
    }
  ];
}

function inspectionSvg(disposition: 'OK' | 'NG', variant: number): string {
  const defect = disposition === 'NG'
    ? `<circle cx="${338 + variant * 16}" cy="${168 - variant * 8}" r="24" fill="none" stroke="#ff4d4f" stroke-width="7"/><path d="M${319 + variant * 16} ${149 - variant * 8}l38 38m0-38-38 38" stroke="#ff4d4f" stroke-width="5"/>`
    : '';
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="640" height="360" viewBox="0 0 640 360">
    <defs><linearGradient id="bg" x2="1" y2="1"><stop stop-color="#0b1118"/><stop offset="1" stop-color="#182330"/></linearGradient></defs>
    <rect width="640" height="360" fill="url(#bg)"/>
    <rect x="150" y="55" width="340" height="250" rx="34" fill="#bbc4ca"/>
    <rect x="${184 + variant * 7}" y="${89 + variant * 5}" width="272" height="182" rx="22" fill="#dfe5e8"/>
    <circle cx="320" cy="180" r="62" fill="#f7f9fa" stroke="#52c41a" stroke-width="6"/>
    <path d="M258 180h124M320 118v124" stroke="#52c41a" stroke-width="3" opacity=".75"/>
    ${defect}
    <rect x="24" y="24" width="94" height="36" rx="8" fill="${disposition === 'OK' ? '#237804' : '#cf1322'}"/>
    <text x="71" y="48" text-anchor="middle" font-family="sans-serif" font-size="22" font-weight="700" fill="white">${disposition}</text>
    <text x="24" y="338" font-family="monospace" font-size="17" fill="#91d5ff">DEMO-${variant + 1} · 1280x720</text>
  </svg>`;
  return `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
}

export function demoInspectionPreviews(): DemoInspectionPreview[] {
  return [
    { id: 'demo-frame-1', label: '最近周期 #1284', disposition: 'OK', durationMs: 842.6, imageUrl: inspectionSvg('OK', 0) },
    { id: 'demo-frame-2', label: '最近周期 #1285', disposition: 'OK', durationMs: 836.1, imageUrl: inspectionSvg('OK', 1) },
    { id: 'demo-frame-3', label: '最近周期 #1286', disposition: 'NG', durationMs: 851.9, imageUrl: inspectionSvg('NG', 2) }
  ];
}
