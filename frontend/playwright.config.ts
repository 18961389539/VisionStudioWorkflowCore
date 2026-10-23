import { defineConfig, devices } from '@playwright/test';

/**
 * 端到端测试：补 jsdom 覆盖不到的层次——真实布局引擎、Canvas、React Flow 的
 * 命中判定与事件链。后端行为由 VisionStudio.Api.Tests 覆盖，这里的 API 全部走桩，
 * 因此 e2e 不依赖 .NET 进程，可在任意机器与 CI 上稳定运行。
 */
export default defineConfig({
  testDir: './e2e',
  // 与后端覆盖率产物同区（artifacts/ 已在 .gitignore），避免污染 frontend 根目录。
  outputDir: '../artifacts/test-results/playwright-output',
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
    // dev server 而非 preview：e2e 验证的是交互与布局，不验证产物压缩；
    // 免去每次跑 e2e 都要先 build（沙箱下 build 需先清理 dist）。
    command: 'npx vite --port 4173 --strictPort',
    url: 'http://localhost:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000
  }
});
