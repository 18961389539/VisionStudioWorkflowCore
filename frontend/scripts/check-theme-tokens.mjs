#!/usr/bin/env node
/**
 * 主题令牌的量化守卫（2026-11-13）。
 *
 * 此前 styles.css 有 62 行颜色绕过 token：浅色主题下 #e3a04a（≈2.2:1）与 #71a8ee（≈2.45:1）
 * 这类值直接落在白底上，远低于 WCAG AA。这个脚本守两件事：
 *   1. 颜色只能出现在 :root / [data-vs-theme='light'] 两个令牌块里（组件规则只许 var()）；
 *   2. 语义 token 在深/浅两套主题下的对比度：文字 ≥ 4.5:1，图形/边框 ≥ 3:1。
 *
 * 为什么不是 vitest 用例：Vitest 在 test.css=false 下把 `styles.css?raw` 处理成空串，
 * 读不到真实源码；这里用 Node fs 直读，和 check-bundle-budget.py 一样作为门禁步骤运行。
 */
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const css = readFileSync(path.join(root, 'src/styles.css'), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');

function blockBody(selector) {
  const start = css.indexOf(selector);
  if (start < 0) throw new Error(`Theme block not found: ${selector}`);
  const open = css.indexOf('{', start);
  const close = css.indexOf('}', open);
  return css.slice(open + 1, close);
}

function parseTokens(selector) {
  const tokens = new Map();
  for (const match of blockBody(selector).matchAll(/--([\w-]+)\s*:\s*([^;]+);/g)) {
    tokens.set(`--${match[1]}`, match[2].trim());
  }
  return tokens;
}

const dark = parseTokens(':root');
const light = parseTokens("[data-vs-theme='light']");

function channels(hex) {
  const value = hex.replace('#', '');
  const normalized = value.length === 3 ? value.split('').map((x) => x + x).join('') : value;
  if (!/^[0-9a-fA-F]{6}$/.test(normalized)) throw new Error(`Not a hex color: ${hex}`);
  return [0, 2, 4].map((index) => parseInt(normalized.slice(index, index + 2), 16) / 255);
}

function luminance(hex) {
  const [r, g, b] = channels(hex).map((value) =>
    value <= 0.04045 ? value / 12.92 : Math.pow((value + 0.055) / 1.055, 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(foreground, background) {
  const a = luminance(foreground);
  const b = luminance(background);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

function token(tokens, name, theme) {
  const value = tokens.get(name);
  if (!value) throw new Error(`${theme} theme does not define ${name}`);
  if (!value.startsWith('#')) throw new Error(`${name} in ${theme} must be a solid hex color (got ${value})`);
  return value;
}

const TEXT_TOKENS = [
  '--vs-text-1', '--vs-text-2', '--vs-text-3', '--vs-text-4', '--vs-text-5', '--vs-text-6',
  '--vs-text-brand', '--vs-text-danger', '--vs-status-success', '--vs-status-paused', '--vs-warn-text'
];

// 图形/边框按 3:1（WCAG 1.4.11）。--vs-skipped 是"已跳过"的淡出态，属于 inactive component，豁免。
const UI_TOKENS = [
  '--vs-accent', '--vs-accent-strong', '--vs-info', '--vs-edge', '--vs-warn', '--vs-danger',
  '--vs-success', '--vs-neutral', '--vs-pending', '--vs-module-border', '--vs-control',
  '--vs-node-ok', '--vs-node-error', '--vs-node-running', '--vs-run-ok', '--vs-run-error',
  '--vs-run-bar', '--vs-selected-outline'
];

const themes = [['dark', dark], ['light', light]];
const failures = [];
const hints = [];

function requireContrast(theme, tokens, foreground, background, minimum) {
  const fg = token(tokens, foreground, theme);
  const bg = token(tokens, background, theme);
  const ratio = contrast(fg, bg);
  const line = `${theme}: ${foreground} (${fg}) on ${background} (${bg}) = ${ratio.toFixed(2)}:1`;
  if (ratio < minimum) failures.push(`${line}, need ${minimum}:1`);
  else hints.push(line);
}

for (const [theme, tokens] of themes) {
  for (const name of TEXT_TOKENS) requireContrast(theme, tokens, name, '--vs-bg-app', 4.5);
  for (const name of UI_TOKENS) requireContrast(theme, tokens, name, '--vs-bg-center', 3);
  for (const background of ['--vs-node-ok', '--vs-node-error']) {
    requireContrast(theme, tokens, '--vs-text-on-color', background, 4.5);
  }
}

for (const name of [...TEXT_TOKENS, ...UI_TOKENS, '--vs-text-on-color', '--vs-bg-app', '--vs-bg-center', '--vs-bg-card']) {
  if (!light.has(name)) failures.push(`light theme is missing ${name} (would silently fall back to the dark value)`);
}

const rootBody = blockBody(':root');
const lightBody = blockBody("[data-vs-theme='light']");
const withoutTokens = css.replace(rootBody, '').replace(lightBody, '');
const hardcoded = [...withoutTokens.matchAll(/#[0-9a-fA-F]{3,8}\b|rgba?\(/g)].map((match) => match[0]);
if (hardcoded.length > 0) {
  failures.push(`hardcoded colors outside theme tokens: ${[...new Set(hardcoded)].join(', ')}`);
}

console.log('== Theme tokens ==');
console.log(`checked ${TEXT_TOKENS.length} text tokens + ${UI_TOKENS.length} UI tokens in dark/light; ${hints.length} contrast pairs`);
if (failures.length > 0) {
  console.error('');
  for (const line of failures) console.error(`THEME TOKEN VIOLATED: ${line}`);
  console.error('');
  console.error('Move the color into :root / [data-vs-theme=\'light\'] and make both themes readable.');
  process.exit(1);
}
console.log('Theme tokens PASSED');
