import { useEffect, useRef, useState, type MutableRefObject, type RefObject } from 'react';
import { responseErrorMessage } from './apiErrors';
import { decideSaveBaseline, pickExpectedRevision, shouldApplySaveResult, type DocumentBaseline, type RemoteBaselineProbe } from './documentSync';
import { apiFetch } from './apiClient';
import { workflows } from './endpoints';

const WORKFLOW_IO_TIMEOUT_MS = 15_000;

type Params = {
  workflowId: string;
  snapshot: string;
  /** 只在整块替换文档时递增；普通编辑（含重命名）属于同一文档。 */
  generationRef: RefObject<number>;
  baselineRef: MutableRefObject<DocumentBaseline>;
  confirmOverwrite: (id: string) => Promise<boolean>;
  onSaved: (snapshot: string) => void;
  messages: { info: (text: string) => unknown; warning: (text: string) => unknown; error: (text: string) => unknown; success: (text: string) => unknown };
};

/** 保存提交快照；只有当前内容已被这次保存覆盖时，才允许“保存并继续”。 */
export function useWorkflowSave(params: Params) {
  const latest = useRef(params);
  latest.current = params;
  const inFlight = useRef(false);
  const mounted = useRef(false);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  const save = async (): Promise<boolean> => {
    if (!mounted.current) return false;
    if (inFlight.current) {
      latest.current.messages.info('保存进行中，请稍候…');
      return false;
    }
    inFlight.current = true;
    setSaving(true);
    const { workflowId: id, snapshot, generationRef, baselineRef } = latest.current;
    const captured = { id, epoch: generationRef.current };
    const isCurrentDocument = () => mounted.current && shouldApplySaveResult(captured, {
      id: latest.current.workflowId, epoch: generationRef.current,
    });
    const stopIfSwitched = () => {
      if (isCurrentDocument()) return false;
      if (mounted.current) latest.current.messages.info('保存已停止：准备保存期间文档已切换。');
      return true;
    };
    let writeStarted = false;
    try {
      let baseline = pickExpectedRevision(baselineRef.current, id);
      if (baseline === undefined) {
        let probe: RemoteBaselineProbe;
        try {
          const response = await apiFetch(workflows.byId(id), { signal: AbortSignal.timeout(WORKFLOW_IO_TIMEOUT_MS) });
          probe = response.status === 404 ? { kind: 'missing' }
            : response.ok ? { kind: 'exists', revision: response.headers.get('X-Workflow-Revision') ?? '' }
              : { kind: 'failed' };
        } catch { probe = { kind: 'failed' }; }
        if (stopIfSwitched()) return false;
        const decision = decideSaveBaseline(undefined, probe, id);
        if (decision.action === 'abort') {
          latest.current.messages.error(`保存已取消：${decision.reason}`);
          return false;
        }
        if (decision.action === 'confirm-overwrite') {
          const confirmed = await latest.current.confirmOverwrite(id);
          if (stopIfSwitched()) return false;
          if (!confirmed) {
            latest.current.messages.info('已取消保存（未覆盖后端已有版本）。');
            return false;
          }
          baseline = decision.revision;
        } else baseline = decision.baseline;
      }

      writeStarted = true;
      const response = await apiFetch(workflows.withExpectedRevision(id, baseline), {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: snapshot,
        signal: AbortSignal.timeout(WORKFLOW_IO_TIMEOUT_MS),
      });
      if (!mounted.current) return false;
      if (response.ok) {
        if (!isCurrentDocument()) {
          latest.current.messages.info('先前文档的保存已完成，当前文档的保存状态保持不变。');
          return false;
        }
        const revision = response.headers.get('X-Workflow-Revision');
        baselineRef.current = revision?.trim() ? { id, revision } : undefined;
        latest.current.onSaved(snapshot);
        const unchanged = latest.current.snapshot === snapshot;
        const revisionDeferred = response.headers.get('X-Workflow-Revision-Deferred') === 'true';
        const deferredMessage = '版本基线记录延迟落定（读取时将自动对齐）；如出现并发冲突，请重新加载核对。';
        const baselineWarning = !baselineRef.current
          ? '响应缺少有效修订号；下次保存将重新探测并确认覆盖。请检查后端或代理配置。'
          : revisionDeferred ? deferredMessage : '';
        if (!unchanged) {
          latest.current.messages.warning(`已保存提交时的版本；保存期间新增的修改仍未保存，请再次保存。${baselineWarning}`);
        } else if (baselineWarning) latest.current.messages.warning(`工作流已保存，但${baselineWarning}`);
        else latest.current.messages.success('工作流已保存为后端 JSON');
        return unchanged;
      }
      const description = await responseErrorMessage(response, `HTTP ${response.status}`);
      if (!isCurrentDocument()) return false;
      latest.current.messages.error(response.status === 409
        ? `保存被拒绝（${description}）：可能已有其他会话保存了该工作流。请先“打开已有流程”加载最新版本核对差异。`
        : `保存失败（${description}）。修改仍保留在本地。`);
      return false;
    } catch (error) {
      if (mounted.current) {
        const reason = error instanceof Error ? error.message : '网络不可达';
        latest.current.messages.error(writeStarted
          ? `保存未确认（${reason}）：修改仍保留在本地，后端可能已收到本次保存。重试若遇版本冲突，请重新加载最新版本核对。`
          : `保存失败（${reason}）。修改仍保留在本地。`);
      }
      return false;
    } finally {
      inFlight.current = false;
      if (mounted.current) setSaving(false);
    }
  };

  return { save, saving };
}
