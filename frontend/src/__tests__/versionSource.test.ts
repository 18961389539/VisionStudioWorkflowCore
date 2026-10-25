/// <reference types="vite/client" />
import { describe, expect, it } from 'vitest';
import { APP_VERSION } from '../version';

/**
 * 产品版本号单一来源的棘轮（2026-10-25）：此前 WorkflowModulePanel 标题写死 V0.52、
 * PluginPackagePanel 描述写死 V0.59/V0.58，而 package.json 已经是 0.63——客户报版本、售后核对时对不上。
 * 展示用版本必须取 APP_VERSION；确需展示外部组件版本（如某型号固件 V2.4）时，
 * 在本文件 EXTERNAL_VERSION_ALLOWLIST 里登记，并在提交信息里说明原因。
 */
const HARDCODED_VERSION_BASELINE = 0;
const EXTERNAL_VERSION_ALLOWLIST: string[] = [];
/** 明确表述为"历史版本"的行不算违规：V0.60 之前的追溯记录仍可读，这类说明本身需要版本号才有意义。 */
const HISTORY_CONTEXT = /(之前|以前|pre-|Pre-|历史)/;

const sources = import.meta.glob('/src/**/*.{ts,tsx}', {
  query: '?raw',
  import: 'default',
  eager: true
}) as Record<string, string>;

describe('product version single source', () => {
  it('APP_VERSION 来自 package.json 而不是字面量', () => {
    expect(APP_VERSION).toMatch(/^\d+\.\d+\.\d+/);
  });

  it(`does not hardcode display versions (baseline ${HARDCODED_VERSION_BASELINE})`, () => {
    const pattern = /V\d+\.\d+/g;
    const offenders = Object.entries(sources)
      .filter(([path]) => !path.includes('/__tests__/') && !path.endsWith('/version.ts'))
      .filter(([path]) => !EXTERNAL_VERSION_ALLOWLIST.includes(path))
      .map(([path, content]) => {
        const hits: string[] = [];
        for (const line of content.split('\n')) {
          if (HISTORY_CONTEXT.test(line)) continue;
          hits.push(...(line.match(pattern) ?? []));
        }
        return { path, hits };
      })
      .filter((entry) => entry.hits.length > 0);

    const total = offenders.reduce((sum, entry) => sum + entry.hits.length, 0);
    const detail = offenders
      .map((entry) => `${entry.path}: ${entry.hits.join(', ')}`)
      .join('\n');

    expect(total, `展示版本号必须取 APP_VERSION（当前 ${APP_VERSION}），不要写死：\n${detail}`)
      .toBeLessThanOrEqual(HARDCODED_VERSION_BASELINE);
  });
});
