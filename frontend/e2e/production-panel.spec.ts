import { test, expect, type Page } from '@playwright/test';

/**
 * 操作员生产台（runtimeOnly 宿主）的真浏览器回归（2026-11-13）。
 * 单测已覆盖配置编辑与轮询兜底；这里验证 jsdom 看不到的部分：
 * 真实布局下的首屏横幅、活动告警提示、告警确认请求链路，以及状态接口失败时
 * 页面降级为"状态未确认"而不是空白/崩溃。
 */
const AUTH_DISABLED = { enabled: false, bootstrapRequired: false, autoLogin: false };

const stoppedStatus = {
  state: 'Stopped',
  productionLocked: false,
  jobId: 'job-1',
  lockedJobVersion: 1,
  lockedWorkflowHash: 'workflow-hash-1',
  lockedDependencyManifestHash: 'dependency-hash-1',
  cycleCount: 12,
  okCount: 10,
  ngCount: 2,
  errorCount: 1,
  consecutiveFailures: 0,
  watchdogTrips: 0,
  lastDurationMs: 42.5,
  ptpGuard: null,
  synchronizationGuard: null
};

const productionConfig = {
  jobId: 'job-1',
  autoStart: false,
  cycleDelayMs: 25,
  maxCycleMs: 15000,
  maxConsecutiveFailures: 3,
  autoRecover: true,
  recoveryDelayMs: 1000,
  stopTimeoutMs: 5000,
  maxRecoveryAttempts: 3,
  ptpDriftGuardEnabled: true,
  ptpGuardWindowRuns: 20,
  ptpGuardMinimumEvidenceRuns: 5,
  ptpGuardMinimumReadyRate: 0.99,
  ptpGuardCheckEveryCycles: 10,
  ptpGuardFaultOnMasterClockChange: true,
  synchronizationHealthGuardEnabled: true,
  synchronizationGuardWindowRuns: 20,
  synchronizationGuardMinimumEvidenceRuns: 5,
  synchronizationGuardMaximumFailureRate: 0.05,
  synchronizationGuardMaximumFrameTimeoutRate: 0.05,
  synchronizationGuardMaxConsecutiveFailures: 2,
  synchronizationGuardMaxConsecutiveSkewViolations: 3,
  synchronizationGuardMaximumNativeFrameLossRate: 0.005,
  synchronizationGuardMaximumBufferUnderruns: 0,
  synchronizationGuardMaximumResynchronizations: 0,
  synchronizationGuardMaximumSequenceGapRate: 0.01,
  synchronizationGuardCheckEveryCycles: 5,
  synchronizationGuardUnhealthyChecksToFault: 2,
  synchronizationGuardHealthyChecksToRecover: 3,
  synchronizationGuardRecoveryTimeoutMs: 30000
};

const alarm = {
  id: 'alarm-1',
  code: 'PROD-FAULTED',
  severity: 'Critical',
  source: 'ProductionRuntime',
  message: '相机掉线',
  active: true,
  acknowledged: false,
  raisedAt: '2026-11-13T08:00:00+00:00'
};

async function stubRuntimeHost(page: Page): Promise<void> {
  // 后注册的路由优先：先装兜底桩，再覆盖生产台需要的单点。
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/status')) return route.fulfill({ json: AUTH_DISABLED });
    if (path.endsWith('/host-mode')) return route.fulfill({ json: { runtimeOnly: true } });
    if (path.endsWith('/production/status')) return route.fulfill({ json: stoppedStatus });
    if (path.endsWith('/production/config')) return route.fulfill({ json: productionConfig });
    if (path.endsWith('/production/device-actions/pending')) return route.fulfill({ status: 204, body: '' });
    if (path.endsWith('/jobs')) return route.fulfill({ json: [{ id: 'job-1', name: '检测配方', publishedVersion: 1 }] });
    if (path.endsWith('/alarms')) return route.fulfill({ json: [alarm] });
    if (route.request().method() !== 'GET') return route.fulfill({ json: {} });
    return route.fulfill({ json: [] });
  });
}

test.describe('生产操作台（真实浏览器）', () => {
  test('首屏展示生产状态与活动告警，确认告警会发出 POST 并刷新', async ({ page }) => {
    await stubRuntimeHost(page);
    await page.goto('/');

    await expect(page.locator('.production-hero')).toBeVisible();
    await expect(page.getByText('当前已停止：可以编辑生产配置').first()).toBeVisible();
    await expect(page.getByText('1 条活动告警 · 1 条严重')).toBeVisible();
    await expect(page.getByText('最近：相机掉线')).toBeVisible();

    await page.getByRole('tab', { name: /告警/ }).click();
    await expect(page.getByText('PROD-FAULTED')).toBeVisible();
    await expect(page.getByRole('cell', { name: '相机掉线' })).toBeVisible();

    const acknowledged = page.waitForRequest((request) =>
      request.method() === 'POST' && request.url().endsWith('/api/alarms/alarm-1/ack'));
    await page.getByRole('button', { name: /^确\s*认$/ }).click();
    await acknowledged;
    await expect(page.getByText('告警已确认')).toBeVisible();
  });

  test('生产状态接口失败时降级为“状态未确认”，页面不空白', async ({ page }) => {
    await stubRuntimeHost(page);
    await page.route('**/api/production/status', (route) =>
      route.fulfill({ status: 500, json: { detail: '状态服务不可用' } }));
    await page.goto('/');

    await expect(page.locator('.production-hero')).toBeVisible();
    await expect(page.getByText('生产状态尚未确认').first()).toBeVisible();
    await expect(page.getByText(/状态服务不可用/).first()).toBeVisible();
    // 关键区域仍在：配置编辑入口没有被错误状态一起清空。
    await expect(page.getByRole('tab', { name: '生产配置' })).toBeVisible();
  });
});
