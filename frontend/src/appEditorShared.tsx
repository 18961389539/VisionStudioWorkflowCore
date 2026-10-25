/**
 * App 编辑器（App.tsx `Editor`）的模块级共享件：
 * 类型别名、localStorage 偏好、布局常量与图数据纯函数。不依赖组件树。
 * 从 App.tsx 拆出（巨型组件拆分，2026-10-23）；语义与归属注释保留原样。
 */
import { Suspense, type ReactNode } from 'react';
import { Skeleton } from 'antd';
import type { Node } from '@xyflow/react';
import ErrorBoundary from './components/ErrorBoundary';
import VisionNode from './components/VisionNode';
import { catalogNodeData, fallbackCatalog, makeDefaultParameters } from './catalog';
import { linearDemo, type DemoKey } from './demos';
import { hydrateNodeParameters, type NodeParameters } from './nodeConfigurationState';
import { withSwitchPorts } from './switchPorts';
import { type PortRecommendation } from './toolboxModel';
import type { NodeCatalogItem, PortDescriptor, WorkflowModuleParameter } from './types';

// R12：条件挂载的按需面板包装——open=false 时完全不渲染（不触发动态加载）；
// open=true 时由 Suspense 兜住加载窗口（面板自身是 Drawer，加载完立即展开）。
// 每个面板各自持有错误边界：单个面板的渲染异常不能打掉画布、未保存修改和其它面板。
// 2026-10-25：fallback 从 null 改为可见占位——首开分包时此前是整屏无反馈的白屏；
// 占位固定右上角且不接收指针事件，避免加载窗口挡住画布操作。
function PanelLoading({ label }: { label: string }) {
  return (
    <div className="panel-loading" role="status" aria-live="polite">
      <Skeleton active title={{ width: '55%' }} paragraph={{ rows: 3, width: ['92%', '84%', '68%'] }} />
      <span className="panel-loading-label">正在加载 {label}…</span>
    </div>
  );
}

export function LazyPanel({ open, label, onClose, children }: { open: boolean; label: string; onClose: () => void; children: ReactNode }) {
  return open
    ? (
      <ErrorBoundary variant="panel" scope={label} onDismiss={onClose}>
        <Suspense fallback={<PanelLoading label={label} />}>{children}</Suspense>
      </ErrorBoundary>
    )
    : null;
}

export const nodeTypes = { vision: VisionNode };
export type NodeAttachment = PortRecommendation & { nodeId: string; portName: string; newPort: string };
export type ConnectionPicker = PortRecommendation & { nodeId: string; portName: string; x: number; y: number; position: { x: number; y: number } };
export type WorkflowDocumentSummary = { id: string; name: string; nodeCount: number; edgeCount: number; updatedAt: string };

// R06（2026-10-12 复审）：编辑器保存/加载是人工关键操作——请求必须有整体超时；失败必须给出
// 可定位原因（状态码/业务码/关联号），且绝不在无法确认结果时改变本地 dirty/草稿状态。
export const WORKFLOW_IO_TIMEOUT_MS = 15_000;

// 模板入口 P0：顶栏下拉替换为“从模板新建”弹窗——分组 + 用途 + 预览，替换流程前必须显式确认
export const DEMO_LABELS: Record<DemoKey, string> = {
  contourThickness: '真实轮廓壁厚与局部变薄检测',
  caliperWidth: '双边缘卡尺测宽与公差判定',
  circularTolerance: '圆环壁厚与同心偏差',
  smallFeatures: '细小亮暗特征检测',
  regionShapes: '区域形状与凸包差',
  edgeTolerance: '直线形状与轮廓偏差',
  colors: '色彩检测与图像增强',
  textValidation: '文本与批号校验',
  alignment: '图像对齐与逐区域测量',
  defects: '缺陷检测与良品比对',
  regions: '区域处理与限定域',
  forEach: '逐目标顺序执行',
  inspection: '多目标检测与判定', precision: '形状模板匹配与精密测量',
  camera: '相机示例', cameraTrigger: '触发式相机', frameSet: '同步帧组',
  measurement: '测量示例', calibration: '标定示例',
  robotEyeToHand: '眼在手外示例', robotEyeInHand: '眼在手上示例', robotRuntime: '机器人运行时',
  robotPlc: '机器人 PLC 握手', robotTcp: '机器人 TCP 模拟器', devicePlc: '设备 / PLC 运行时',
  linear: '线性流程示例', roi: '感兴趣区域示例', if: '条件分支示例', parallel: '并行流程示例', nested: '嵌套流程示例'
};
export const DEMO_GROUPS: { group: string; keys: DemoKey[] }[] = [
  { group: '相机采集', keys: ['camera', 'cameraTrigger', 'frameSet'] },
  { group: '测量与标定', keys: ['contourThickness', 'caliperWidth', 'smallFeatures', 'regionShapes', 'circularTolerance', 'edgeTolerance', 'colors', 'textValidation', 'alignment', 'defects', 'regions', 'precision', 'inspection', 'measurement', 'calibration'] },
  { group: '机器人', keys: ['robotEyeToHand', 'robotEyeInHand', 'robotRuntime', 'robotPlc', 'robotTcp'] },
  { group: '设备 / PLC', keys: ['devicePlc'] },
  { group: '流程结构', keys: ['linear', 'roi', 'if', 'parallel', 'nested', 'forEach'] }
];

// 节点紧凑模式：全局显示偏好（压缩标题 / 端口间距 / 页脚），持久化在 localStorage
const NODE_COMPACT_KEY = 'visionstudio.node-compact';
export const readNodeCompact = () => {
  try { return window.localStorage.getItem(NODE_COMPACT_KEY) === '1'; } catch { return false; }
};
export const persistNodeCompact = (value: boolean) => {
  try { window.localStorage.setItem(NODE_COMPACT_KEY, value ? '1' : '0'); } catch { /* 忽略存储失败 */ }
};

// 工具栏已移出画布，自动适配在四周使用相同内缩。
// 类型对齐 @xyflow/react 的 PaddingWithUnit（number | `${number}px` | `${number}%`），避免任意 string 撞类型。
type FitPaddingUnit = number | `${number}px` | `${number}%`;
export const CANVAS_FIT_PADDING: { top: FitPaddingUnit; right: FitPaddingUnit; bottom: FitPaddingUnit; left: FitPaddingUnit } =
  { top: 0.16, right: 0.16, bottom: 0.16, left: 0.16 };

// 结果区折叠态保留的标签条高度（P1）：折叠不再等于“入口消失”，标签条即展开入口。
// 与 .bottom-panel.collapsed .bottom-tabs-bar 的 padding + Segmented 高度对齐（4+24+4）。
export const COLLAPSED_RESULT_BAR_HEIGHT = 34;

export function hydrateNodesFromCatalog(nodes: Node[], catalog: NodeCatalogItem[]): Node[] {
  const byType = new Map(catalog.map((item) => [item.type.toLowerCase(), item]));
  return nodes.map((node) => {
    const typeKey = String(node.data.typeKey ?? '');
    const catalogItem = byType.get(typeKey.toLowerCase());
    if (!catalogItem) return node;
    const item = withSwitchPorts(catalogItem, node.data.parameters as NodeParameters | undefined);

    const previousCatalogLabel = typeof node.data.catalogDisplayName === 'string' ? node.data.catalogDisplayName : undefined;
    const currentLabel = typeof node.data.label === 'string' ? node.data.label : typeKey;
    const label = currentLabel === typeKey || currentLabel === previousCatalogLabel ? item.displayName : currentLabel;
    return {
      ...node,
      data: {
        ...node.data,
        label,
        catalogDisplayName: item.displayName,
        parameters: hydrateNodeParameters((node.data.parameters ?? {}) as NodeParameters, item, makeDefaultParameters(item)),
        ...catalogNodeData(item)
      }
    };
  });
}

const initialGraph = linearDemo();
export const initial = { ...initialGraph, nodes: hydrateNodesFromCatalog(initialGraph.nodes, fallbackCatalog) };

export function getPorts(node: Node | undefined, key: 'inputs' | 'outputs'): PortDescriptor[] {
  return ((node?.data?.[key] ?? []) as PortDescriptor[]);
}

export function moduleMetadata(parameters: Record<string, unknown>) {
  const inputs = Array.isArray(parameters.__moduleInputs) ? parameters.__moduleInputs as PortDescriptor[] : [];
  const outputs = Array.isArray(parameters.__moduleOutputs) ? parameters.__moduleOutputs as PortDescriptor[] : [];
  const moduleParameters = Array.isArray(parameters.__moduleParameters) ? parameters.__moduleParameters as WorkflowModuleParameter[] : [];
  return { inputs, outputs, moduleParameters };
}

export function moduleCatalogFromNode(node?: Node): NodeCatalogItem | undefined {
  if (!node || String(node.data.typeKey) !== 'module.call') return undefined;
  const parameters = (node.data.parameters ?? {}) as Record<string, unknown>;
  const meta = moduleMetadata(parameters);
  return {
    type: 'module.call',
    displayName: `可复用模块 · ${String(parameters.moduleId ?? node.data.label ?? node.id)} · V${String(parameters.moduleVersion ?? '?')}`,
    category: '模块',
    inputs: meta.inputs,
    outputs: meta.outputs,
    parameters: meta.moduleParameters.map((x) => ({
      name: x.name, label: x.label, type: x.type as any, defaultValue: x.defaultValue, min: x.min, max: x.max, step: x.step, options: x.options, unit: x.unit, group: x.group, description: x.description
    })),
    description: 'Immutable reusable Workflow Module call. The pinned module version is expanded before compile/execution.',
    pluginId: 'module',
    capabilities: { deterministic: true, supportsRunNode: false, supportsParallel: true, executionMode: 'Auto' }
  };
}

export function edgePresentation(kind: 'control' | 'data') {
  return kind === 'control'
    ? { animated: true, data: { kind }, style: { strokeDasharray: '7 5' } }
    : { animated: false, data: { kind } };
}

const RECENT_NODES_KEY = 'visionstudio.recent-node-types';
export const RECENT_NODES_LIMIT = 6;
// 双击添加时的占位尺寸与最小间距（流程坐标）；实际渲染尺寸在 measured 中回读
export const NEW_NODE_WIDTH = 220;
export const NEW_NODE_HEIGHT = 180;
export const SPOT_GAP = 24;

export function readRecentNodeTypes(): string[] {
  try {
    const raw = window.localStorage.getItem(RECENT_NODES_KEY);
    const parsed = raw ? JSON.parse(raw) : [];
    return Array.isArray(parsed)
      ? parsed.filter((value): value is string => typeof value === 'string').slice(0, RECENT_NODES_LIMIT)
      : [];
  } catch {
    return [];
  }
}

export function persistRecentNodeTypes(types: string[]): void {
  try { window.localStorage.setItem(RECENT_NODES_KEY, JSON.stringify(types)); } catch { /* 忽略存储失败 */ }
}
