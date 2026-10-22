import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { useProductionConfigDraft } from '../useProductionConfigDraft';
import { defaultProductionConfig as defaults } from '../productionConfig';
import type { ProductionRuntimeConfig } from '../types';

const baseline = { ...defaults, jobId: 'A' };
afterEach(cleanup);
const mount = (remote: ProductionRuntimeConfig | undefined = baseline) => renderHook(
  ({ config }) => useProductionConfigDraft(config), { initialProps: { config: remote } });

describe('生产配置草稿', () => {
  it('读取成功前不把展示默认值变成可保存的配置', () => {
    const { result } = renderHook(() => useProductionConfigDraft(undefined));
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    expect(result.current.dirty).toBe(false);
    expect(result.current.readCurrent().config).toBeUndefined();
  });

  it('未编辑时跟随服务端刷新，编辑后保留草稿', () => {
    const { result, rerender } = mount();
    rerender({ config: { ...baseline, cycleDelayMs: 30 } });
    expect(result.current.config.cycleDelayMs).toBe(30);
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    rerender({ config: { ...baseline, cycleDelayMs: 30 } });
    expect(result.current.config.cycleDelayMs).toBe(100);
    expect(result.current.dirty).toBe(true);
    expect(result.current.conflict).toBe(false);
  });

  it('远端变更保留草稿并报告冲突，放弃后载入最新配置', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ jobId: 'B' }));
    rerender({ config: { ...baseline, maxCycleMs: 20000 } });
    expect(result.current.config).toMatchObject({ jobId: 'B', maxCycleMs: 15000 });
    expect(result.current.conflict).toBe(true);
    act(() => result.current.discard());
    expect(result.current.config).toMatchObject({ jobId: 'A', maxCycleMs: 20000 });
    expect(result.current.dirty).toBe(false);
    expect(result.current.conflict).toBe(false);
  });

  it('同一事件内连续修改不同字段不会相互覆盖，旧回调也读取最新草稿', () => {
    const { result } = mount();
    const read = result.current.readCurrent;
    act(() => {
      result.current.changeConfig({ cycleDelayMs: 100 });
      result.current.changeConfig({ maxCycleMs: 22000 });
      expect(read()).toMatchObject({ config: { cycleDelayMs: 100, maxCycleMs: 22000 }, dirty: true });
    });
  });

  it('修改后改回基线恢复同步，不因字段顺序改变误报冲突', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    rerender({ config: Object.fromEntries(Object.entries(baseline).reverse()) as ProductionRuntimeConfig });
    expect(result.current.conflict).toBe(false);
    act(() => result.current.changeConfig({ cycleDelayMs: 25 }));
    expect(result.current.dirty).toBe(false);
  });

  it('保存期间新增编辑保留为未保存，下一次使用已确认的保存基线', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    const submitted = result.current.config;
    act(() => result.current.changeConfig({ maxCycleMs: 22000 }));
    act(() => result.current.acceptSaved(submitted, submitted));
    rerender({ config: submitted });
    expect(result.current.config).toMatchObject({ cycleDelayMs: 100, maxCycleMs: 22000 });
    expect(result.current.dirty).toBe(true);
    expect(result.current.conflict).toBe(false);
    const second = result.current.config;
    act(() => result.current.acceptSaved(second, second));
    rerender({ config: second });
    expect(result.current.dirty).toBe(false);
  });

  it('轮询先读到已提交配置时，保存等待期间改回原值不会丢失', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    const submitted = result.current.config;
    const finishSave = result.current.beginSave();
    rerender({ config: submitted });
    act(() => result.current.changeConfig({ cycleDelayMs: baseline.cycleDelayMs }));
    expect(result.current.config.cycleDelayMs).toBe(baseline.cycleDelayMs);
    act(() => result.current.acceptSaved(submitted, submitted));
    rerender({ config: { ...submitted } });
    expect(result.current.config.cycleDelayMs).toBe(baseline.cycleDelayMs);
    expect(result.current.readCurrent()).toMatchObject({ dirty: true, conflict: false });
    finishSave?.();
  });

  it('先改回原值、再轮询到提交配置时，仍保留保存期间的新编辑', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    const submitted = result.current.config;
    const finishSave = result.current.beginSave();
    act(() => result.current.changeConfig({ cycleDelayMs: baseline.cycleDelayMs }));
    rerender({ config: submitted });
    expect(result.current.config.cycleDelayMs).toBe(baseline.cycleDelayMs);
    act(() => result.current.acceptSaved(submitted, submitted));
    finishSave?.();
    expect(result.current.readCurrent()).toMatchObject({
      config: { cycleDelayMs: baseline.cycleDelayMs }, dirty: true, conflict: false,
    });
  });

  it('保存结果未确认时不丢弃期间的新意图，迟到的远端变化仍需核对', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    const submitted = result.current.config;
    const finishSave = result.current.beginSave();
    act(() => result.current.changeConfig({ cycleDelayMs: baseline.cycleDelayMs }));
    finishSave?.();
    rerender({ config: submitted });
    expect(result.current.config.cycleDelayMs).toBe(baseline.cycleDelayMs);
    expect(result.current.readCurrent()).toMatchObject({ dirty: true, conflict: true });
    act(() => result.current.discard());
    expect(result.current.config.cycleDelayMs).toBe(100);
  });

  it('关闭重开释放旧保存归属，旧结束回调不能释放新保存', () => {
    const { result, rerender } = renderHook(({ open }) => useProductionConfigDraft(baseline, open), { initialProps: { open: true } });
    const first = result.current.beginSave();
    expect(first).toBeTypeOf('function');
    expect(result.current.beginSave()).toBeUndefined();
    rerender({ open: false });
    expect(result.current.beginSave()).toBeUndefined();
    rerender({ open: true });
    const second = result.current.beginSave();
    expect(second).toBeTypeOf('function');
    first?.();
    expect(result.current.beginSave()).toBeUndefined();
    second?.();
    expect(result.current.beginSave()).toBeTypeOf('function');
  });

  it('服务端已经确认相同配置后恢复同步，后续轮询不复活旧草稿', () => {
    const { result, rerender } = mount();
    const read = result.current.readCurrent;
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    rerender({ config: { ...baseline, cycleDelayMs: 100 } });
    expect(result.current.dirty).toBe(false);
    expect(result.current.conflict).toBe(false);
    expect(read()).toMatchObject({ dirty: false, conflict: false });
    rerender({ config: { ...baseline, cycleDelayMs: 150 } });
    expect(result.current.config.cycleDelayMs).toBe(150);
    expect(result.current.dirty).toBe(false);
  });

  it('配置收敛后的下一次编辑以最新服务端快照为基线', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    const confirmed = { ...baseline, cycleDelayMs: 100 };
    rerender({ config: confirmed });
    act(() => result.current.changeConfig({ maxCycleMs: 22000 }));
    expect(result.current.config).toMatchObject({ cycleDelayMs: 100, maxCycleMs: 22000 });
    expect(result.current.dirty).toBe(true);
    expect(result.current.conflict).toBe(false);
  });

  it('远端改变后输入原基线值仍保留为待核对修改，不静默替换为远端值', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    rerender({ config: { ...baseline, cycleDelayMs: 45 } });
    act(() => result.current.changeConfig({ cycleDelayMs: baseline.cycleDelayMs }));
    expect(result.current.config.cycleDelayMs).toBe(baseline.cycleDelayMs);
    expect(result.current.dirty).toBe(true);
    expect(result.current.conflict).toBe(true);
    expect(result.current.readCurrent()).toMatchObject({ dirty: true, conflict: true });
    act(() => result.current.discard());
    expect(result.current.config.cycleDelayMs).toBe(45);
    expect(result.current.dirty).toBe(false);
  });

  it('手动改成最新远端值时清除冲突，并继续跟随服务端刷新', () => {
    const { result, rerender } = mount();
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    rerender({ config: { ...baseline, cycleDelayMs: 45 } });
    act(() => result.current.changeConfig({ cycleDelayMs: 45 }));
    expect(result.current.dirty).toBe(false);
    expect(result.current.conflict).toBe(false);
    expect(result.current.readCurrent()).toMatchObject({ dirty: false, conflict: false });
    rerender({ config: { ...baseline, cycleDelayMs: 50 } });
    expect(result.current.config.cycleDelayMs).toBe(50);
  });

  it('保留服务端新增字段，编辑普通参数不会重置恢复次数', () => {
    const remote = { ...baseline, maxRecoveryAttempts: 7 };
    const { result, rerender } = mount(remote);
    act(() => result.current.changeConfig({ cycleDelayMs: 100 }));
    expect(result.current.config).toHaveProperty('maxRecoveryAttempts', 7);
    const updated = { ...remote, maxRecoveryAttempts: 2 };
    rerender({ config: updated });
    expect(result.current.conflict).toBe(true);
  });
});
