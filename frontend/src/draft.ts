import type { WorkflowPayload } from './types';

export const DRAFT_STORAGE_KEY = 'visionstudio.draft.workflow';

/**
 * Locally persisted unsaved draft of the designer workflow (hot-exit style recovery).
 * Written (debounced) while the editor is dirty, cleared on save / discard / recovery.
 */
export type WorkflowDraft = {
  version: 1;
  savedAt: string;
  workflowId: string;
  workflowName: string;
  demoKey: string;
  payload: WorkflowPayload;
};

export function readDraft(): WorkflowDraft | undefined {
  try {
    const raw = window.localStorage.getItem(DRAFT_STORAGE_KEY);
    if (!raw) return undefined;
    const parsed = JSON.parse(raw) as Partial<WorkflowDraft>;
    if (parsed?.version !== 1 || !parsed.payload || !Array.isArray(parsed.payload.nodes) || !Array.isArray(parsed.payload.edges)) {
      window.localStorage.removeItem(DRAFT_STORAGE_KEY);
      return undefined;
    }
    return {
      version: 1,
      savedAt: typeof parsed.savedAt === 'string' ? parsed.savedAt : new Date().toISOString(),
      workflowId: String(parsed.workflowId ?? ''),
      workflowName: String(parsed.workflowName ?? ''),
      demoKey: String(parsed.demoKey ?? ''),
      payload: parsed.payload
    };
  } catch {
    return undefined;
  }
}

export function writeDraft(draft: WorkflowDraft): void {
  try { window.localStorage.setItem(DRAFT_STORAGE_KEY, JSON.stringify(draft)); } catch { /* 忽略存储失败 */ }
}

export function clearDraft(): void {
  try { window.localStorage.removeItem(DRAFT_STORAGE_KEY); } catch { /* 忽略存储失败 */ }
}
