/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  // R12（2026-10-12 复审）：单 JS 包拆分已通过两条途径落地（实测：index 2,174→1,004 kB、gzip 664→303 kB）：
  //  · App.tsx 的低频面板 React.lazy 动态导入——每个面板独立 chunk，首屏不下载；
  //  · rolldown 默认 code splitting 的共享依赖自动分块（antd 组件级 chunk 等）。
  // 手工 vendor 分组（build.rolldownOptions.output.codeSplitting.groups）在 vite 8.3.2 + rolldown 1.2.12
  // 下实测未生效（连 codeSplitting:false 都不改变输出）——待工具链修复/文档更新后再接入，勿在此处重复尝试。
  server: {
    port: 5173,
    proxy: {
      '/api': 'http://localhost:5180',
      '/hubs': { target: 'http://localhost:5180', ws: true }
    }
  },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.{ts,tsx}']
  }
});
