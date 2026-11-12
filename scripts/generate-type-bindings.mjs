#!/usr/bin/env node
/**
 * 生成前端类型与后端契约的编译期绑定（2026-11-12）。
 *
 * 背景：`frontend/src/types.ts` 是**手写的 UI 视图**，刻意比后端 DTO 宽松（可选/可空），
 * 因此不能简单替换成 `ContractSchema<'X'>`——上一轮为了 strict 编译已经按真实可空性放宽过，
 * 那是必要的，不是重复。真正的问题是：手写视图与后端之间**没有任何机制**保证字段名还一致。
 * 后端把 `haltNodeId` 改成 `haltedNodeId`，tsc 不会报错，直到运行时读到 undefined。
 *
 * 这个脚本生成 `src/generated/typeBindings.ts`，对每个同名类型断言：
 *
 *     keyof 手写 extends keyof 后端
 *
 * 语义刻意单向：
 * - 后端**新增**字段 → 断言仍成立（前端不需要立刻跟上）；
 * - 后端**删除/改名**字段而手写还在用 → 断言失败，tsc 直接红。
 *
 * 三个类型豁免（见 EXEMPT），因为它们是刻意的 UI 扩展/交叉类型。
 *
 * 用法：`npm run generate:type-bindings`（改了 openapi.json 或 types.ts 之后跑）。
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '..');
const openapiPath = resolve(root, 'frontend/src/generated/openapi.json');
const typesPath = resolve(root, 'frontend/src/types.ts');
const outputPath = resolve(root, 'frontend/src/generated/typeBindings.ts');

const spec = JSON.parse(readFileSync(openapiPath, 'utf8'));
const schemas = spec.components?.schemas ?? {};
const source = readFileSync(typesPath, 'utf8');

/** 故意保留的 UI 扩展：这些类型的键集不是后端子集，且原因明确。 */
const EXEMPT = {
  PortDescriptor: 'UI 注入 label（localizeCatalog 渲染时填显示名，后端不返回）',
  OfflineReplayResult: '交叉类型 RunResult & {...}，继承的视图字段（errorCode/engine 等）不在该 schema 内',
  WorkflowModuleParameter: '交叉类型 ParameterDescriptor & {...}，继承的视图字段（advanced/variants 等）不在该 schema 内'
};

/** 从 `export type X =` 起，做括号/引号感知扫描，返回顶层 `;` 的位置。 */
function typeEnd(text, start) {
  let i = start;
  let depth = 0;
  let quote = null;
  while (i < text.length) {
    const ch = text[i];
    if (quote) {
      if (ch === '\\') { i += 2; continue; }
      if (ch === quote) quote = null;
      i += 1;
      continue;
    }
    if (ch === '"' || ch === "'" || ch === '`') quote = ch;
    else if ('{[(<'.includes(ch)) depth += 1;
    else if ('}])>'.includes(ch)) depth -= 1;
    else if (ch === ';' && depth <= 0) return i + 1;
    i += 1;
  }
  throw new Error('unterminated type at ' + start);
}

const declarations = [];
for (const match of source.matchAll(/^export type (\w+)\s*=\s*/gm)) {
  const name = match[1];
  if (!(name in schemas)) continue;
  const bodyStart = match.index + match[0].length;
  const end = typeEnd(source, bodyStart);
  const body = source.slice(bodyStart, end).trim();
  declarations.push({ name, body });
}

const bound = [];
const exempted = [];
const skipped = [];
for (const { name, body } of declarations) {
  if (body.startsWith("ContractSchema<")) { skipped.push(name); continue; }
  if (name in EXEMPT) { exempted.push(name); continue; }
  // 只处理对象/交叉类型；纯 union 的 keyof 语义不同（会得到成员交集），断言没有意义。
  if (!body.startsWith('{') && !body.includes('& {')) { skipped.push(name); continue; }
  bound.push(name);
}

const lines = [
  '// 由 scripts/generate-type-bindings.mjs 生成，不要手改。',
  '// 重新生成：npm run generate:type-bindings（改了 openapi.json 或 types.ts 之后）。',
  '//',
  '// 手写 UI 视图与后端契约的编译期绑定：每个断言要求「手写类型的键集是后端键集的子集」。',
  '// 后端新增字段不会红；后端删除/改名字段而手写还在用会红——这正是镜像漂移最贵的形态。',
  "import type { components } from './api-types';",
  "import type {"
];
for (const name of bound) lines.push(`  ${name},`);
lines.push("} from '../types';");
lines.push('');
lines.push('type Assert<T extends true> = T;');
lines.push('type KeysOf<T> = keyof T;');
lines.push('');
for (const name of bound) {
  lines.push(`type B_${name} = components['schemas']['${name}'];`);
  // 必须 export：oxlint 的 no-unused-vars 会把未被引用的 type alias 判成死声明。
  // 这些别名本身就是"被 tsc 使用"的断言，没有运行时引用点。
  lines.push(`export type K_${name} = Assert<KeysOf<${name}> extends KeysOf<B_${name}> ? true : false>;`);
}
lines.push('');
lines.push('// 以下类型刻意不绑定（键集不是后端子集），原因见 scripts/generate-type-bindings.mjs 的 EXEMPT：');
for (const name of exempted) lines.push(`//  - ${name}: ${EXEMPT[name]}`);
lines.push('');

writeFileSync(outputPath, lines.join('\n'), 'utf8');
console.log(`type bindings: ${bound.length} bound, ${exempted.length} exempt, ${skipped.length} already ContractSchema/union`);
