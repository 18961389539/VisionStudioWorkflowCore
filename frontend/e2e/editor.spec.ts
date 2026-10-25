import { test, expect, type Page } from '@playwright/test';

/**
 * 只覆盖 jsdom 无法验证的层次：真实布局、Canvas、React Flow 的命中判定与事件链。
 * 所有 /api 请求走桩——e2e 不依赖 .NET 进程，后端行为由 VisionStudio.Api.Tests 覆盖。
 */
const AUTH_DISABLED = { enabled: false, bootstrapRequired: false, autoLogin: false };

async function stubApi(page: Page): Promise<void> {
  await page.route('**/api/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path.endsWith('/auth/status')) return route.fulfill({ json: AUTH_DISABLED });
    if (path.endsWith('/host-mode')) return route.fulfill({ json: { runtimeOnly: false } });
    if (route.request().method() !== 'GET') return route.fulfill({ json: {} });
    return route.fulfill({ json: [] });
  });
}

test.describe('编辑器画布（真实浏览器）', () => {
  test('首屏渲染画布与初始流程，且无未捕获异常', async ({ page }) => {
    const pageErrors: string[] = [];
    page.on('pageerror', (error) => pageErrors.push(error.message));

    await stubApi(page);
    await page.goto('/');

    await expect(page.locator('.react-flow')).toBeVisible();
    // 初始画布载入 linearDemo（见 appEditorShared.initial），应当有多个节点。
    await expect(page.locator('.react-flow__node').first()).toBeVisible();
    expect(await page.locator('.react-flow__node').count()).toBeGreaterThan(1);

    expect(pageErrors).toEqual([]);
  });

  test('节点在真实布局中获得非零尺寸并落在画布可视区内', async ({ page }) => {
    await stubApi(page);
    await page.goto('/');

    const node = page.locator('.react-flow__node').first();
    await expect(node).toBeVisible();
    const box = await node.boundingBox();
    expect(box).not.toBeNull();
    // jsdom 里所有元素都是 0×0，只有真实布局引擎才会给出实际尺寸。
    expect(box!.width).toBeGreaterThan(20);
    expect(box!.height).toBeGreaterThan(20);

    const canvasBox = await page.locator('.react-flow').boundingBox();
    expect(canvasBox!.width).toBeGreaterThan(300);
    expect(box!.x).toBeGreaterThanOrEqual(canvasBox!.x - 1);
    expect(box!.y).toBeGreaterThanOrEqual(canvasBox!.y - 1);
  });

  test('双击节点打开节点配置面板', async ({ page }) => {
    await stubApi(page);
    await page.goto('/');

    const node = page.locator('.react-flow__node').first();
    await expect(node).toBeVisible();
    await node.dblclick();

    // 配置面板是 React.lazy 加载，首帧需要给动态 import 留出时间。
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 20_000 });
  });

  test('插件接口返回非数组时画布仍可用（契约漂移防御）', async ({ page }) => {
    // 回归护栏：这条用例最初是失败的——/api/plugins 返回对象时，工具栏标签里的
    // plugins.filter 抛错，而该处位于面板级错误边界之外，整个编辑器被顶层边界
    // 替换成错误页。修复点是 App.tsx 两处 setPlugins 的 Array.isArray 校验。
    // 先装兜底桩，再覆盖单点——Playwright 的路由匹配是后注册优先。
    await stubApi(page);
    await page.route('**/api/plugins', (route) => route.fulfill({ json: { items: 'not-an-array' } }));
    await page.goto('/');

    await expect(page.locator('.react-flow')).toBeVisible();
    await expect(page.locator('.react-flow__node').first()).toBeVisible();

    // 打不开面板不算失败；关键是画布仍在，且未被错误边界外的异常清空。
    expect(await page.locator('.react-flow__node').count()).toBeGreaterThan(1);
  });

  test('Ctrl+K 命令面板可直接加节点（真实画布命中链路）', async ({ page }) => {
    await stubApi(page);
    await page.goto('/');
    const before = await page.locator('.react-flow__node').count();

    await page.keyboard.press('Control+KeyK');
    const search = page.getByLabel('命令面板搜索');
    await expect(search).toBeVisible();
    await search.fill('阈值');
    await page.keyboard.press('Enter');

    await expect.poll(async () => page.locator('.react-flow__node').count(), { timeout: 10_000 }).toBe(before + 1);
  });

  test('切换界面语言后 <html lang> 与工具箱文案同步（i18n 接线）', async ({ page }) => {
    // 这条用例守的是"Provider 真的挂上了"：词表与 Provider 早就存在，但 main.tsx 没挂、
    // 也没有切换入口，语言只能靠手改 localStorage。jsdom 里测不到 <html lang> 的副作用链。
    await stubApi(page);
    await page.goto('/');

    const toggle = page.getByRole('button', { name: '切换界面语言' });
    await expect(toggle).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('lang', 'zh-CN');

    await toggle.click();

    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
    await expect(page.getByLabel('Search nodes')).toBeVisible();
    await expect(toggle).toHaveText('中');
    // 语言偏好必须落盘：刷新后仍是英文
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  });
});
