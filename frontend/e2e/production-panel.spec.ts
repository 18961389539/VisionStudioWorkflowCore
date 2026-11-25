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

async function stubRuntimeHost(page: Page, runtimeOnly = true): Promise<void> {
  const recent = Array.from({ length: 8 }, (_, index) => ({
    ...alarm, id: `recent-${index}`, code: `RECOVERED-${index}`, active: false, acknowledged: true
  }));
  let acknowledged = false;
  // 后注册的路由优先：先装兜底桩，再覆盖生产台需要的单点。
  await page.route('**/api/**', async (route) => {
    const { pathname: path, searchParams } = new URL(route.request().url());
    if (path.endsWith('/auth/status')) return route.fulfill({ json: AUTH_DISABLED });
    if (path.endsWith('/host-mode')) return route.fulfill({ json: { runtimeOnly } });
    if (path.endsWith('/production/operator-layout/job-1')) return route.fulfill({
      json: { title: null, widgets: ['Result', 'Counts', 'Timing'] }, headers: { ETag: '"layout-1"' }
    });
    if (path.endsWith('/production/status')) return route.fulfill({ json: stoppedStatus });
    if (path.endsWith('/production/config')) return route.fulfill({ json: productionConfig, headers: { ETag: '"runtime-config"' } });
    if (path.endsWith('/production/device-actions/pending')) return route.fulfill({ status: 204, body: '' });
    if (path.endsWith('/jobs')) return route.fulfill({ json: [{ id: 'job-1', name: '检测配方', publishedVersion: 1 }] });
    if (path.endsWith('/alarms/summary')) return route.fulfill({
      json: { activeCount: 1, criticalCount: 1, latestActiveMessage: alarm.message, pendingCount: acknowledged ? 0 : 1 }
    });
    if (path.endsWith('/alarms')) {
      const active = [{ ...alarm, acknowledged }];
      const matching = searchParams.has('pendingOnly') ? active.filter((item) => !item.acknowledged)
        : searchParams.has('activeOnly') ? active : [...recent, ...active];
      const skip = Number(searchParams.get('skip') ?? '0');
      const take = Number(searchParams.get('take') ?? '8');
      return route.fulfill({ headers: { 'X-Total-Count': String(matching.length) }, json: matching.slice(skip, skip + take) });
    }
    if (path.endsWith('/alarms/alarm-1/ack')) {
      acknowledged = true;
      return route.fulfill({ json: { ...alarm, acknowledged } });
    }
    if (route.request().method() !== 'GET') return route.fulfill({ json: {} });
    return route.fulfill({ json: [] });
  });
}

test.describe('生产操作台（真实浏览器）', () => {
  for (const [url, runtimeOnly] of [
    ['/', true],
    ['/?view=operator', false],
    ['/?view=operator&kiosk=1', false],
    ['/?view=spc', true],
  ] as const) {
    test(`${url} 不加载编辑器模块、不请求编辑器目录且不写草稿`, async ({ page }) => {
      await stubRuntimeHost(page, runtimeOnly);
      const requested: string[] = [];
      page.on('request', (request) => requested.push(new URL(request.url()).pathname));
      await page.goto(url);
      if (url.includes('spc')) await expect(page.locator('.spc-page')).toBeVisible();
      else await expect(page.getByRole('region', { name: '操作员工作台', exact: true })).toBeVisible();
      expect(requested.filter((path) => /EditorShell|useEditor|useRunController|appEditor|@xyflow/.test(path))).toEqual([]);
      expect(requested.filter((path) => /^\/api\/(catalog|plugins)$/.test(path))).toEqual([]);
      expect(await page.evaluate(() => Object.keys(localStorage).filter((key) => /draft|canvas-annotations|node-compact/i.test(key)))).toEqual([]);
    });
  }

  test('从首屏进入待确认告警，确认后保留活动记录和分页历史', async ({ page }) => {
    await stubRuntimeHost(page);
    await page.goto('/');

    await expect(page.locator('.production-hero')).toBeVisible();
    await expect(page.getByText('当前已停止', { exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: '操作员工作台' })).toBeVisible();
    await expect(page.getByText('1 条活动告警 · 1 条严重')).toBeVisible();
    await expect(page.getByText('最近：相机掉线')).toBeVisible();

    const pendingAction = page.getByRole('button', { name: /查看待确认告警/ });
    await pendingAction.click();
    await expect(page.getByRole('tab', { name: /告警/ })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByRole('radio', { name: '待确认' })).toBeChecked();
    await expect(page.getByText('PROD-FAULTED')).toBeVisible();
    await expect(page.getByRole('cell', { name: '相机掉线' })).toBeVisible();

    const acknowledged = page.waitForRequest((request) =>
      request.method() === 'POST' && request.url().endsWith('/api/alarms/alarm-1/ack'));
    await page.getByRole('button', { name: /^确\s*认$/ }).click();
    await acknowledged;
    await expect(page.getByText('告警已确认')).toBeVisible();
    await expect(page.getByText('没有待确认的活动告警')).toBeVisible();
    await expect(pendingAction).toHaveCount(0);

    await page.getByRole('tabpanel').getByText('活动告警', { exact: true }).click();
    const alarmRow = page.getByRole('row').filter({ hasText: 'PROD-FAULTED' });
    await expect(alarmRow.getByText('已确认', { exact: true })).toBeVisible();

    await page.getByRole('tabpanel').getByText('全部', { exact: true }).click();
    await expect(page.getByText('RECOVERED-0', { exact: true })).toBeVisible();
    await expect(alarmRow).toHaveCount(0);
    await page.getByTitle('2', { exact: true }).click();
    await expect(alarmRow.getByText('已确认', { exact: true })).toBeVisible();
  });

  test('在操作员工作台确认告警并保留活动状态', async ({ page }) => {
    await stubRuntimeHost(page);
    await page.goto('/');
    const queue = page.getByRole('region', { name: '最近待确认告警' });
    await expect(queue.getByText('相机掉线')).toBeVisible();
    const acknowledged = page.waitForRequest((request) =>
      request.method() === 'POST' && request.url().endsWith('/api/alarms/alarm-1/ack'));
    await queue.getByRole('button', { name: '确认告警 相机掉线' }).click();
    await acknowledged;
    await expect(queue).toHaveCount(0);
    await expect(page.getByRole('tab', { name: '操作员' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByText('1 条活动告警 · 1 条严重')).toBeVisible();
  });

  test('在总览跟踪工位发布和运行进度，记录读取失败时保留站点状态', async ({ page }) => {
    await stubRuntimeHost(page, false);
    const hash = 'a'.repeat(64);
    const status = {
      stationId: 'line-a', state: 'Running', jobId: 'job-1',
      job: { name: '正面检测配方', publishedVersion: 2, publishedWorkflowHash: 'old' },
      lockedVersion: 2, lockedWorkflowHash: 'old', activeAlarms: 0, pendingAlarms: 0,
    };
    await page.route('**/api/production/stations', (route) => route.fulfill({ json: [
      { id: 'line-a', name: '正面工位', operatorUrl: 'https://example.com/?view=operator',
        connection: 'Online', versionCheck: 'NotConfigured', lastSeenAt: '2026-11-23T08:32:00Z', status, target: null },
      { id: 'line-b', name: '背面工位', operatorUrl: 'https://example.com/?view=operator',
        connection: 'Offline', versionCheck: 'NotConfigured', status: null, target: null },
    ] }));
    let historyOffline = false;
    await page.route('**/api/production/recipe-rollouts', (route) => route.fulfill({ json: [] }));
    await page.route('**/api/production/recipe-rollouts/latest-by-station', (route) => historyOffline
      ? route.fulfill({ status: 503, json: { detail: '工位进度暂不可用' } })
      : route.fulfill({ json: [
        { batchId: 'batch-1', jobId: 'job-1', jobName: '正面检测配方', sourceVersion: 3, workflowHash: hash, createdAt: '2026-11-23T08:31:00Z',
          delivery: { stationId: 'line-a', stationName: '正面工位', localVersion: 7, outcome: 'Received' } },
      ] }));
    await page.goto('/?view=stations');
    const overview = page.getByRole('region', { name: '多工位总览' });
    const row = overview.getByRole('row').filter({ hasText: '正面工位' });
    await expect(overview.getByRole('row').nth(1)).toContainText('背面工位');
    await expect(row.getByText('已接收 · 待发布')).toBeVisible();
    const phases = overview.getByRole('combobox', { name: '下发进度' });
    await phases.click();
    await page.getByText('待发布', { exact: true }).click();
    await expect(overview.getByText('背面工位')).toHaveCount(0);
    await phases.click();
    await page.getByText('所有下发进度', { exact: true }).click();
    await overview.getByRole('textbox', { name: '搜索工位或配方' }).fill('正面检测配方');
    await expect(overview.getByText('背面工位')).toHaveCount(0);
    status.job.publishedVersion = 7;
    status.job.publishedWorkflowHash = hash;
    await overview.getByRole('button', { name: '刷新总览' }).click();
    await expect(row.getByText('已发布 · 待切换')).toBeVisible();
    status.lockedVersion = 7;
    status.lockedWorkflowHash = hash;
    await overview.getByRole('button', { name: '刷新总览' }).click();
    await expect(row.getByText('已切换运行')).toBeVisible();
    historyOffline = true;
    await overview.getByRole('button', { name: '刷新总览' }).click();
    await expect(overview.getByText('工位下发进度未更新')).toBeVisible();
    await expect(row.getByText('在线', { exact: true })).toBeVisible();
    await expect(row.getByText('已接收 · 待核验')).toBeVisible();
  });

  test('可按配方调整操作员标题和组件顺序', async ({ page }) => {
    await stubRuntimeHost(page, false);
    let layout = { title: null as string | null, widgets: ['Result', 'Counts', 'Timing'] };
    await page.route('**/api/production/operator-layout/job-1', async (route) => {
      if (route.request().method() === 'PUT') {
        expect(route.request().headers()['if-match']).toBe('"layout-1"');
        layout = route.request().postDataJSON();
      }
      await route.fulfill({ json: layout, headers: { ETag: layout.title ? '"layout-2"' : '"layout-1"' } });
    });
    await page.goto('/?view=operator');
    await page.getByRole('button', { name: '配置操作员界面' }).click();
    const dialog = page.getByRole('dialog', { name: '配置操作员界面' });
    await dialog.getByLabel('面板标题').fill('正面视觉检测');
    await dialog.getByRole('button', { name: '上移产量与良率' }).click();
    await dialog.getByRole('checkbox', { name: '检测节拍' }).uncheck();
    await dialog.getByRole('button', { name: '保存布局' }).click();
    await expect(page.getByRole('heading', { name: '正面视觉检测' })).toBeVisible();
    await expect(page.locator('.operator-widget h3')).toHaveText(['产量与良率', '最近判定']);
  });

  test('多工位看板区分版本偏差和离线缓存', async ({ page }) => {
    await stubRuntimeHost(page);
    const stationStatus = {
      stationId: 'line-a', state: 'Running', jobId: 'job-1',
      job: { name: '检测配方', publishedVersion: 3 }, lockedVersion: 2,
      okCount: 15, ngCount: 1, activeAlarms: 0, pendingAlarms: 0
    };
    let offline = false;
    await page.route('**/api/production/stations', (route) => route.fulfill({ json: [
      { id: 'line-a', name: '正面工位', operatorUrl: 'https://station.example/?view=operator',
        connection: offline ? 'Offline' : 'Online', versionCheck: offline ? 'Unverified' : 'Mismatch',
        lastSeenAt: '2026-11-23T04:00:00Z', status: stationStatus, target: { jobId: 'job-1', version: 3 } }
    ] }));
    await page.goto('/?view=stations');
    const station = page.getByRole('row').filter({ hasText: '正面工位' });
    await expect(station.getByText('发布 V3 · 运行 V2')).toBeVisible();
    await expect(station.getByText('版本不一致')).toBeVisible();
    await expect(station.getByRole('link', { name: '打开正面工位操作台' })).toHaveAttribute('href', 'https://station.example/?view=operator');
    offline = true;
    await expect(station.getByText('离线')).toBeVisible({ timeout: 12_000 });
    await expect(station.getByText(/上次 ·/)).toBeVisible();
    await expect(station.getByText('待核验')).toBeVisible();
  });

  test('下发选定工位并显示逐站回执', async ({ page }) => {
    await stubRuntimeHost(page, false);
    const stations = ['a', 'b'].map((id) => ({
      id, name: '工位 ' + id.toUpperCase(), operatorUrl: 'https://' + id + '.example/?view=operator',
      connection: 'Online', versionCheck: 'NotConfigured', status: null, target: null, recipeDeliveryEnabled: true,
    }));
    const hash = 'a'.repeat(64);
    let sourceVersion = 3;
    let sends = 0;
    await page.route('**/api/production/stations', (route) => route.fulfill({ json: stations }));
    await page.route('**/api/jobs', (route) => route.fulfill({ json: [
      { id: 'job-1', name: '检测配方', publishedVersion: sourceVersion, versions: [{ version: sourceVersion, workflowHash: hash }] },
    ] }));
    let batches: object[] = [];
    await page.route('**/api/production/recipe-rollouts', async (route) => {
      if (route.request().method() === 'GET') return route.fulfill({ json: batches });
      sends++;
      expect(route.request().postDataJSON()).toEqual({ jobId: 'job-1', sourceVersion: 3, workflowHash: hash, stationIds: ['a', 'b'] });
      const batch = {
        id: 'batch-1', jobId: 'job-1', jobName: '检测配方', sourceVersion: 3, workflowHash: hash, createdAt: '2026-11-23T04:00:00Z',
        stations: [
          { stationId: 'a', stationName: '工位 A', outcome: 'Received', localVersion: 7 },
          { stationId: 'b', stationName: '工位 B', outcome: 'Unconfirmed', detail: 'timeout' },
        ],
      };
      batches = [batch];
      return route.fulfill({ json: batch });
    });
    await page.goto('/?view=stations');
    await page.getByRole('button', { name: '配方分批下发' }).click();
    const dialog = page.getByRole('dialog', { name: '配方分批下发' });
    await dialog.getByRole('combobox', { name: '来源配方' }).click();
    await page.getByText('检测配方 · V3', { exact: true }).click();
    await dialog.getByRole('checkbox', { name: '工位 A' }).check();
    await dialog.getByRole('checkbox', { name: '工位 B' }).check();
    await dialog.getByRole('button', { name: '下发本批' }).click();
    await expect(dialog.getByText('已接收 · 本地 V7')).toBeVisible();
    await expect(dialog.getByText('等待工位响应超时')).toBeVisible();
    await dialog.getByRole('button', { name: '重选待处理工位' }).click();
    await expect(dialog.getByRole('checkbox', { name: '工位 A' })).not.toBeChecked();
    await expect(dialog.getByRole('checkbox', { name: '工位 B' })).toBeChecked();
    sourceVersion = 4;
    await dialog.getByRole('button', { name: '刷新来源' }).click();
    await expect(dialog.getByText('来源已发布版本变化，请重新选择配方')).toBeVisible();
    await expect(dialog.getByRole('button', { name: '下发本批' })).toBeDisabled();
    expect(sends).toBe(1);
  });

  test('工位收件箱展示成熟度确认后才发布本地版本', async ({ page }) => {
    await stubRuntimeHost(page);
    const receipt = {
      id: 'delivery-1', stationId: 'line-a', jobId: 'job-1', jobName: '检测配方', sourceVersion: 8,
      localVersion: 3, workflowHash: 'c'.repeat(64), receivedAt: '2026-11-23T04:00:00Z', requiresValidation: false,
      jobUpdatedAt: '2026-11-23T04:00:01.0000001Z', publishedVersion: 2,
    };
    await page.route('**/api/production/recipe-deliveries', (route) => route.fulfill({ json: [receipt] }));
    const acknowledgements: boolean[] = [];
    await page.route('**/api/production/recipe-deliveries/delivery-1/publish', (route) => {
      const request = route.request().postDataJSON();
      expect(request.expectedUpdatedAt).toBe(receipt.jobUpdatedAt);
      acknowledgements.push(request.acknowledgeMaturityNodes);
      if (!request.acknowledgeMaturityNodes) return route.fulfill({ status: 409, json: {
        code: 'NODE_MATURITY_ACK_REQUIRED', findings: [{ nodeId: 'n1', nodeType: 'measure.thread', reason: '需现场验证' }],
      } });
      receipt.publishedVersion = receipt.localVersion;
      return route.fulfill({ json: { id: receipt.jobId, publishedVersion: receipt.localVersion } });
    });
    await page.goto('/');
    await page.getByRole('tab', { name: '配方收件箱' }).click();
    await page.getByRole('button', { name: '核对并发布' }).click();
    const dialog = page.getByRole('dialog', { name: '核对工位配方' });
    const publish = dialog.getByRole('button', { name: '发布本地版本' });
    await publish.click();
    await expect(dialog.getByText('measure.thread · 需现场验证')).toBeVisible();
    await expect(publish).toBeDisabled();
    await dialog.getByRole('checkbox', { name: '我已核对这些节点' }).check();
    await publish.click();
    await expect(page.getByRole('cell', { name: '已发布' })).toBeVisible();
    expect(acknowledgements).toEqual([false, true]);
  });

  test('操作员留图绑定生产会话并在停止后清除', async ({ page }) => {
    await stubRuntimeHost(page);
    const runningStatus = { ...stoppedStatus, state: 'Running', productionLocked: true, lockedJobVersion: 7,
      startedAt: '2026-11-23T04:00:00Z' };
    await page.route('**/api/production/status', (route) => route.fulfill({ json: runningStatus }));
    await page.route('**/api/production/operator-layout/job-1', (route) => route.fulfill({
      json: { title: '工位相机', widgets: ['Preview'] }, headers: { ETag: '"preview-layout"' },
    }));
    let retainedNg = false;
    await page.route('**/api/production/operator-preview*', (route) => {
      const ngOnly = new URL(route.request().url()).searchParams.get('ngOnly') === 'true';
      if (ngOnly && !retainedNg) return route.fulfill({ status: 204 });
      return route.fulfill({ json: ngOnly
        ? { runId: 'ng-run', jobVersion: 7, startedAt: '2026-11-23T04:00:30Z', disposition: 'NG' }
        : { runId: 'image-run', jobVersion: 7, startedAt: '2026-11-23T04:01:00Z', disposition: 'OK' } });
    });
    await page.route(/\/api\/traces\/(image-run|ng-run)\/preview/, (route) => route.fulfill({
      contentType: 'image/png',
      body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jR1sAAAAASUVORK5CYII=', 'base64'),
    }));
    await page.route('**/api/production/stop', (route) => {
      runningStatus.state = 'Stopped';
      runningStatus.productionLocked = false;
      return route.fulfill({ json: runningStatus });
    });
    await page.goto('/');
    await expect(page.getByRole('img', { name: '最近检验留图' })).toBeVisible();
    await page.getByText('最近不合格', { exact: true }).click();
    await expect(page.getByText('本次会话暂无不合格留图')).toBeVisible();
    await expect(page.getByRole('img', { name: '最近检验留图' })).toHaveCount(0);
    retainedNg = true;
    await page.getByText('最近留图', { exact: true }).click();
    await page.getByText('最近不合格', { exact: true }).click();
    await expect(page.getByRole('img', { name: '最近不合格留图' })).toBeVisible();
    await expect(page.getByRole('link', { name: '打开最近不合格留图' })).toHaveAttribute('href', '/api/traces/ng-run/preview');
    await page.getByRole('button', { name: /^停\s*止$/ }).click();
    await page.locator('.ant-popconfirm').getByRole('button', { name: /^停\s*止$/ }).click();
    await expect(page.getByText('生产会话启动后显示本次检验留图。')).toBeVisible();
    await expect(page.getByRole('img', { name: '最近不合格留图' })).toHaveCount(0);
  });

  test('生产状态接口失败时降级为“状态未确认”，页面不空白', async ({ page }) => {
    await stubRuntimeHost(page);
    await page.route('**/api/production/status', (route) =>
      route.fulfill({ status: 500, json: { detail: '状态服务不可用' } }));
    await page.goto('/');

    await expect(page.locator('.production-hero')).toBeVisible();
    await expect(page.getByText('生产状态尚未确认').first()).toBeVisible();
    await expect(page.getByText(/状态服务不可用/).first()).toBeVisible();
    await expect(page.getByRole('tab', { name: '告警' })).toBeVisible();
    await expect(page.getByRole('tab', { name: '生产配置' })).toHaveCount(0);
  });
});
