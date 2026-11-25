import { defineConfig, devices } from '@playwright/test';

const testProductionBundle = process.env.VISIONSTUDIO_TEST_PRODUCTION_BUNDLE === '1';

/**
 * 端到端测试：补 jsdom 覆盖不到的层次——真实布局引擎、Canvas、React Flow 的
 * 命中判定与事件链。真实后端冒烟还验证生产告警的 SignalR 推送、确认和刷新。
 */
export default defineConfig({
  testDir: './e2e',
  // 与后端覆盖率产物同区（artifacts/ 已在 .gitignore），避免污染 frontend 根目录。
  // 门禁脚本会用 VISIONSTUDIO_E2E_OUTPUT_DIR 给每轮注入一个全新的空目录：Playwright 启动时会
  // 递归清空 outputDir，本机安全删除包装器对单批 >50 个条目要求人工确认，连跑几轮累积的
  // trace/screenshot 会让"还没开始测就红"。直接跑 npm run test:e2e 时不设该变量，行为不变。
  outputDir: process.env.VISIONSTUDIO_E2E_OUTPUT_DIR ?? '../artifacts/test-results/playwright-output',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: [
    ['list'],
    ['html', { outputFolder: '../artifacts/test-results/playwright-report', open: 'never' }]
  ],
  use: {
    // 必须用 localhost 而非 127.0.0.1：Vite 默认绑定 localhost（可能解析到 ::1），
    // 在带 HTTP 代理的环境里 127.0.0.1:4173 会被代理拦成 502。
    baseURL: 'http://localhost:4173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure'
  },
  projects: [
    {
      name: 'chromium',
      // channel:'chromium' 使用完整 Chromium 构建而非 headless shell——
      // 本机缓存里 chromium-1248 已就绪，免去额外下载。
      use: { ...devices['Desktop Chrome'], channel: 'chromium' }
    }
  ],
  webServer: {
    // 默认用 dev server；验证分包时显式启用 preview 并预先构建。
    command: testProductionBundle ? 'npx vite preview --port 4173 --strictPort' : 'npx vite --port 4173 --strictPort',
    url: 'http://localhost:4173',
    reuseExistingServer: !testProductionBundle && !process.env.CI && !process.env.VISIONSTUDIO_E2E_API,
    timeout: 120_000
  }
});
