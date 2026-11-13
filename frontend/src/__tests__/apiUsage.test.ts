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

/**
 * 静默失败棘轮（2026-11-13）：列表/验证类请求失败时不得用 `if (!response.ok) return;`
 * 把后端故障渲染成"没有数据"。仅允许三处附加信息路径（失败时可退回摘要/跳过提示），
 * 以及 DataErrorAlert 文档里引用的反例。修好一处就要同步删掉对应白名单，否则测试会失败。
 */
const SILENT_FAILURE_ALLOWLIST: Record<string, string> = {
  '/src/components/CalibrationIntrinsicsWizard.tsx': '补全提示是附加信息，失败不打断标定向导',
  '/src/components/useNodeConfigurationDialogModel.ts': '节点耗时提示是附加信息，失败不打断配置',
  '/src/components/WorkflowModulePanel.tsx': '模块详情失败时退回摘要，下次打开重试',
  '/src/components/DataErrorAlert.tsx': '组件文档中引用的反例'
};

const sources = import.meta.glob('/src/**/*.{ts,tsx}', {
  query: '?raw',
  import: 'default',
  eager: true
}) as Record<string, string>;

describe('API request layer ratchet', () => {
  it(`does not add direct fetch calls (baseline ${DIRECT_FETCH_BASELINE})`, () => {
    const offenders = Object.entries(sources)
      .filter(([path]) => !path.includes('/__tests__/') && !path.includes('/src/generated/') && !path.endsWith('/apiClient.ts'))
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
      .filter(([path]) => !path.includes('/__tests__/') && !path.includes('/src/generated/') && !path.endsWith('/endpoints.ts'))
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

  it('does not turn failed responses into empty data without an error state', () => {
    const pattern = /if\s*\(\s*!response\.ok\s*\)\s*return\s*;/g;
    const offenders = Object.entries(sources)
      .filter(([path]) => path.startsWith('/src/components/'))
      .map(([path, content]) => ({ path, count: (content.match(pattern) ?? []).length }))
      .filter((entry) => entry.count > 0);

    const unexpected = offenders.filter((entry) => !(entry.path in SILENT_FAILURE_ALLOWLIST));
    expect(
      unexpected,
      `新增了把请求失败渲染成空数据的写法：${unexpected.map((entry) => entry.path).join(', ')}。请用 DataErrorAlert 呈现可重试错误。`
    ).toEqual([]);

    const allowlisted = offenders.filter((entry) => entry.path in SILENT_FAILURE_ALLOWLIST)
      .map((entry) => entry.path)
      .sort();
    expect(
      allowlisted,
      '静默失败白名单已过期：对应文件不再包含该写法，请删除白名单条目。'
    ).toEqual(Object.keys(SILENT_FAILURE_ALLOWLIST).sort());
  });
});
