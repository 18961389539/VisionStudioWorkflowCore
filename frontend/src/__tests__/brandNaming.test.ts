import { describe, expect, it } from 'vitest';

/**
 * 品牌名写法守卫（2026-11-23）。
 *
 * 运行时页标题曾写死 `VISIONSTUDIO · 生产运行`（缺一个 I），而顶栏是 `VisionStudio`——
 * 同一屏上两种写法。这类错字既不会编译失败，也不会被类型检查发现，只能靠文本断言拦。
 *
 * 约定：
 *  - 品牌名固定为 `VisionStudio`，两个词之间不加空格；
 *  - 技术标识例外：C# 命名空间 `VisionStudio.*`、环境变量 `VISIONSTUDIO_*`（变量名全大写是命名约定）。
 */
const sources = import.meta.glob('/src/**/*.{ts,tsx}', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

const scanned = Object.entries(sources)
  .filter(([path]) => !path.includes('/src/generated/') && !path.includes('/__tests__/'));

describe('品牌名写法', () => {
  it('不出现缺 I 的 VISONSTUDIO / VisonStudio', () => {
    const offenders = scanned
      .filter(([, content]) => /VISONSTUDIO|VisonStudio|Vison Studio/i.test(content))
      .map(([path]) => path);
    expect(offenders, `品牌名缺 I，应写作 VisionStudio：${offenders.join(', ')}`).toEqual([]);
  });

  it('不写成带空格的 Vision Studio', () => {
    const offenders = scanned
      .filter(([, content]) => /Vision Studio\b/.test(content))
      .map(([path]) => path);
    expect(offenders, `品牌名不应带空格：${offenders.join(', ')}`).toEqual([]);
  });

  it('界面文案不出现全大写品牌名（环境变量 VISIONSTUDIO_* 除外）', () => {
    const offenders = scanned
      .filter(([, content]) => /VISIONSTUDIO(?!_)/.test(content))
      .map(([path]) => path);
    expect(offenders, `界面文案应写作 VisionStudio，全大写只用于环境变量：${offenders.join(', ')}`).toEqual([]);
  });
});
