/**
 * 编辑器文档动作（巨型组件拆分，2026-11-13）：应用示例、整篇恢复、插入/升级模块。
 * 逐行搬运自 App 的 Editor 组件；代次、修订基线与撤销时间线的处理保持不变。
 */
import { useCallback, type Dispatch, type SetStateAction } from 'react';
import type { Edge, FitViewOptions, Node } from '@xyflow/react';
import { message } from 'antd';
import { demos, type DemoKey } from './demos';
import { CANVAS_FIT_PADDING, DEMO_LABELS, edgePresentation, hydrateNodesFromCatalog, moduleMetadata } from './appEditorShared';
import { isWorkflowDocument } from './workflowDocument';
import { migrateNodeType } from './legacyNodes';
import { catalogNodeData } from './catalog';
import { withSwitchPorts } from './switchPorts';
import type { GraphHistory } from './history';
import type { DocumentBaseline } from './documentSync';
import type { NodeCatalogItem, WorkflowModuleVersion, WorkflowPayload } from './types';

type MessageApi = ReturnType<typeof message.useMessage>[0];

export type EditorWorkflowDocumentOptions = {
  catalog: NodeCatalogItem[];
  nodesRef: { current: Node[] };
  selectedRef: { current: string };
  selectedId: string;
  documentGenerationRef: { current: number };
  workflowRevisionRef: { current: DocumentBaseline };
  markCleanOnNextChange: { current: boolean };
  history: GraphHistory;
  setNodes: Dispatch<SetStateAction<Node[]>>;
  setEdges: Dispatch<SetStateAction<Edge[]>>;
  setSelectedId: Dispatch<SetStateAction<string>>;
  setHistoryVersion: Dispatch<SetStateAction<number>>;
  setDesignerWorkflowId: (value: string) => void;
  setDesignerWorkflowName: (value: string) => void;
  setDemoKey: (value: DemoKey) => void;
  setLastSavedAt: (value: Date | undefined) => void;
  setHighlightedEdgeId: (value: string | undefined) => void;
  pushHistory: (label: string, coalesceKey?: string) => void;
  resetInspection: () => void;
  clearResult: () => void;
  fitView: (options?: FitViewOptions) => Promise<boolean>;
  screenToFlowPosition: (position: { x: number; y: number }) => { x: number; y: number };
  messageApi: MessageApi;
};

export function useEditorWorkflowDocument(options: EditorWorkflowDocumentOptions) {
  const {
    catalog, nodesRef, selectedRef, selectedId, documentGenerationRef, workflowRevisionRef, markCleanOnNextChange, history,
    setNodes, setEdges, setSelectedId, setHistoryVersion, setDesignerWorkflowId, setDesignerWorkflowName,
    setDemoKey, setLastSavedAt, setHighlightedEdgeId, pushHistory, resetInspection, clearResult,
    fitView, screenToFlowPosition, messageApi
  } = options;
  const applyDemo = (key: DemoKey) => {
    // A03/A04：整块替换文档即新代次——在途旧响应作废；并发基线失效（下次保存显式重建）。
    documentGenerationRef.current += 1;
    workflowRevisionRef.current = undefined;
    const rawGraph = demos[key]();
    const graph = { ...rawGraph, nodes: hydrateNodesFromCatalog(rawGraph.nodes, catalog) };
    setDemoKey(key);
    setDesignerWorkflowId(`demo-${key}`);
    // 文档名不携带版本号（避免与品牌 V{APP_VERSION} 同屏互相矛盾），并复用分组里的中文用途名
    setDesignerWorkflowName(`VisionStudio · ${DEMO_LABELS[key] ?? key}`);
    setNodes(graph.nodes.map((node) => ({ ...node, data: { ...node.data, breakpoint: false } })));
    setEdges(graph.edges);
    setSelectedId(key === 'roi' ? 'threshold-1' : (graph.nodes[0]?.id ?? ''));
    history.reset();
    setHistoryVersion((version) => version + 1);
    markCleanOnNextChange.current = true;
    clearResult(); // 结果与图像（预览/逐节点目录）随 runId 代际一并失效，由 useRunImages 接管
    setLastSavedAt(undefined);
    resetInspection();
    setHighlightedEdgeId(undefined);
    setTimeout(() => fitView({ padding: CANVAS_FIT_PADDING, duration: 250 }), 0);
  };
  const restoreWorkflow = useCallback((workflow: WorkflowPayload, options?: { markClean?: boolean; preserveDocument?: boolean; editLabel?: string }) => {
    // A03/A04：整块替换文档即新代次——在途旧响应作废；并发基线失效。
    // 调用方如需绑定"本次实际加载"的修订基线，必须在 restore 之后重新设置（load 即如此）。
    if (!isWorkflowDocument(workflow)) throw new Error('工作流文档结构无效');
    const restoredNodes: Node[] = workflow.nodes.map((item) => {
      // 已合并的旧节点类型在这里改写为规范类型并补齐参数，使旧流程仍可编辑与运行。
      const migrated = migrateNodeType(item.type, item.parameters ?? undefined);
      const meta = catalog.find((x) => x.type === migrated.type);
      const parameters = migrated.parameters;
      const nodeMeta = meta ? withSwitchPorts(meta, parameters) : undefined;
      const module = migrated.type === 'module.call' ? moduleMetadata(parameters) : undefined;
      return {
        id: item.id,
        type: 'vision',
        position: item.position ?? { x: 100, y: 100 },
        data: {
          typeKey: migrated.type,
          label: item.name ?? nodeMeta?.displayName ?? (migrated.type === 'module.call' ? String(parameters.moduleId ?? 'Reusable Module') : migrated.type),
          parameters,
          ...(module ? { inputs: module.inputs, outputs: module.outputs }
            : nodeMeta ? catalogNodeData(nodeMeta) : { inputs: [], outputs: [] }),
          moduleParameters: module?.moduleParameters ?? [],
          moduleVersion: parameters.moduleVersion,
          status: 'idle',
          breakpoint: options?.preserveDocument
            ? Boolean(nodesRef.current.find((node) => node.id === item.id && node.data.typeKey === migrated.type)?.data.breakpoint) : false
        }
      };
    });
    const restoredEdges: Edge[] = workflow.edges.map((item) => ({
      id: item.id,
      source: item.sourceNodeId,
      sourceHandle: item.sourcePort,
      target: item.targetNodeId,
      targetHandle: item.targetPort,
      ...edgePresentation(item.kind === 'control' ? 'control' : 'data')
    }));
    // 先完成图数据构建；无效模块元数据不能造成身份已切换、画布未更新的半应用状态。
    if (options?.preserveDocument) pushHistory(options.editLabel ?? '应用流程编辑');
    if (!options?.preserveDocument) {
      documentGenerationRef.current += 1;
      workflowRevisionRef.current = undefined;
      setDesignerWorkflowId(workflow.id);
      setDesignerWorkflowName(workflow.name);
    }
    setNodes(restoredNodes);
    setEdges(restoredEdges);
    setSelectedId(options?.preserveDocument && restoredNodes.some((node) => node.id === selectedRef.current) ? selectedRef.current : restoredNodes[0]?.id ?? '');
    // 文档替换重建撤销时间线；调优/模块提取是同一文档的可撤销编辑。
    if (!options?.preserveDocument) history.reset();
    setHistoryVersion((version) => version + 1);
    markCleanOnNextChange.current = options?.markClean !== false;
    clearResult(); // 结果与图像（预览/逐节点目录）随 runId 代际一并失效，由 useRunImages 接管
    if (!options?.preserveDocument) setLastSavedAt(undefined); // 文档替换后旧保存时间不再代表当前文档
    resetInspection();
    setHighlightedEdgeId(undefined);
    if (!options?.preserveDocument) setTimeout(() => fitView({ padding: CANVAS_FIT_PADDING, duration: 250 }), 0);
  }, [catalog, fitView, history, pushHistory, resetInspection, setEdges, setNodes]);
  const insertModule = (version: WorkflowModuleVersion) => {
    const position = screenToFlowPosition({ x: window.innerWidth * 0.48, y: window.innerHeight * 0.38 });
    const parameters: Record<string, unknown> = {
      moduleId: version.moduleId, moduleVersion: version.version, moduleHash: version.moduleHash,
      __moduleInputs: version.inputs.map(({ name, dataType, required }) => ({ name, dataType, required })),
      __moduleOutputs: version.outputs.map(({ name, dataType, required }) => ({ name, dataType, required })),
      __moduleParameters: version.parameters
    };
    version.parameters.forEach((parameter) => { parameters[parameter.name] = parameter.defaultValue; });
    const newNode: Node = {
      id: `module-${version.moduleId.replace(/[^A-Za-z0-9_-]/g, '_')}-${crypto.randomUUID().slice(0, 8)}`, type: 'vision', position,
      data: { typeKey: 'module.call', label: version.moduleId, parameters, inputs: parameters.__moduleInputs, outputs: parameters.__moduleOutputs, moduleParameters: version.parameters, moduleVersion: version.version, status: 'idle', breakpoint: false }
    };
    pushHistory('插入模块');
    setNodes((current) => [...current, newNode]); setSelectedId(newNode.id); messageApi.success(`已插入模块 ${version.moduleId} V${version.version}`);
  };

  const upgradeSelectedModule = (version: WorkflowModuleVersion) => {
    if (!selectedId) return;
    pushHistory('升级模块版本');
    setNodes((current) => current.map((node) => {
      if (node.id !== selectedId || String(node.data.typeKey) !== 'module.call') return node;
      const old = (node.data.parameters ?? {}) as Record<string, unknown>;
      const parameters: Record<string, unknown> = {
        moduleId: version.moduleId, moduleVersion: version.version, moduleHash: version.moduleHash,
        __moduleInputs: version.inputs.map(({ name, dataType, required }) => ({ name, dataType, required })),
        __moduleOutputs: version.outputs.map(({ name, dataType, required }) => ({ name, dataType, required })),
        __moduleParameters: version.parameters
      };
      version.parameters.forEach((parameter) => { parameters[parameter.name] = old[parameter.name] ?? parameter.defaultValue; });
      return { ...node, data: { ...node.data, label: version.moduleId, parameters, inputs: parameters.__moduleInputs, outputs: parameters.__moduleOutputs, moduleParameters: version.parameters, moduleVersion: version.version } };
    }));
    messageApi.success(`已将选中的模块调用升级到 ${version.moduleId} V${version.version}`);
  };
  return { applyDemo, restoreWorkflow, insertModule, upgradeSelectedModule };
}
