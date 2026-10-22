/// <reference types="vite/client" />
import { describe, expect, it } from 'vitest';

/**
 * 统一请求层的棘轮：新代码必须走 apiClient，不能新增直接 fetch 调用。
 * 运行时代码已全部迁移；基线为 0，新增直接调用会失败。
 * 通过 Vite 的 raw glob 读取源码，避免测试依赖 node 类型。
 */
const DIRECT_FETCH_BASELINE = 0;

/**
 * 路径常量棘轮：运行时代码不允许出现硬编码 '/api/...' 或 '${API_BASE}'，
 * 必须从 endpoints.ts 的常量与 builder 取路径（endpoints.ts 本身除外）。
 * 2026-10-22 全量收敛完成，基线为 0。
 */
const HARDCODED_API_PATH_BASELINE = 0;

const sources = import.meta.glob('/src/**/*.{ts,tsx}', {
  query: '?raw',
  import: 'default',
  eager: true
}) as Record<string, string>;

describe('API request layer ratchet', () => {
  it(`does not add direct fetch calls (baseline ${DIRECT_FETCH_BASELINE})`, () => {
    const offenders = Object.entries(sources)
      .filter(([path]) => !path.includes('/__tests__/') && !path.endsWith('/apiClient.ts'))
      .map(([path, content]) => ({ path, count: (content.match(/(?<![\w.])fetch\(/g) ?? []).length }))
      .filter((entry) => entry.count > 0);
    const total = offenders.reduce((sum, entry) => sum + entry.count, 0);
    const detail = offenders
      .sort((left, right) => right.count - left.count)
      .slice(0, 8)
      .map((entry) => `${entry.count} ${entry.path}`)
      .join('\n');

    expect(total, `直接 fetch 调用增加到 ${total} 处，请改用 apiClient 的 apiFetch/apiJson：\n${detail}`)
      .toBeLessThanOrEqual(DIRECT_FETCH_BASELINE);
  });

  it(`does not hardcode /api/ paths outside endpoints.ts (baseline ${HARDCODED_API_PATH_BASELINE})`, () => {
    const offenders = Object.entries(sources)
      .filter(([path]) => !path.includes('/__tests__/') && !path.endsWith('/endpoints.ts'))
      .map(([path, content]) => ({ path, count: (content.match(/['"`]\/api\/|\$\{API_BASE\}/g) ?? []).length }))
      .filter((entry) => entry.count > 0);
    const total = offenders.reduce((sum, entry) => sum + entry.count, 0);
    const detail = offenders
      .sort((left, right) => right.count - left.count)
      .slice(0, 8)
      .map((entry) => `${entry.count} ${entry.path}`)
      .join('\n');

    expect(total, `硬编码 /api/ 路径增加到 ${total} 处，请改用 endpoints.ts 的常量与 builder：\n${detail}`)
      .toBeLessThanOrEqual(HARDCODED_API_PATH_BASELINE);
  });
});
