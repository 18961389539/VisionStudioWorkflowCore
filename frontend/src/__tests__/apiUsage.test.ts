/// <reference types="vite/client" />
import { describe, expect, it } from 'vitest';

/**
 * 统一请求层的棘轮：新代码必须走 apiClient，不能新增直接 fetch 调用。
 * 已有 150 处历史调用按文件逐步迁移；每迁完一批，把这个基线下调。
 * 通过 Vite 的 raw glob 读取源码，避免测试依赖 node 类型。
 */
const DIRECT_FETCH_BASELINE = 150;

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
});
