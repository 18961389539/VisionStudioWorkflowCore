import { useEffect, useRef, useState } from 'react';
import type { ProductionRuntimeConfig } from './types';
import { defaultProductionConfig, productionConfigsEqual } from './productionConfig';

type Edit = { base: ProductionRuntimeConfig; value: ProductionRuntimeConfig; editedDuringSave?: boolean };

/** 实时配置是服务端事实，编辑副本保留其开始编辑时的基线。 */
export function useProductionConfigDraft(remote: ProductionRuntimeConfig | undefined, open = true) {
  const [edit, setEdit] = useState<Edit>();
  const pendingSave = useRef<object | undefined>(undefined);
  const current = useRef({ remote, edit, open });
  current.current = { remote, edit, open };
  const replace = (next: Edit | undefined) => { current.current.edit = next; setEdit(next); };
  useEffect(() => () => { pendingSave.current = undefined; }, [open]);
  useEffect(() => {
    const state = current.current;
    if (state.remote && state.edit && !state.edit.editedDuringSave && productionConfigsEqual(state.edit.value, state.remote)) replace(undefined);
  }, [remote, edit]);

  const changeConfig = (patch: Partial<ProductionRuntimeConfig>) => {
    const state = current.current;
    if (!state.remote) return;
    const base = state.edit && !productionConfigsEqual(state.edit.value, state.remote) ? state.edit.base : state.remote;
    const value = { ...(state.edit?.value ?? state.remote), ...patch };
    const editedDuringSave = Boolean(pendingSave.current || state.edit?.editedDuringSave);
    // 回到旧远端值也可能是保存开始后的新意图，不能在保存结果未处理前清除。
    replace(!editedDuringSave && productionConfigsEqual(state.remote, value) ? undefined : { base, value, editedDuringSave });
  };
  const beginSave = () => {
    if (!current.current.open || !current.current.remote || pendingSave.current) return;
    const ticket = {};
    pendingSave.current = ticket;
    return () => { if (pendingSave.current === ticket) pendingSave.current = undefined; };
  };
  const acceptSaved = (submitted: ProductionRuntimeConfig, saved: ProductionRuntimeConfig) => {
    const value = current.current.edit?.value ?? current.current.remote;
    // 只确认提交快照；保存等待期间新增的编辑仍然保留，并沿用新的保存基线。
    replace(!value || productionConfigsEqual(value, submitted) ? undefined : { base: saved, value });
  };
  const readCurrent = () => {
    const state = current.current;
    const dirty = Boolean(state.edit && !productionConfigsEqual(state.edit.value, state.remote ?? state.edit.base));
    return {
      config: state.edit?.value ?? state.remote,
      dirty,
      conflict: Boolean(dirty && state.edit && state.remote && !productionConfigsEqual(state.edit.base, state.remote)),
    };
  };
  const { config = defaultProductionConfig, dirty, conflict } = readCurrent();
  return { config, dirty, conflict, changeConfig, beginSave, acceptSaved, readCurrent, discard: () => replace(undefined) };
}
