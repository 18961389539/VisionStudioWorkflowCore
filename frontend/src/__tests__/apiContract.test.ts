/**
 * API 契约快照测试。
 *
 * 快照：frontend/src/generated/openapi.json（由 scripts/export-openapi.ps1 从后端导出）。
 * 目的：把手写镜像（src/types.ts）与后端 DTO 之间的漂移变成可执行断言——
 *   1) 前端依赖的骨架字段必须在 spec 里存在；
 *   2) 尚未进入 spec 的核心域 DTO 用"只减不增"的棘轮记录，补齐一个就更新一次基线。
 * 刷新快照：pwsh scripts/export-openapi.ps1
 */
import { describe, expect, it } from 'vitest';
import raw from '../generated/openapi.json?raw';

type Schema = { required?: string[]; properties?: Record<string, unknown>; type?: string };
type Spec = { openapi: string; components: { schemas: Record<string, Schema> } };
const spec = JSON.parse(raw) as Spec;
const schemas = spec.components.schemas;

/**
 * 端点以匿名对象返回、尚未声明 Produces<T> 的核心域 DTO。
 * 每补齐一个：后端补响应类型 → 重导快照 → 从本基线删除该名字。
 * 若名字已进入 spec 但基线未更新，测试会失败——这正是提醒。
 */
const PENDING_SPEC_DTOS = ['RunResult', 'NodeCatalogItem', 'DeviceDescriptor', 'RobotDescriptor', 'AlarmRecord', 'JobDescriptor', 'TracePage'] as const;

describe('API contract snapshot', () => {
  it('快照可解析且覆盖后端路由模型', () => {
    expect(spec.openapi).toMatch(/^3\./);
    expect(Object.keys(schemas).length).toBeGreaterThanOrEqual(100);
  });

  it('工作流文档骨架与后端 schema 一致（保存/加载最高频契约）', () => {
    const schema = schemas.WorkflowDefinition;
    expect(schema, '缺少 WorkflowDefinition：后端类型改名后必须同步此测试与前端文档处理').toBeTruthy();
    expect([...(schema.required ?? [])].sort()).toEqual(['edges', 'id', 'name', 'nodes']);
    for (const field of ['id', 'name', 'nodes', 'edges', 'schemaVersion'])
      expect(Object.keys(schema.properties ?? {}), `WorkflowDefinition 缺少前端依赖的字段 ${field}`).toContain(field);
  });

  it('尚未进入 spec 的核心域 DTO 名单只减不增（棘轮）', () => {
    const stillMissing = PENDING_SPEC_DTOS.filter((name) => !(name in schemas));
    expect(stillMissing, '有 DTO 进入了 spec：请从 PENDING_SPEC_DTOS 基线删除对应名字；若出现新的缺失项说明后端移除了响应类型').toEqual([...PENDING_SPEC_DTOS]);
  });
});
