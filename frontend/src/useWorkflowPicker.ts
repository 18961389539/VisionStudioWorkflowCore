/**
 * “打开已有流程”选择器（巨型组件拆分，2026-11-13）：列表加载、选中与加载应用。
 * 逐行搬运自 App 的 Editor 组件；请求超时、错误文案与文档结构校验保持不变。
 */
import { useState } from 'react';
import { message } from 'antd';
import { apiFetch } from './apiClient';
import { responseErrorMessage } from './apiErrors';
import { workflows } from './endpoints';
import { isWorkflowDocument } from './workflowDocument';
import { useWorkflowImport } from './useWorkflowImport';
import type { useWorkflowChanges } from './useWorkflowChanges';
import { WORKFLOW_IO_TIMEOUT_MS, type WorkflowDocumentSummary } from './appEditorShared';

type MessageApi = ReturnType<typeof message.useMessage>[0];

export function useWorkflowPicker(options: {
  designerWorkflowId: string;
  messageApi: MessageApi;
  prepare: ReturnType<typeof useWorkflowChanges>['prepare'];
}) {
  const { designerWorkflowId, messageApi, prepare } = options;
  const [workflowPickerOpen, setWorkflowPickerOpen] = useState(false);
  const [savedWorkflows, setSavedWorkflows] = useState<WorkflowDocumentSummary[]>([]);
  const [workflowToLoad, setWorkflowToLoad] = useState<string>();
  const [workflowListLoading, setWorkflowListLoading] = useState(false);
  const [workflowListError, setWorkflowListError] = useState<string>();

  const workflowImport = useWorkflowImport({ active: workflowPickerOpen, contextKey: workflowToLoad,
    prepare, error: (text) => { void messageApi.error(text); } });
  const load = (requestedId: string) => workflowImport.load({
    label: '加载已保存的工作流', url: workflows.byId(requestedId),
    select: (data) => {
      if (!isWorkflowDocument(data) || data.id !== requestedId) throw new Error('响应工作流与选中的文档不匹配');
      return data;
    },
    options: (response) => ({ markClean: true, revision: response.headers.get('X-Workflow-Revision') ?? '' }),
    onApplied: () => { setWorkflowPickerOpen(false); void messageApi.success('工作流已加载'); },
  });

  const openWorkflowPicker = async () => {
    setWorkflowPickerOpen(true);
    setWorkflowListLoading(true);
    setWorkflowListError(undefined);
    try {
      const response = await apiFetch(workflows.list, { signal: AbortSignal.timeout(WORKFLOW_IO_TIMEOUT_MS) });
      if (!response.ok) throw new Error(await responseErrorMessage(response, `HTTP ${response.status}`));
      const items = await response.json() as WorkflowDocumentSummary[];
      setSavedWorkflows(items);
      setWorkflowToLoad((current) => items.some((item) => item.id === current)
        ? current
        : items.some((item) => item.id === designerWorkflowId) ? designerWorkflowId : items[0]?.id);
    } catch (error) {
      setWorkflowListError(error instanceof Error ? error.message : '已保存工作流列表加载失败');
      setSavedWorkflows([]);
      setWorkflowToLoad(undefined);
    } finally { setWorkflowListLoading(false); }
  };

  const loadSelectedSavedWorkflow = () => { if (workflowToLoad) void load(workflowToLoad); };

  return {
    workflowPickerOpen, setWorkflowPickerOpen, savedWorkflows, workflowToLoad, setWorkflowToLoad,
    workflowListLoading, workflowListError, workflowImport, openWorkflowPicker, loadSelectedSavedWorkflow
  };
}
