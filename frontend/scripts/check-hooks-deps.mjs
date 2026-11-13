#!/usr/bin/env node
/**
 * react/exhaustive-deps 存量棘轮（2026-11-13）。
 *
 * 为什么规则在 .oxlintrc.json 里仍是 off：一次性打开会让 65 处历史遗漏同时变红，
 * 而其中大部分是"把内联 refresh/load 加进依赖会触发死循环"的既有写法，需要按组件
 * 逐个改成 useCallback，不能机械补依赖。这里用独立统计 + 基线的方式先把它变成门禁：
 *   - 新代码产生 exhaustive-deps 问题 -> 计数超过基线 -> 失败；
 *   - 修掉一处 -> 计数下降 -> 打印提示要求下调基线（只许减）。
 *
 * 统计方式：oxlint CLI 的 `-D react/exhaustive-deps` 覆盖配置里的 off，仅启用这一条规则，
 * 输出 JSON 后按 code 过滤，避免其他规则的数量干扰基线。
 */
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const baselinePath = path.join(root, 'lint-hooks-baseline.json');
const baseline = JSON.parse(readFileSync(baselinePath, 'utf8')).exhaustiveDeps ?? 0;

const oxlintCli = path.join(root, 'node_modules/oxlint/bin/oxlint');
const result = spawnSync(process.execPath, [oxlintCli, 'src', '-D', 'react/exhaustive-deps', '-f', 'json'], {
  cwd: root,
  encoding: 'utf8'
});

const output = `${result.stdout ?? ''}${result.stderr ?? ''}`;
const start = output.indexOf('{');
if (start < 0) {
  console.error('check-hooks-deps: oxlint produced no JSON output.');
  console.error(output.trim());
  process.exit(1);
}

let report;
try {
  report = JSON.parse(output.slice(start));
} catch (error) {
  console.error(`check-hooks-deps: failed to parse oxlint JSON (${error instanceof Error ? error.message : error}).`);
  process.exit(1);
}

const diagnostics = (report.diagnostics ?? [])
  .filter((item) => item.code === 'react-hooks(exhaustive-deps)');

const byFile = new Map();
for (const item of diagnostics) byFile.set(item.filename, (byFile.get(item.filename) ?? 0) + 1);

console.log('== Hooks dependency baseline ==');
console.log(`react/exhaustive-deps: ${diagnostics.length} finding(s), baseline ${baseline}`);

if (diagnostics.length > baseline) {
  console.error('');
  for (const [file, count] of [...byFile.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))) {
    console.error(`  ${file}: ${count}`);
  }
  console.error('');
  console.error('New exhaustive-deps findings exceed the baseline. Fix them, or explicitly lower the baseline after fixing older ones.');
  process.exit(1);
}

if (diagnostics.length < baseline) {
  console.log(`hint: baseline can be lowered from ${baseline} to ${diagnostics.length} (frontend/lint-hooks-baseline.json)`);
}

console.log('Hooks dependency baseline PASSED');
