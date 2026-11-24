import { useEffect, useRef, useState } from 'react';
import type { ProductionRuntimeConfig } from './types';
import { defaultProductionConfig, differingProductionConfigFields, productionConfigsEqual } from './productionConfig';

type ConfigField = keyof ProductionRuntimeConfig;
type Edit = {
  base: ProductionRuntimeConfig;
  value: ProductionRuntimeConfig;
  editedDuringSave?: boolean;
  keepBase?: ReadonlySet<ConfigField>;
};
type MergeChoice = 'draft' | 'remote';
type MergeChoices = Partial<Record<ConfigField, MergeChoice>>;
type MergeField = {
  field: ConfigField;
  draft: ProductionRuntimeConfig[ConfigField];
  latest: ProductionRuntimeConfig[ConfigField];
  source: MergeChoice | 'choice';
};
export type ProductionConfigMerge = { fields: MergeField[]; apply: (choices: MergeChoices) => boolean };

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
    const keepBase = new Set(state.edit?.keepBase);
    for (const field of Object.keys(patch) as ConfigField[]) {
      if (Object.is(value[field], base[field]) && (editedDuringSave || !Object.is(value[field], state.remote[field]))) {
        keepBase.add(field);
      } else {
        keepBase.delete(field);
      }
    }
    // 回到旧远端值也可能是保存开始后的新意图，不能在保存结果未处理前清除。
    replace(!editedDuringSave && productionConfigsEqual(state.remote, value) ? undefined : { base, value, editedDuringSave, keepBase });
  };
  const beginSave = () => {
    if (!current.current.open || !current.current.remote || pendingSave.current) return;
    const ticket = {};
    pendingSave.current = ticket;
    return () => { if (pendingSave.current === ticket) pendingSave.current = undefined; };
  };
  const acceptSaved = (submitted: ProductionRuntimeConfig, saved: ProductionRuntimeConfig) => {
    const draft = current.current.edit?.value ?? submitted;
    const changes = differingProductionConfigFields(submitted, draft);
    // 保存期间的编辑应用到响应正文，保留服务端确认的其它字段。
    const value = { ...saved, ...Object.fromEntries(changes.map((field) => [field, draft[field]])) };
    replace(productionConfigsEqual(value, saved) ? undefined : { base: saved, value });
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
  let merge: ProductionConfigMerge | undefined;
  if (conflict && edit && remote) {
    const fields = differingProductionConfigFields(edit.value, remote).map((field): MergeField => {
      const draft = edit.value[field];
      const latest = remote[field];
      const localChanged = edit.keepBase?.has(field) || !Object.is(draft, edit.base[field]);
      const remoteChanged = !Object.is(latest, edit.base[field]);
      return { field, draft, latest, source: localChanged ? (remoteChanged ? 'choice' : 'draft') : 'remote' };
    });
    merge = {
      fields,
      apply: (choices) => {
        const state = current.current;
        if (!state.open || pendingSave.current || state.edit !== edit || !state.remote || !productionConfigsEqual(state.remote, remote)
          || fields.some(({ field, source }) => source === 'choice' && !choices[field])) return false;
        const localFields = fields.filter(({ field, source }) => source === 'draft' || (source === 'choice' && choices[field] === 'draft'));
        const value = { ...state.remote, ...Object.fromEntries(localFields.map(({ field, draft }) => [field, draft])) };
        replace(productionConfigsEqual(value, state.remote) ? undefined : { base: state.remote, value });
        return true;
      },
    };
  }
  return { config, dirty, conflict, merge, changeConfig, beginSave, acceptSaved, readCurrent, discard: () => replace(undefined) };
}
