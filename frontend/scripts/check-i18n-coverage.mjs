#!/usr/bin/env node
/**
 * 界面文案本地化的量化守卫（2026-11-26）。
 *
 * 背景：英文模式由顶栏 EN 按钮暴露给用户，但界面文案实际有三套写法并存——
 *  1. `t('namespace.key')`  —— 词表驱动，可审计；
 *  2. `text('中文', 'English')` —— 组件内联双语，渲染正确但无法被工具统计；
 *  3. 裸中文字面量 —— 英文模式下原样显示中文。
 * 前两套在英文下都正确，第三套是真实缺口。此前无人统计，缺口一直在扩大。
 *
 * 本脚本按**确定的规则**把每个含中文的位置归入上述三类，产出可比较的基线：
 *   - 统计口径只看"非注释的中文字面量"，注释里的中文不计；
 *   - 只要同一行出现 `text(` 且含中文，即归为内联双语（保守估计，宁可少报缺口）；
 *   - 其余含中文的行归为裸中文（真实缺口）。
 *
 * 用法：
 *   node scripts/check-i18n-coverage.mjs            # 打印当前数据并与基线比较
 *   node scripts/check-i18n-coverage.mjs --update   # 写入/更新基线
 *   node scripts/check-i18n-coverage.mjs --list     # 列出裸中文位置（供分批迁移）
 *
 * 门禁语义与覆盖率棘轮一致：数字只能降，不能升。新增裸中文会让 `--check` 失败。
 */
import { readdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const srcRoot = path.join(root, 'src');
const baselinePath = path.join(root, 'scripts/i18n-baseline.json');

const CJK = /[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]/;
const SKIP_DIRS = new Set(['__tests__', 'generated', 'demos']);
// 词表文件本身就是「已翻译」的载体，不能被算成缺口；
// 其余 i18n 运行时文件（provider / index / lang / catalog）仍参与统计。
const SKIP_FILES = new Set(['testSetup.ts', 'vite-env.d.ts', 'messages.ts', 'catalog.zh.ts', 'catalogLabels.ts']);

function* walk(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      yield* walk(path.join(dir, entry.name));
      continue;
    }
    if (!/\.(ts|tsx)$/.test(entry.name)) continue;
    if (SKIP_FILES.has(entry.name)) continue;
    yield path.join(dir, entry.name);
  }
}

/** 移除块注释（含 JSX 的 {/* ... *\/}），保留换行以维持行号。 */
function stripBlockComments(source) {
  return source.replace(/\/\*[\s\S]*?\*\//g, (match) => match.replace(/[^\n]/g, ' '));
}

/**
 * 逐行分类。跳过整行 `//` 注释——行尾注释仍参与统计，
 * 因为区分「行尾注释」与「字符串里的 //」需要完整词法分析，宁可多报不漏报。
 */
function classify(source) {
  const lines = stripBlockComments(source).split('\n');
  const result = { inlineBilingual: 0, hardcoded: 0, hardcodedLines: [], bilingualLines: [] };

  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    if (!CJK.test(line)) continue;
    if (/^\s*\/\//.test(line)) continue;

    if (/\btext\s*\(/.test(line)) {
      result.inlineBilingual += 1;
      result.bilingualLines.push(index + 1);
    } else {
      result.hardcoded += 1;
      result.hardcodedLines.push(index + 1);
    }
  }

  return result;
}

const files = [];
const scannedFiles = new Set();
for (const file of walk(srcRoot)) {
  const relative = path.relative(root, file).replace(/\\/g, '/');
  scannedFiles.add(relative);
  const source = readFileSync(file, 'utf8');
  const stats = classify(source);
  if (stats.hardcoded === 0 && stats.inlineBilingual === 0) continue;
  files.push({
    file: relative,
    hardcoded: stats.hardcoded,
    inlineBilingual: stats.inlineBilingual,
    hardcodedLines: stats.hardcodedLines
  });
}

files.sort((a, b) => b.hardcoded - a.hardcoded || a.file.localeCompare(b.file));

const totals = files.reduce(
  (acc, item) => ({
    hardcoded: acc.hardcoded + item.hardcoded,
    inlineBilingual: acc.inlineBilingual + item.inlineBilingual
  }),
  { hardcoded: 0, inlineBilingual: 0 }
);

// 词表规模：key 数决定「已迁移」这一侧的分子。
const messagesSource = readFileSync(path.join(srcRoot, 'i18n/messages.ts'), 'utf8');
const messageKeys = (messagesSource.match(/^\s{2}'[^']+':\s*\{/gm) ?? []).length;

const current = {
  generatedAt: new Date().toISOString().slice(0, 10),
  messageKeys,
  inlineBilingual: totals.inlineBilingual,
  hardcoded: totals.hardcoded,
  files: files.map(({ file, hardcoded, inlineBilingual }) => ({ file, hardcoded, inlineBilingual }))
};

const mode = process.argv.includes('--update') ? 'update'
  : process.argv.includes('--list') ? 'list'
  : 'check';

if (mode === 'list') {
  for (const item of files) {
    if (item.hardcoded === 0) continue;
    console.log(`${item.file}  (${item.hardcoded})`);
    console.log(`  行: ${item.hardcodedLines.join(', ')}`);
  }
  process.exit(0);
}

if (mode === 'update') {
  writeFileSync(baselinePath, `${JSON.stringify(current, null, 2)}\n`, 'utf8');
  console.log(`基线已写入 ${path.relative(root, baselinePath)}`);
  console.log(`  词表 key      ${current.messageKeys}`);
  console.log(`  内联双语      ${current.inlineBilingual}`);
  console.log(`  裸中文        ${current.hardcoded}`);
  process.exit(0);
}

if (!existsSync(baselinePath)) {
  console.error('缺少基线文件，请先执行：node scripts/check-i18n-coverage.mjs --update');
  process.exit(1);
}

const baseline = JSON.parse(readFileSync(baselinePath, 'utf8'));
const delta = current.hardcoded - baseline.hardcoded;

console.log('界面文案本地化覆盖');
console.log(`  词表 key           ${current.messageKeys}（基线 ${baseline.messageKeys}）`);
console.log(`  内联双语 text()    ${current.inlineBilingual}（基线 ${baseline.inlineBilingual}）`);
console.log(`  裸中文（缺口）      ${current.hardcoded}（基线 ${baseline.hardcoded}）`);

const regressions = [];
const baselineByFile = new Map(baseline.files.map((item) => [item.file, item.hardcoded]));
for (const item of files) {
  const allowed = baselineByFile.get(item.file) ?? 0;
  if (item.hardcoded > allowed) regressions.push(`${item.file}: ${allowed} → ${item.hardcoded}`);
}
for (const item of baseline.files) {
  // 只有文件真的不在了才提示同步基线；迁移到 0 是目标状态，不是回归。
  if (!scannedFiles.has(item.file) && item.hardcoded > 0) {
    regressions.push(`${item.file}: ${item.hardcoded} → 文件不存在（已删除或改名，请同步基线）`);
  }
}

if (regressions.length > 0) {
  console.error('\n以下文件新增了裸中文（英文模式下会显示中文）：');
  for (const line of regressions) console.error(`  - ${line}`);
  console.error('\n改用 t(\'namespace.key\') 并在 src/i18n/messages.ts 补 zh/en；');
  console.error('若确属临时情况，迁移完成后再执行 --update 收紧基线。');
  process.exit(1);
}

if (delta < 0) {
  console.log(`\n缺口较基线减少 ${-delta} 处。请执行 --update 收紧基线，避免数字回弹。`);
} else {
  console.log('\n未新增裸中文。');
}
