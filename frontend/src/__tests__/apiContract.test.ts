/**
 * API 契约快照测试。
 *
 * 快照：frontend/src/generated/openapi.json（由 scripts/export-openapi.ps1 从后端导出）。
 * 目的：把手写镜像（src/types.ts）与后端 DTO 之间的漂移变成可执行断言——
 *   1) 前端依赖的骨架字段必须在 spec 里存在；
 *   2) 核心域 DTO 必须持续留在 spec 里：端点漏掉 Produces<T>() 会让整条类型生成链断掉。
 * 刷新快照：pwsh scripts/export-openapi.ps1（需先 dotnet build）
 */
import { describe, expect, it } from 'vitest';
import raw from '../generated/openapi.json?raw';

type Schema = { required?: string[]; properties?: Record<string, unknown>; type?: string };
type Spec = { openapi: string; components: { schemas: Record<string, Schema> } };
const spec = JSON.parse(raw) as Spec;
const schemas = spec.components.schemas;

/**
 * 通过端点 `Produces<T>()` 进入 spec 的核心域 DTO。
 *
 * 2026-10-23（P1-4 第一批）：原先是"尚未进入 spec 的名单，只减不增"的棘轮，现已全部补齐，
 * 断言翻转为正向守卫——这些类型必须存在于 spec 中。后端若把端点改回匿名对象、或 DTO 改名，
 * 这里会立即失败，而这正是要继续生成 types.ts 的前提。
 *
 * 命名注意：前端手写的 RunResult 对应后端 WorkflowRunResult（spec 用后端类型名）。
 */
const REQUIRED_SPEC_DTOS = [
  'DeviceDescriptor',
  'NodeCatalogItem',
  'RobotDescriptor',
  'AlarmRecord',
  'JobDescriptor',
  'RunTraceRecord',
  'TracePage',
  'WorkflowRunResult'
] as const;

describe('API contract snapshot', () => {
  it('快照可解析且覆盖后端路由模型', () => {
    expect(spec.openapi).toMatch(/^3\./);
    // 153 为 2026-10-23 实测值（补齐核心域 DTO 前是 101）。下限只用于发现 schema 大规模丢失。
    expect(Object.keys(schemas).length).toBeGreaterThanOrEqual(150);
  });

  it('工作流文档骨架与后端 schema 一致（保存/加载最高频契约）', () => {
    const schema = schemas.WorkflowDefinition;
    expect(schema, '缺少 WorkflowDefinition：后端类型改名后必须同步此测试与前端文档处理').toBeTruthy();
    expect([...(schema.required ?? [])].sort()).toEqual(['edges', 'id', 'name', 'nodes']);
    for (const field of ['id', 'name', 'nodes', 'edges', 'schemaVersion'])
      expect(Object.keys(schema.properties ?? {}), `WorkflowDefinition 缺少前端依赖的字段 ${field}`).toContain(field);
  });

  it('核心域 DTO 必须留在 spec 中（Produces<T> 守卫）', () => {
    const missing = REQUIRED_SPEC_DTOS.filter((name) => !(name in schemas));
    expect(missing, '有 DTO 掉出 spec：检查对应端点是否漏了 Produces<T>()，或后端改名后未同步本清单').toEqual([]);
  });
});
