/// <reference types="vitest/config" />
import os from 'node:os';
import path from 'node:path';
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // Vite/Vitest 的磁盘缓存（2026-11-12）：默认落在 node_modules/.vite 与
  // node_modules/.vitest-cache，连续跑几轮门禁会攒出上千个小文件；Vitest 开跑前清理它们时
  // 会撞上本机安全删除守卫（单批 >50 个条目要求人工确认），报成 "Startup Error" 而不是测试失败。
  // 系统临时目录是守卫明确放行的范围，缓存本来也是一次性中间物。
  cacheDir: path.join(os.tmpdir(), 'visionstudio-vite-cache'),
  // R12（2026-10-12 复审）：首屏拆分已通过两条途径落地：
  //  · App.tsx 的低频面板 React.lazy 动态导入——每个面板独立 chunk，首屏不下载；
  //  · rolldown 默认 code splitting 的共享依赖自动分块（antd 组件级 chunk 等）。
  //
  // 2026-10-23 复核（vite 8.3.2 + rolldown 1.2.12）：早前"手工分组不生效"的结论有误，
  // 当时把配置写在了 build.rollupOptions 的旧层级。正确写法是 build.rolldownOptions.output.
  // codeSplitting.groups（rolldown 的 advancedChunks 已废弃）。实测对照：
  //  · codeSplitting:false → 单包 3,571 kB（证明该键确实被传给打包器）；
  //  · 下列 vendor 分组 → index 2,179→1,548 kB，首屏请求 15→7，首屏 gzip 持平 923 kB。
  // 收益不在减小首屏字节，而在缓存粒度：业务代码改动不会让 vendor 的 ~310 kB (antd) 失效。
  build: {
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            // react 优先匹配，避免被依赖它的 antd 组吃掉。
            { name: 'vendor-react', test: /node_modules[\\/](react|react-dom|scheduler)[\\/]/, priority: 40 },
            { name: 'vendor-antd', test: /node_modules[\\/](antd|@ant-design|@rc-component|rc-[^\\/]+)[\\/]/, priority: 30 },
            { name: 'vendor-konva', test: /node_modules[\\/](konva|react-konva)[\\/]/, priority: 30 },
            { name: 'vendor-xyflow', test: /node_modules[\\/]@xyflow[\\/]/, priority: 30 },
            { name: 'vendor-signalr', test: /node_modules[\\/]@microsoft[\\/]signalr[\\/]/, priority: 30 }
          ]
        }
      }
    }
  },
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5180',
      '/hubs': { target: 'http://localhost:5180', ws: true }
    }
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}'],
    // 单元测试默认按中文渲染：预装 catalog.zh 词表，避免组件断言看到英文回落（2026-11-13）。
    setupFiles: ['./src/testSetup.ts'],
    // 与 cacheDir 同理：Vitest 5 的 fsModuleCache 默认写 node_modules/.vitest-cache，
    // 单独指到系统临时目录，避免任何一处缓存清理撞上批量删除守卫。
    fsModuleCachePath: path.join(os.tmpdir(), 'visionstudio-vitest-module-cache'),
    // jsdom 下多次完整渲染的用例在满载时接近默认期限（120 顶点多边形、圆几何联动等）。
    // 2026-11-12：CI 的 2~4 vCPU runner 上 15s 仍反复超时，放宽到 30s；只是把"卡住才失败"
    // 的上限拉长，断言口径一点没松。hook 同理，避免 beforeEach 在慢 runner 上先超时。
    testTimeout: 30_000,
    hookTimeout: 30_000,
    // 8 → 4（2026-11-12）：CI runner 上 8 个 jsdom worker 抢 CPU 是超时的另一半原因；
    // 28 核本机也不再拉满 27 个 worker，换来稳定门禁，全量覆盖率仍在一两分钟内完成。
    maxWorkers: 4,
    // 覆盖率棘轮：基线在此记录，阈值只升不降。断言未覆盖的 UI 分支不计入
    // （types.ts 为纯类型声明，generated 为机器生成，均无运行时语句）。
    coverage: {
      provider: 'v8',
      reporter: ['text-summary', 'json-summary', 'lcov'],
      // 覆盖率产物落在**系统临时目录**（2026-11-12），不再写进仓库的 artifacts/。
      //
      // v8 provider 有两处递归删除：开跑前 clean reportsDirectory、以及开跑前/收尾都删内部的
      // `.tmp`（每个测试文件一个 json，100+ 个小文件）。这两处都不受配置控制——`clean: false`
      // 只挡得住前者，`.tmp` 是无条件删的。在本机它们会撞上安全删除守卫：条目数超过阈值要求人工
      // 确认（SAFE_DELETE_BULK_CONFIRM_REQUIRED），vitest 直接以 unhandled error 退出，日志只剩
      // 一行 RUN，看起来像测试自己崩了。
      //
      // 临时目录是守卫明确放行的范围（换成直接删除），而覆盖率产物本来就是一次性的中间物，
      // 放临时目录语义上也更对。质量门禁在跑完后把 coverage-summary.json / lcov.info 两份
      // **机器可读摘要**复制回 artifacts/test-results/frontend-coverage/ 留档（复制不是删除，
      // 不触发任何守卫）。阈值门禁由 vitest 内部按下面的 thresholds 判定，与报告位置无关。
      reportsDirectory: path.join(os.tmpdir(), 'visionstudio-frontend-coverage'),
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/__tests__/**',
        'src/generated/**',
        'src/main.tsx',
        'src/types.ts',
        'src/vite-env.d.ts'
      ],
      // 2026-10-23 首次基线（83 文件 / 1091 项全绿实测）：
      //   statements 69.29% / branches 65.99% / functions 59.35% / lines 74.02%
      // 2026-10-29 复核（91 文件 / 1138 项全绿实测）：
      //   statements 69.73% / branches 66.32% / functions 60.47% / lines 74.34%
      // 阈值取整数并各留约 1~2% 余量，只为拦住退化，不制造 flaky。棘轮：只升不降。
      // 本次只收紧 functions（58 → 59，实测 60.47）；其余三项已各留 1.3~2.5 点，
      // 再抬 1 点就会把余量压到 0.3 以内，等于用 flaky 换覆盖率数字。
      thresholds: {
        statements: 68,
        branches: 65,
        functions: 59,
        lines: 73
      }
    }
  }
});
