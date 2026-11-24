import { test, expect, type Page } from '@playwright/test';

/**
 * 二维手眼向导的真浏览器回归（2026-11-23）。
 * jsdom 已有向导单测；这里补的是真实布局下的完整链路：命令面板加节点 → 设备菜单开标定 →
 * 手眼页签录入/预检 → 求解响应渲染残差读数 → 导出参数写入节点 → 节点配置里读回。
 */
const AUTH_DISABLED = { enabled: false, bootstrapRequired: false, autoLogin: false };

const solveResponse = {
  pose: { x: 10, y: 20, thetaDeg: 5 },
  translationRms: 0.01,
  angleRmsDeg: 0.02,
  maxTranslationError: 0.09,
  maxAngleErrorDeg: 0.5,
  worstSampleIndex: 1,
  outlierSampleIndex: 1,
  converged: true,
  iterations: 5,
  conditionNumber: 120,
  stdDevX: 0.001,
  stdDevY: 0.002,
  angleStdDevDeg: 0.01,
  crossCheckTranslationDelta: 0.0001,
  crossCheckAngleDeltaDeg: 0.001,
  // 与向导默认的 4 对样本一一对应：索引 1 同时是最差与离群样本。
  sampleResidualsJson: JSON.stringify([
    { index: 0, translationError: 0.004, angleErrorDeg: 0.01, robot: { x: 0, y: 0, thetaDeg: -20 }, vision: { x: 0, y: 0, thetaDeg: 10 } },
    { index: 1, translationError: 0.09, angleErrorDeg: 0.5, robot: { x: 40, y: 10, thetaDeg: -5 }, vision: { x: 12, y: -3, thetaDeg: 25 } },
    { index: 2, translationError: 0.006, angleErrorDeg: 0.02, robot: { x: 80, y: -20, thetaDeg: 15 }, vision: { x: 28, y: 8, thetaDeg: 45 } },
    { index: 3, translationError: 0.008, angleErrorDeg: 0.03, robot: { x: -10, y: 50, thetaDeg: 30 }, vision: { x: -5, y: 20, thetaDeg: 60 } }
  ]),
  summary: { mode: 'eyeInHand' },
  mode: 'eyeInHand'
};

async function stubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/status')) return route.fulfill({ json: AUTH_DISABLED });
    if (path.endsWith('/host-mode')) return route.fulfill({ json: { runtimeOnly: false } });
    if (path.endsWith('/calibration/wizard/handeye/solve')) return route.fulfill({ json: solveResponse });
    if (route.request().method() !== 'GET') return route.fulfill({ json: {} });
    return route.fulfill({ json: [] });
  });
}

test.describe('手眼标定向导（真实浏览器）', () => {
  test('从命令面板添加手眼节点，求解并把手眼参数写回节点', async ({ page }) => {
    await stubApi(page);
    await page.goto('/');
    await expect(page.locator('.react-flow')).toBeVisible();

    // Ctrl+K 监听随首个 effect 挂载；打开前没有可观察信号，沿用“已打开就通过，否则再按一次”。
    const search = page.getByLabel('命令面板搜索');
    await expect.poll(async () => {
      if (await search.isVisible()) return true;
      await page.keyboard.press('Control+KeyK');
      await page.waitForTimeout(150);
      return search.isVisible();
    }, { timeout: 15_000 }).toBe(true);
    await search.fill('二维手眼');
    const nodeRow = page.getByRole('option', { name: /二维手眼标定/ });
    await expect(nodeRow).toBeVisible({ timeout: 20_000 });
    await nodeRow.click();

    const node = page.locator('.react-flow__node').filter({ hasText: '二维手眼标定' });
    await expect(node).toBeVisible({ timeout: 20_000 });

    await page.getByRole('button', { name: /^设备/ }).click();
    await page.getByRole('menuitem', { name: '标定', exact: true }).click();

    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 20_000 });
    await expect(page.getByRole('tab', { name: '手眼向导', exact: true })).toBeVisible();
    await page.getByRole('tab', { name: '手眼向导', exact: true }).click();

    await page.getByRole('button', { name: '下一步：录入样本' }).click();
    await expect(page.getByText(/样本预检：机器人角度覆盖/)).toBeVisible();
    await expect(page.getByText('满足求解条件（≥4 对互异位姿，角度覆盖 ≥8°，平移跨度 ≥0.001）。')).toBeVisible();

    const solveRequest = page.waitForRequest((request) =>
      request.method() === 'POST' && request.url().endsWith('/api/calibration/wizard/handeye/solve'));
    await page.getByRole('button', { name: /求解手眼（4 对）/ }).click();
    await solveRequest;

    // 读数来自求解响应而非像素：残差 Alert 与样本标签能证明响应已接入真实 DOM。
    // 带“· 已收敛”避开同文案的 success toast，否则严格模式会命中两个元素。
    await expect(page.getByText(/平移 RMS 0\.0100 · 角度 RMS 0\.020° · 已收敛/)).toBeVisible();
    await expect(page.getByText('最差', { exact: true })).toBeVisible();
    await expect(page.getByText('离群', { exact: true })).toBeVisible();

    await page.getByRole('button', { name: /导出到节点（calibration\.handEye2D）/ }).click();
    await expect(page.getByText('已写入 handEye2D 节点参数')).toBeVisible();

    await page.locator('.ant-modal-close').first().click();
    await expect(dialog).toBeHidden();

    // 参数回写：节点配置里的“位姿对 JSON”应包含向导默认的 4 对样本。
    await node.dblclick();
    await expect(page.getByRole('dialog')).toBeVisible({ timeout: 20_000 });
    const samplesText = page.getByRole('textbox', { name: '位姿对 JSON' });
    await expect(samplesText).toBeVisible();
    const persisted = JSON.parse(await samplesText.inputValue()) as {
      samples: Array<{ robot: { x: number; y: number; thetaDeg: number } }>;
    };
    expect(persisted.samples).toHaveLength(4);
    expect(persisted.samples[1].robot.x).toBe(40);
    expect(persisted.samples[3].robot.y).toBe(50);
  });
});