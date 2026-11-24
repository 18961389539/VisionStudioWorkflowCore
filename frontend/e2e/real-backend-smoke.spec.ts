import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

// 这组用例共用隔离宿主；生产告警用例会短暂改变运行状态。
test.describe.configure({ mode: 'default' });

/**
 * 真后端冒烟（2026-11-12）。
 *
 * 为什么需要它：`editor.spec.ts` / `recipe-publication.spec.ts` 都把 `/api/**` 桩掉了——
 * 那测的是"前端拿到某种 JSON 时的表现"，不是"前后端在运行时能对上"。契约保障目前只有
 * 编译期对账（`apiContractParity.ts`），而手写镜像类型 + 编译期断言无法发现
 * "端点存在但运行时 500 / 字段名改了但类型没改 / 追溯根本没落库"。
 *
 * 这一条用例走完整链路：
 *   健康 → 自动登录 → 节点目录 → 真执行（3 节点流程）→ 追溯落库 → 节点 span 可见。
 * 最后一段同时验证遥测闭环（埋点 → NodeSpanBuffer → 诊断端点），这是纯单测拿不到的。
 *
 * 运行方式：
 *   - 门禁：`./scripts/quality-gate.ps1 -Scope E2E` 会先拉起宿主（127.0.0.1:5199）；
 *   - 手工：先起 API（Development + Security:AutoLoginAdmin=true），再
 *     `VISIONSTUDIO_E2E_API=http://127.0.0.1:5199 npx playwright test real-backend-smoke`。
 * 手工运行时请使用隔离数据根。显式配置的宿主不可达会使真实后端用例失败。
 */

const API = process.env.VISIONSTUDIO_E2E_API ?? 'http://127.0.0.1:5199';

/** 最小可执行流程：合成图 → 阈值 → 斑点，不依赖磁盘上的图像文件。 */
function linearWorkflow() {
  const stamp = Date.now();
  return {
    id: `e2e-smoke-${stamp}`,
    name: `E2E 真后端冒烟 ${stamp}`,
    nodes: [
      {
        id: 'source-1',
        type: 'image.synthetic',
        name: 'Synthetic Image',
        position: { x: 60, y: 150 },
        parameters: { width: 640, height: 480, centerX: 320, centerY: 240, radius: 80 }
      },
      {
        id: 'threshold-1',
        type: 'image.threshold',
        name: 'Threshold',
        position: { x: 360, y: 150 },
        parameters: { threshold: 128 }
      },
      {
        id: 'blob-1',
        type: 'measure.blobs',
        name: 'Largest Blob',
        position: { x: 680, y: 150 },
        parameters: { minArea: 1000, mode: 'largest' }
      }
    ],
    edges: [
      { id: 'c1', sourceNodeId: 'source-1', sourcePort: 'next', targetNodeId: 'threshold-1', targetPort: 'exec', kind: 'control' },
      { id: 'd1', sourceNodeId: 'source-1', sourcePort: 'image', targetNodeId: 'threshold-1', targetPort: 'image', kind: 'data' },
      { id: 'c2', sourceNodeId: 'threshold-1', sourcePort: 'next', targetNodeId: 'blob-1', targetPort: 'exec', kind: 'control' },
      { id: 'd2', sourceNodeId: 'threshold-1', sourcePort: 'image', targetNodeId: 'blob-1', targetPort: 'image', kind: 'data' }
    ]
  };
}

/** 检查 Development 宿主并登录；未显式配置宿主时允许跳过。 */
async function connect(request: APIRequestContext): Promise<void> {
  let health;
  try {
    health = await request.get(`${API}/healthz`, { timeout: 3000 });
  } catch (error) {
    if (process.env.VISIONSTUDIO_E2E_API) throw error;
    test.skip(true, `真后端未启动（${API}）。用 ./scripts/quality-gate.ps1 -Scope E2E 或手工启动宿主后再跑。`);
    return;
  }
  expect(health.ok(), '宿主 /healthz 必须可达').toBeTruthy();

  const status = await request.get(`${API}/api/auth/status`);
  expect(status.ok()).toBeTruthy();
  const securityStatus = (await status.json()) as { enabled: boolean; autoLogin: boolean };
  expect(securityStatus.enabled, '冒烟需要启用安全（否则绕过的是另一条代码路径）').toBeTruthy();
  expect(securityStatus.autoLogin, '宿主必须以 Security:AutoLoginAdmin=true 启动').toBeTruthy();

  const login = await request.post(`${API}/api/auth/auto-login`);
  expect(login.ok(), 'auto-login 失败：检查 ASPNETCORE_ENVIRONMENT=Development 与 AutoLoginAdmin').toBeTruthy();
}

test('真后端：登录 → 目录 → 运行 → 追溯 → 节点 span 闭环', async ({ request }) => {
  await connect(request);

  // 节点目录：真实 registry，不是快照文件。哈希头是前端缓存键，缺了说明中间件链路被改过。
  const catalogResponse = await request.get(`${API}/api/catalog`);
  expect(catalogResponse.ok()).toBeTruthy();
  const catalog = (await catalogResponse.json()) as Array<{ type: string }>;
  expect(catalog.length, '节点目录条数不应低于 200').toBeGreaterThan(200);
  expect(catalog.some(node => node.type === 'image.synthetic')).toBeTruthy();
  expect(catalogResponse.headers()['x-visionstudio-catalog-hash']).toBeTruthy();

  // 4) 真执行：这是"编译期契约"完全覆盖不到的一段——节点注册、参数校验、OpenCV、调度器都在这条路径上。
  const workflow = linearWorkflow();
  const run = await request.post(`${API}/api/run`, { data: workflow });
  const runBody = await run.text();
  expect(run.ok(), `POST /api/run 失败：${runBody.slice(0, 500)}`).toBeTruthy();
  const result = JSON.parse(runBody) as {
    runId: string;
    success: boolean;
    error?: string | null;
    nodeReports: unknown[];
  };
  expect(result.success, `流程执行失败：${result.error ?? 'no error message'}`).toBeTruthy();
  expect(result.runId).toBeTruthy();
  expect(result.nodeReports.length, '三个节点都应产出执行报告').toBe(3);

  // 5) 追溯：执行成功但没落库等于"现场查不到"，这是最容易被静默跳过的一步。
  const traceResponse = await request.get(`${API}/api/traces/${result.runId}`);
  expect(traceResponse.ok()).toBeTruthy();
  const trace = (await traceResponse.json()) as {
    runId: string;
    workflowId: string;
    executionStatus: string;
    disposition: string;
    nodeCount: number;
  };
  expect(trace.runId).toBe(result.runId);
  expect(trace.workflowId).toBe(workflow.id);
  expect(trace.executionStatus).toBe('Complete');
  expect(trace.disposition).toBe('OK');
  expect(trace.nodeCount).toBe(3);

  // 6) 遥测闭环：埋点（ActivitySource）→ 消费者（NodeSpanBuffer）→ 端点。
  //    只有这一段能证明"引擎确实产生了 span 并且有人收"，纯单测只能证明缓冲本身可用。
  const spansResponse = await request.get(`${API}/api/diagnostics/node-spans?recent=64`);
  expect(spansResponse.ok()).toBeTruthy();
  const spans = (await spansResponse.json()) as {
    captured: number;
    recent: Array<{ runId?: string | null; nodeType: string }>;
  };
  expect(spans.captured, '本次进程至少应捕获这 3 个节点的 span').toBeGreaterThanOrEqual(3);
  expect(
    spans.recent.some(span => span.runId === result.runId),
    `节点 span 应带本次 runId（最近节点：${spans.recent.map(s => s.nodeType).join(', ')}）`
  ).toBeTruthy();

  // 7) Prometheus 出口（2026-11-13）：没有这一条，指标端点可以静默 404/500 很久没人发现。
  const metrics = await request.get(`${API}/api/diagnostics/metrics`);
  expect(metrics.ok()).toBeTruthy();
  const metricsText = await metrics.text();
  expect(metricsText).toContain('visionstudio_node_spans_captured_total');
  expect(metricsText).toContain('node_type="image.threshold"');
  expect(metricsText).toContain('visionstudio_production_state{state="Stopped"}');
});

test('真后端：创建配方 → 发布 → 作业运行 → 追溯带 jobId', async ({ request }) => {
  await connect(request);
  const workflow = linearWorkflow();
  const jobId = `e2e-job-${Date.now()}`;

  const create = await request.post(`${API}/api/jobs`, {
    data: { id: jobId, name: workflow.name, description: 'E2E 发布链路', workflow, note: 'smoke' }
  });
  expect(create.ok(), `POST /api/jobs 失败：${(await create.text()).slice(0, 400)}`).toBeTruthy();

  const publish = await request.post(`${API}/api/jobs/${jobId}/versions/1/publish`);
  expect(publish.ok(), `发布失败：${(await publish.text()).slice(0, 400)}`).toBeTruthy();
  const published = (await publish.json()) as { publishedVersion: number };
  expect(published.publishedVersion).toBe(1);

  const run = await request.post(`${API}/api/jobs/${jobId}/run`);
  expect(run.ok(), `作业运行失败：${(await run.text()).slice(0, 400)}`).toBeTruthy();
  const result = (await run.json()) as { runId: string; success: boolean; error?: string | null };
  expect(result.success, `作业执行失败：${result.error ?? 'no error message'}`).toBeTruthy();

  const traceResponse = await request.get(`${API}/api/traces/${result.runId}`);
  expect(traceResponse.ok()).toBeTruthy();
  const trace = (await traceResponse.json()) as { jobId?: string | null; jobVersion?: number | null; executionStatus: string };
  expect(trace.jobId).toBe(jobId);
  expect(trace.jobVersion).toBe(1);
  expect(trace.executionStatus).toBe('Complete');
});

test('真后端：失败流程返回失败报告并落一条 Failed 追溯', async ({ request }) => {
  await connect(request);
  const stamp = Date.now();
  const workflow = {
    id: `e2e-fail-${stamp}`,
    name: `E2E 失败流程 ${stamp}`,
    nodes: [{
      id: 'load-1',
      type: 'image.load',
      name: 'Out-of-root Load',
      position: { x: 60, y: 150 },
      parameters: { path: 'C:\\definitely-not-an-authorized-root\\missing.png' }
    }],
    edges: []
  };

  const run = await request.post(`${API}/api/run`, { data: workflow });
  const body = await run.text();
  // 执行失败可能映射成 4xx（契约要求的"执行完成但结果为失败"），但绝不能是 5xx；
  // 响应体仍必须是可解析的 WorkflowRunResult，而不是 ProblemDetails。
  expect(run.status(), `POST /api/run 不应 5xx：${body.slice(0, 400)}`).toBeLessThan(500);
  const result = JSON.parse(body) as {
    runId: string;
    success: boolean;
    error?: string | null;
    nodeReports: Array<{ success: boolean; error?: string | null }>;
  };
  expect(result.success).toBeFalsy();
  expect(result.nodeReports).toHaveLength(1);
  expect(result.nodeReports[0].success).toBeFalsy();
  expect(result.runId).toBeTruthy();

  const traceResponse = await request.get(`${API}/api/traces/${result.runId}`);
  expect(traceResponse.ok()).toBeTruthy();
  const trace = (await traceResponse.json()) as { executionStatus: string; disposition: string; error?: string | null };
  expect(trace.executionStatus).toBe('Failed');
  expect(trace.disposition).toBe('ERROR');
  expect(trace.error ?? '').not.toBe('');
});

function observeAlarmRevisions(page: Page): Set<string> {
  const revisions = new Set<string>();
  page.on('websocket', (socket) => {
    if (!new URL(socket.url()).pathname.endsWith('/hubs/monitor')) return;
    socket.on('framereceived', ({ payload }) => {
      for (const frame of payload.toString().split('\u001e').filter(Boolean)) {
        const message = JSON.parse(frame);
        const revision = message.arguments?.[0]?.alarmRevision;
        if (message.target === 'monitorSnapshot' && typeof revision === 'string') revisions.add(revision);
      }
    });
  });
  return revisions;
}

async function openProductionPanel(page: Page) {
  await page.getByRole('button', { name: /生产/ }).click();
  await page.getByRole('menuitem', { name: '生产运行' }).click();
  return page.getByRole('dialog', { name: '生产运行' });
}

test('真后端：生产故障推送告警，操作员确认后刷新仍显示已确认', async ({ page, request }) => {
  test.skip(!process.env.VISIONSTUDIO_E2E_API, '请通过 E2E 门禁启动隔离宿主，或显式提供隔离宿主的 URL。');
  await connect(request);

  const revisions = observeAlarmRevisions(page);
  await page.goto('/');
  const panel = await openProductionPanel(page);
  await expect(panel.locator('.production-hero')).toBeVisible();
  await expect.poll(() => revisions.size).toBeGreaterThan(0);

  const peer = await page.context().newPage();
  try {
    const peerRevisions = observeAlarmRevisions(peer);
    await peer.goto('/');
    await openProductionPanel(peer);
    await expect.poll(() => peerRevisions.size).toBeGreaterThan(0);
  } finally {
    await peer.close();
  }
  await panel.getByRole('tab', { name: /告警/ }).click();

  const initialRevisions = new Set(revisions);
  const jobId = `e2e-missing-${crypto.randomUUID()}`;
  const alarmRow = panel.getByRole('row').filter({ hasText: jobId });
  await expect(alarmRow).toHaveCount(0);
  try {
    // 不存在的配方走常规启动失败路径，由后端生成真实告警，无需连接设备。
    const start = await request.post(`${API}/api/production/start`, { data: { jobId } });
    expect(start.status(), await start.text()).toBe(404);
    await expect.poll(() => [...revisions].some((revision) => !initialRevisions.has(revision)), { timeout: 7000 })
      .toBe(true);
    await expect(alarmRow.getByRole('cell', { name: 'PROD-START' })).toBeVisible({ timeout: 7000 });

    const acknowledged = page.waitForResponse((response) =>
      response.request().method() === 'POST' && /\/api\/alarms\/[^/]+\/ack$/.test(new URL(response.url()).pathname));
    await alarmRow.getByRole('button', { name: /^确\s*认$/ }).click();
    expect((await acknowledged).ok()).toBeTruthy();
    await expect(alarmRow.getByText('已确认', { exact: true })).toBeVisible();

    await page.reload();
    const reopened = await openProductionPanel(page);
    await reopened.getByRole('tab', { name: /告警/ }).click();
    await expect(reopened.getByRole('row').filter({ hasText: jobId }).getByText('已确认', { exact: true })).toBeVisible();
  } finally {
    const stop = await request.post(`${API}/api/production/stop`);
    expect(stop.ok(), await stop.text()).toBeTruthy();
  }
});
