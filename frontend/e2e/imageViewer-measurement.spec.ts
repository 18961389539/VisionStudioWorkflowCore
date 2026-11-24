import { test, expect, type Page } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/**
 * ImageViewer 测量几何的真浏览器回归（2026-11-23）。
 * jsdom 里 Konva Stage 是替身，命中判定、拖拽事件链与绘制提交只能在这里验证：
 * 从模板载入卡尺测宽流程 → 运行出图 → 在真实画布上拖出搜索线 → 参数回写 →
 * 应用后重新打开节点仍能读到新几何，而不是比较像素。
 */
const AUTH_DISABLED = { enabled: false, bootstrapRequired: false, autoLogin: false };
const RUN_ID = 'browser-run';
// 与引擎金样共用同一张 640×480 固定图：1×1 占位图会让画布拟合与指针坐标失真。
const SCENE_PNG = readFileSync(fileURLToPath(new URL(
  '../../backend/tests/VisionStudio.Engine.Tests/Fixtures/scratch-640x480.png', import.meta.url)));

async function stubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/status')) return route.fulfill({ json: AUTH_DISABLED });
    if (path.endsWith('/host-mode')) return route.fulfill({ json: { runtimeOnly: false } });
    if (route.request().method() !== 'GET') return route.fulfill({ json: {} });
    return route.fulfill({ json: [] });
  });
}

test.describe('ImageViewer 测量编辑（真实浏览器）', () => {
  test('在画布上重绘搜索线并写回节点参数', async ({ page }) => {
    await stubApi(page);
    // 后注册的路由优先：先兜底桩，再覆盖本次运行需要的单点。
    await page.route('**/api/run', (route) => route.fulfill({
      json: { runId: RUN_ID, success: true, previewAvailable: false, totalDurationMs: 120, nodeReports: [] }
    }));
    await page.route(`**/api/runs/${RUN_ID}/images`, (route) => route.fulfill({
      json: {
        runId: RUN_ID,
        images: [
          { nodeId: 'source', portName: 'image', width: 640, height: 480 },
          { nodeId: 'width', portName: 'image', width: 640, height: 480 }
        ]
      }
    }));
    await page.route(`**/api/runs/${RUN_ID}/nodes/source/image*`, (route) =>
      route.fulfill({ contentType: 'image/png', body: SCENE_PNG }));

    await page.goto('/');
    await expect(page.locator('.react-flow')).toBeVisible();

    // 载入带真实卡尺参数与连线坐标系的模板（startX=10/endX=220 作为回写基线）。
    await page.getByRole('button', { name: '从模板新建' }).click();
    const templateSearch = page.getByLabel('搜索流程模板');
    await expect(templateSearch).toBeVisible({ timeout: 15_000 });
    await templateSearch.fill('双边缘卡尺');
    await page.getByRole('button', { name: /双边缘卡尺测宽与公差判定/ }).click();
    await page.getByRole('button', { name: /使用「双边缘卡尺测宽与公差判定」/ }).click();

    // 首次加载的示例流程被视为未保存修改：应用模板前的确认里选择丢弃。
    const discard = page.getByRole('button', { name: '丢弃修改' });
    await expect.poll(async () => {
      if (await page.locator('.react-flow__node[data-id="width"]').isVisible()) return 'applied';
      if (await discard.isVisible()) { await discard.click(); return 'discard'; }
      return 'waiting';
    }, { timeout: 20_000 }).not.toBe('waiting');

    const caliperNode = page.locator('.react-flow__node[data-id="width"]');
    await expect(caliperNode).toBeVisible({ timeout: 20_000 });

    const runDone = page.waitForResponse((response) =>
      response.url().endsWith('/api/run') && response.request().method() === 'POST');
    await page.getByRole('button', { name: /^运\s*行$/ }).click();
    await runDone;

    await caliperNode.dblclick();
    await expect(page.getByRole('dialog')).toBeVisible({ timeout: 20_000 });

    // 节点配置弹窗是 React.lazy 加载，查看器就绪后工具栏才出现“重绘搜索线”。
    const drawButton = page.getByRole('button', { name: '重绘搜索线' });
    await expect(drawButton).toBeVisible({ timeout: 30_000 });
    const stage = page.locator('.viewer-stage').first();
    await expect.poll(async () => (await stage.boundingBox())?.width ?? 0, { timeout: 20_000 }).toBeGreaterThan(200);

    const startXInput = page.getByRole('spinbutton', { name: '起点 X' });
    const endXInput = page.getByRole('spinbutton', { name: '终点 X' });
    expect(Number(await startXInput.inputValue())).toBeCloseTo(10, 6);
    expect(Number(await endXInput.inputValue())).toBeCloseTo(220, 6);

    await drawButton.click();
    await expect(page.getByLabel('搜索线绘制提示')).toBeVisible();
    // 提示条插入后画布行会被重新分配高度并重新适配，坐标必须按新尺寸量。
    await expect.poll(async () => (await stage.boundingBox())?.height ?? 0, { timeout: 10_000 }).toBeGreaterThan(80);
    const stageBox = (await stage.boundingBox())!;

    // 画面居中拖拽：适应窗口后图像位于画布中心，40%→60% 必然落在图像内部。
    const dragY = stageBox.y + stageBox.height / 2;
    const from = { x: stageBox.x + stageBox.width * 0.4, y: dragY };
    const to = { x: stageBox.x + stageBox.width * 0.6, y: dragY };
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move((from.x + to.x) / 2, dragY, { steps: 4 });
    await page.mouse.move(to.x, to.y, { steps: 4 });
    await expect(page.getByLabel('搜索线绘制尺寸')).toContainText(/长度 [1-9]\d*\.\d+ px/);
    await page.mouse.up();

    await expect(page.getByRole('button', { name: '撤销测量编辑' })).toBeVisible();
    const drawnStartX = Number(await startXInput.inputValue());
    const drawnEndX = Number(await endXInput.inputValue());
    expect(drawnEndX - drawnStartX).toBeGreaterThan(100);

    // AntD 会在两个汉字的按钮文案间插空格（“应 用”），用正则容忍。
    await page.getByRole('button', { name: /^应\s*用$/ }).click();
    await expect(page.getByRole('dialog')).toBeHidden();

    // 参数回写：重新打开节点读到的必须是应用后的几何，而不是打开弹窗时的旧值。
    await caliperNode.dblclick();
    await expect(page.getByRole('dialog')).toBeVisible({ timeout: 20_000 });
    await expect.poll(async () => Number(await page.getByRole('spinbutton', { name: '起点 X' }).inputValue())).toBeCloseTo(drawnStartX, 2);
    expect(Number(await page.getByRole('spinbutton', { name: '终点 X' }).inputValue())).toBeCloseTo(drawnEndX, 2);
  });
});