import { describe, expect, it } from 'vitest';
import openapiRaw from '../generated/openapi.json?raw';

// 用 Vite 的 ?raw 读取源文件：这样测试同时跑在应用 tsconfig 与 vitest 下，
// 不需要给测试项目引入 @types/node（node:fs / process 会在 tsc -b 里报错）。
// 走 import.meta.glob 而不是 `import x from './x.ts?raw'`：后者会让 oxlint 去解析真实模块，
// 报"没有 default export"（实际是 Vite 的 raw 转换），属于工具误报。
const sources = import.meta.glob(['../types.ts', '../generated/apiContractParity.ts'], {
  query: '?raw',
  import: 'default',
  eager: true
}) as Record<string, string>;
const typesSource = sources['../types.ts'];
const paritySource = sources['../generated/apiContractParity.ts'];

/**
 * 契约对账覆盖率棘轮（2026-11-12）。
 *
 * `generated/apiContractParity.ts` 用类型断言证明"后端响应能喂给前端手写类型"。
 * 2026-11-12 之前有 45 个与 OpenAPI schema 同名的手写 DTO 完全没有对账，而缺口不可见：
 * 新增 DTO 时没人知道该不该补断言。本轮把其中 40 个补进编译期断言，只留下
 * `WorkflowModuleParameter`（前端把 parameters.type 收窄成封闭联合，schema 是宽 string，
 * 属于有意收窄，不能直接 `Backend extends Frontend`）。
 *
 * 这个测试守的是"缺口只许变小"：
 * - 同名 DTO 必须有 `Parity_<Name>`（或特殊命名的 *Parity 类型），否则失败；
 * - 列在 PENDING 里的必须仍然未覆盖——补上断言后要删掉；
 * - PENDING 只许变短（当前 0 条）。
 *
 * 2026-11-13 收尾：最后一条 `WorkflowModuleParameter` 已补断言（摘掉刻意收窄的 `type`
 * 后断言其余字段全等），PENDING 归零。此后再出现同名 DTO 未对账会直接失败，
 * 没有"先登记再说"的缓冲带了。
 */
const COVERED_BY_SPECIAL_TYPE = new Set(['NodeCatalogItem', 'NodeRunReport', 'ParameterDescriptor', 'RunTraceRecord']);

/** 已确认尚未对账的同名 DTO。补一条 Parity_ 就从这里删一条；只许减不许增。 */
const PENDING = new Set<string>();

function namesIn(source: string, pattern: RegExp): Set<string> {
  const names = new Set<string>();
  for (const match of source.matchAll(pattern)) names.add(match[1]);
  return names;
}

describe('契约对账覆盖率', () => {
  it('每个与 OpenAPI schema 同名的手写 DTO 都有对账断言或明确的待办', () => {
    const openapi = JSON.parse(openapiRaw) as { components: { schemas: Record<string, unknown> } };
    const declared = namesIn(typesSource, /export (?:type|interface) (\w+)/g);
    const schemas = new Set(Object.keys(openapi.components.schemas));
    const parityNames = namesIn(paritySource, /Parity_(\w+)\s*=/g);

    const overlap = [...declared].filter((name) => schemas.has(name));
    const uncovered = overlap.filter((name) => !parityNames.has(name) && !COVERED_BY_SPECIAL_TYPE.has(name));

    const missingFromPending = uncovered.filter((name) => !PENDING.has(name));
    expect(missingFromPending, '新增同名 DTO 需要补 Parity_ 断言，或（暂不收敛时）加入 PENDING 并说明原因').toEqual([]);

    const stale = [...PENDING].filter((name) => !uncovered.includes(name));
    expect(stale, 'PENDING 里的名字已被覆盖或已改名，请从清单删除').toEqual([]);

    // 棘轮：只许变短。数字变了要在提交说明里写明补了哪些 DTO。
    expect(PENDING.size).toBeLessThanOrEqual(0);
  });
});
