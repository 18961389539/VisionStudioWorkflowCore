import { test, expect, type Page } from '@playwright/test';

/**
 * 发布/回滚的真浏览器护栏（2026-11-12）。
 *
 * 为什么必须在真实浏览器里跑：发布对话框的"409 门禁 → 勾选 → 重提"是一次多步写入，
 * jsdom 里 checkbox 的命中区、Modal 的焦点陷阱和按钮 disabled 的视觉态都是模拟的。
 * 这条链路一旦回归，工程师看到的是"发布失败"或更糟——误以为门禁已过而重复提交。
 *
 * 所有 /api 走桩：e2e 不依赖 .NET 进程，后端语义由 VisionStudio.Api.Tests 覆盖。
 */
const AUTH_DISABLED = { enabled: false, bootstrapRequired: false, autoLogin: false };
const NOW = '2026-11-12T06:00:00.1234567+00:00';
const NEXT = '2026-11-12T06:01:00.1234567+00:00';

function recipe(publishedVersion: number | null) {
  return {
    id: 'A',
    name: '划痕检测配方',
    description: '',
    createdAt: NOW,
    updatedAt: publishedVersion === 2 ? NEXT : NOW,
    latestVersion: 2,
    publishedVersion,
    versions: [1, 2].map((version) => ({
      version,
      workflowHash: `hash-${version}`,
      createdAt: NOW,
      note: '',
      published: version === publishedVersion,
      validation: { validationRunId: 'validation-1', datasetId: 'dataset-1', linkedAt: NOW, accepted: true, reason: '' }
    })),
    publicationHistory: []
  };
}

async function stubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/status')) return route.fulfill({ json: AUTH_DISABLED });
    if (path.endsWith('/host-mode')) return route.fulfill({ json: { runtimeOnly: false } });
    if (route.request().method() !== 'GET') return route.fulfill({ json: {} });
    return route.fulfill({ json: [] });
  });
}

test('含 Experimental 节点的配方：409 门禁→勾选→发布成功', async ({ page }) => {
  let published = false;
  const publishRequests: string[] = [];

  await stubApi(page);
  await page.route('**/api/jobs', (route) => route.fulfill({ json: [recipe(published ? 2 : 1)] }));
  await page.route('**/api/jobs/A', (route) => route.fulfill({ json: recipe(published ? 2 : 1) }));
  await page.route('**/api/jobs/A/parameters', (route) => route.fulfill({ json: { values: {} } }));
  await page.route('**/api/jobs/A/versions/*/diff/*', (route) => route.fulfill({
    json: {
      jobId: 'A', fromVersion: 1, toVersion: 2, fromHash: 'hash-1', toHash: 'hash-2',
      changed: true, changes: [{ kind: 'Parameter', path: 'threshold', before: '120', after: '96' }]
    }
  }));
  await page.route('**/api/jobs/A/versions/2/publish*', async (route) => {
    publishRequests.push(route.request().url());
    if (!route.request().url().includes('acknowledgeMaturityNodes=true')) {
      return route.fulfill({
        status: 409,
        json: {
          code: 'NODE_MATURITY_ACK_REQUIRED',
          message: '该版本包含未达到 Stable 的节点，确认后才会发布。',
          findings: [{
            nodeId: 'infer', nodeType: 'inference.obb', nodeName: 'OBB 推理',
            maturity: 'Experimental', reason: '没有可用的模型包，端到端推理路径未被断言。'
          }]
        }
      });
    }
    published = true;
    return route.fulfill({ json: recipe(2) });
  });

  await page.goto('/');
  await expect(page.locator('.react-flow')).toBeVisible();

  // 顶栏「生产」→「配方管理」
  await page.getByRole('button', { name: /生产/ }).click();
  await page.getByRole('menuitem', { name: '配方管理' }).click();
  await expect(page.getByRole('dialog', { name: '配方管理' })).toBeVisible();

  await page.getByRole('button', { name: '发布 V2' }).click();

  // 第一次确认：后端返回 409，门禁面板出现且确认按钮必须不可点
  const publishDialog = page.getByRole('dialog', { name: /发布 划痕检测配方 V2/ });
  await expect(publishDialog).toBeVisible();
  await expect(publishDialog.getByText('当前生效 V1 → 目标 V2')).toBeVisible();
  await publishDialog.getByRole('button', { name: '确认发布' }).click();

  await expect(publishDialog.getByText('OBB 推理')).toBeVisible();
  await expect(publishDialog.getByText('没有可用的模型包，端到端推理路径未被断言。')).toBeVisible();
  const confirm = publishDialog.getByRole('button', { name: '确认发布' });
  await expect(confirm).toBeDisabled();
  expect(publishRequests).toHaveLength(1);

  // 勾选后才放行，且重提必须带上显式确认参数
  await publishDialog.getByRole('checkbox', { name: /我已了解上述节点尚未达到 Stable/ }).check();
  await expect(confirm).toBeEnabled();
  await confirm.click();

  await expect(publishDialog).toBeHidden({ timeout: 15_000 });
  await expect(page.getByText('配方 划痕检测配方 已生效 → V2')).toBeVisible();
  expect(publishRequests).toHaveLength(2);
  expect(publishRequests[1]).toContain('acknowledgeMaturityNodes=true');
});
