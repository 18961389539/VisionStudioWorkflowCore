/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
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
    // jsdom 下多次完整渲染的用例在满载时接近 5s 默认期限（120 顶点多边形、圆几何联动等），
    // 统一放宽到 15s；断言未放宽，只是避免负载造成的假失败。
    testTimeout: 15_000,
    // 覆盖率棘轮：基线在此记录，阈值只升不降。断言未覆盖的 UI 分支不计入
    // （types.ts 为纯类型声明，generated 为机器生成，均无运行时语句）。
    coverage: {
      provider: 'v8',
      reporter: ['text-summary', 'json-summary', 'lcov'],
      reportsDirectory: '../artifacts/test-results/frontend-coverage',
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
      // 阈值取整数并各留约 1% 余量，只为拦住退化，不制造 flaky。棘轮：只升不降。
      thresholds: {
        statements: 68,
        branches: 65,
        functions: 58,
        lines: 73
      }
    }
  }
});
