/**
 * 编辑器顶栏 / 右键菜单 / 命令面板的菜单装配（巨型组件拆分，2026-11-13）。
 *
 * 纯函数：输入当前状态与动作集合，输出 antd MenuProps。菜单项文案、键位、禁用条件
 * 与拆分前逐条一致；不持有 React 状态。
 */
import { Checkbox } from 'antd';
import type { MenuProps } from 'antd';
import type { Node } from '@xyflow/react';
import type { PaletteCommand } from './components/CommandPalette';
import type { LayoutPreset } from './layout';
import type { NodeConfigurationTab } from './components/NodeConfigurationDialog';

/** 面板/弹窗开关：菜单项与命令面板共用。 */
export type PanelOpeners = {
  setCalibrationOpen: (value: boolean) => void;
  setTracesOpen: (value: boolean) => void;
  setJobsOpen: (value: boolean) => void;
  setValidationOpen: (value: boolean) => void;
  setTuningOpen: (value: boolean) => void;
  setModulesOpen: (value: boolean) => void;
  setCamerasOpen: (value: boolean) => void;
  setRobotsOpen: (value: boolean) => void;
  setDevicesOpen: (value: boolean) => void;
  setProductionOpen: (value: boolean) => void;
  setDiagnosticsOpen: (value: boolean) => void;
  setPluginsOpen: (value: boolean) => void;
  setModelPackagesOpen: (value: boolean) => void;
  setModelAssetsOpen: (value: boolean) => void;
  setFramesOpen: (value: boolean) => void;
  setHardwareProvenanceOpen: (value: boolean) => void;
  setShortcutsOpen: (value: boolean) => void;
  setAboutOpen: (value: boolean) => void;
};

export type EditorCommands = {
  run: () => void;
  validate: () => void;
  save: () => void;
  fitView: () => void;
  toggleFocusMode: () => void;
  toggleTheme: () => void;
  toggleLang: () => void;
};

export type PaletteContext = PanelOpeners & EditorCommands & { focusMode: boolean };

export function buildPaletteTargets(focusMode: boolean): PaletteCommand[] {
  return [
    { key: 'run', title: '运行流程', group: '命令', hint: 'Run', keywords: 'run execute' },
    { key: 'validate', title: '校验流程', group: '命令', hint: 'Validate', keywords: 'validate check' },
    { key: 'save', title: '保存工作流', group: '命令', hint: 'Ctrl+S', keywords: 'save' },
    { key: 'fit', title: '适应画布', group: '命令', hint: 'Fit view', keywords: 'fit zoom 缩放' },
    { key: 'focus', title: focusMode ? '退出专注画布' : '专注画布', group: '命令', keywords: 'focus fullscreen 专注' },
    { key: 'theme', title: '切换深浅主题', group: '命令', keywords: 'theme dark light 主题' },
    { key: 'lang', title: '切换界面语言 / Switch language', group: '命令', keywords: 'language lang 语言 english' },
    { key: 'panel:calibration', title: '标定', group: '面板', keywords: 'calibration' },
    { key: 'panel:traces', title: '运行追溯', group: '面板', keywords: 'trace' },
    { key: 'panel:jobs', title: '配方管理', group: '面板', keywords: 'job recipe' },
    { key: 'panel:validation', title: '数据集 / 批量验证', group: '面板', keywords: 'dataset validation' },
    { key: 'panel:tuning', title: '参数调优', group: '面板', keywords: 'tuning' },
    { key: 'panel:modules', title: '可复用模块', group: '面板', keywords: 'module' },
    { key: 'panel:cameras', title: '相机采集', group: '面板', keywords: 'camera' },
    { key: 'panel:robots', title: '机器人', group: '面板', keywords: 'robot' },
    { key: 'panel:devices', title: '设备运行时', group: '面板', keywords: 'device plc' },
    { key: 'panel:production', title: '生产运行', group: '面板', keywords: 'production' },
    { key: 'panel:diagnostics', title: '诊断中心', group: '面板', keywords: 'diagnostics' },
    { key: 'panel:plugins', title: '插件包管理', group: '面板', keywords: 'plugin' },
    { key: 'panel:models', title: '外部训练包', group: '面板', keywords: 'model package' },
    { key: 'panel:model-assets', title: '模型资产', group: '面板', keywords: 'model asset shape 资产' },
    { key: 'panel:frames', title: '坐标系树', group: '面板', keywords: 'frame 坐标' },
    { key: 'panel:provenance', title: '硬件溯源', group: '面板', keywords: 'provenance' }
  ];
}

export function runPaletteCommand(key: string, context: PaletteContext): void {
  switch (key) {
    case 'run': context.run(); return;
    case 'validate': context.validate(); return;
    case 'save': context.save(); return;
    case 'fit': context.fitView(); return;
    case 'focus': context.toggleFocusMode(); return;
    case 'theme': context.toggleTheme(); return;
    case 'lang': context.toggleLang(); return;
    case 'panel:calibration': context.setCalibrationOpen(true); return;
    case 'panel:traces': context.setTracesOpen(true); return;
    case 'panel:jobs': context.setJobsOpen(true); return;
    case 'panel:validation': context.setValidationOpen(true); return;
    case 'panel:tuning': context.setTuningOpen(true); return;
    case 'panel:modules': context.setModulesOpen(true); return;
    case 'panel:cameras': context.setCamerasOpen(true); return;
    case 'panel:robots': context.setRobotsOpen(true); return;
    case 'panel:devices': context.setDevicesOpen(true); return;
    case 'panel:production': context.setProductionOpen(true); return;
    case 'panel:diagnostics': context.setDiagnosticsOpen(true); return;
    case 'panel:plugins': context.setPluginsOpen(true); return;
    case 'panel:models': context.setModelPackagesOpen(true); return;
    case 'panel:model-assets': context.setModelAssetsOpen(true); return;
    case 'panel:frames': context.setFramesOpen(true); return;
    case 'panel:provenance': context.setHardwareProvenanceOpen(true); return;
    default: return;
  }
}

export type DebugMenuState = {
  debugSession: { status: string } | undefined;
  statusText: string;
  anyBusy: boolean;
  selectedId: string;
  selectedNode: Node | undefined;
  targetSuffix: string;
  allowSideEffects: boolean;
  hasSideEffectNodes: boolean;
  hasModuleCalls: boolean;
  breakpointCount: number;
  compileBusy: boolean;
};

export type DebugMenuHandlers = {
  toggleBreakpoint: () => void;
  continueDebugSession: () => void;
  runSelectedNodeInSession: () => void;
  endDebugSession: () => void;
  runNode: () => void;
  runFromHere: () => void;
  setAllowSideEffects: (value: boolean) => void;
  startDebugSession: () => void;
  inspectDsl: () => void;
};

export function buildDebugMenuItems(state: DebugMenuState, handlers: DebugMenuHandlers): NonNullable<MenuProps['items']> {
  const { debugSession, statusText, anyBusy, selectedId, selectedNode, targetSuffix, allowSideEffects,
    hasSideEffectNodes, hasModuleCalls, breakpointCount, compileBusy } = state;
  return [
    ...(debugSession ? [
      { key: 'debug-state', label: `当前会话：${statusText}`, disabled: true },
      { key: 'debug-continue', label: '继续调试', disabled: anyBusy || debugSession.status !== 'halted', onClick: () => handlers.continueDebugSession() },
      { key: 'debug-cached-node', label: `用缓存输入运行节点${targetSuffix}`, disabled: anyBusy || debugSession.status === 'faulted' || !selectedId, onClick: () => handlers.runSelectedNodeInSession() },
      { key: 'debug-end', label: '结束调试会话', danger: true, disabled: anyBusy, onClick: () => handlers.endDebugSession() },
      { type: 'divider' as const }
    ] : []),
    { key: 'breakpoint', label: `切换断点${targetSuffix}`, disabled: !selectedId, onClick: handlers.toggleBreakpoint },
    { key: 'run-node', label: `运行节点${targetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !selectedId || selectedNode?.data.typeKey === 'module.call', onClick: handlers.runNode },
    { key: 'run-from-here', label: `从此处运行${targetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !selectedId || selectedNode?.data.typeKey === 'module.call', onClick: handlers.runFromHere },
    { type: 'divider' },
    ...(hasSideEffectNodes ? [{ key: 'debug-side-effects', label: <Checkbox checked={allowSideEffects} disabled={anyBusy || Boolean(debugSession)} onChange={(event) => handlers.setAllowSideEffects(event.target.checked)}>允许真实设备写入</Checkbox> }] : []),
    { key: 'debug-breakpoints', label: `开始断点调试${breakpointCount ? `（${breakpointCount}）` : ''}`, disabled: anyBusy || hasModuleCalls || Boolean(debugSession) || breakpointCount === 0, onClick: () => handlers.startDebugSession() },
    { key: 'core-dsl', label: '查看核心 DSL', disabled: compileBusy, onClick: handlers.inspectDsl }
  ];
}

export type ContextMenuHandlers = {
  openNodeConfiguration: (nodeId: string, tab?: NodeConfigurationTab) => void;
  runNode: () => void;
  runFromHere: () => void;
  toggleBreakpoint: () => void;
  copySelection: () => boolean;
  deleteElements: (nodeIds: string[], edgeIds: string[]) => void;
};

export type ContextMenuState = {
  contextNode: Node | undefined;
  contextTargetSuffix: string;
  anyBusy: boolean;
  debugSession: { status: string } | undefined;
  nodeContextMenu: { nodeId: string } | undefined;
};

export function buildNodeContextMenuItems(state: ContextMenuState, handlers: ContextMenuHandlers): NonNullable<MenuProps['items']> {
  const { contextNode, contextTargetSuffix, anyBusy, debugSession, nodeContextMenu } = state;
  return [
    { key: 'configure', label: `配置节点${contextTargetSuffix}`, disabled: !contextNode, onClick: () => { if (contextNode) handlers.openNodeConfiguration(contextNode.id); } },
    { key: 'view-image', label: `查看图像${contextTargetSuffix}`, disabled: !contextNode, onClick: () => { if (contextNode) handlers.openNodeConfiguration(contextNode.id, 'image'); } },
    { key: 'run-node', label: `运行此节点${contextTargetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !contextNode || contextNode.data.typeKey === 'module.call', onClick: handlers.runNode },
    { key: 'run-from-here', label: `从此处运行${contextTargetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !contextNode || contextNode.data.typeKey === 'module.call', onClick: handlers.runFromHere },
    { key: 'breakpoint', label: `切换断点${contextTargetSuffix}`, disabled: !contextNode, onClick: handlers.toggleBreakpoint },
    { type: 'divider' },
    { key: 'copy', label: `复制节点${contextTargetSuffix}`, extra: 'Ctrl+C', onClick: handlers.copySelection },
    { key: 'delete', label: `删除节点${contextTargetSuffix}`, extra: 'Del', danger: true, disabled: !contextNode, onClick: () => { if (nodeContextMenu) handlers.deleteElements([nodeContextMenu.nodeId], []); } }
  ];
}

export function buildDeviceMenuItems(openers: PanelOpeners): NonNullable<MenuProps['items']> {
  return [
    { key: 'cameras', label: '相机', onClick: () => openers.setCamerasOpen(true) },
    { key: 'robots', label: '机器人', onClick: () => openers.setRobotsOpen(true) },
    { key: 'devices', label: '设备 / PLC', onClick: () => openers.setDevicesOpen(true) },
    { type: 'divider' },
    { key: 'calibration', label: '标定', onClick: () => openers.setCalibrationOpen(true) },
    { key: 'frames', label: '坐标系', onClick: () => openers.setFramesOpen(true) },
    { type: 'divider' },
    { key: 'hardware', label: '硬件信息', onClick: () => openers.setHardwareProvenanceOpen(true) },
    { key: 'diagnostics', label: '诊断中心', onClick: () => openers.setDiagnosticsOpen(true) }
  ];
}

export function buildProductionMenuItems(openers: PanelOpeners): NonNullable<MenuProps['items']> {
  return [
    { key: 'jobs', label: '配方管理', onClick: () => openers.setJobsOpen(true) },
    { key: 'traces', label: '运行追溯', onClick: () => openers.setTracesOpen(true) },
    { key: 'production', label: '生产运行', onClick: () => openers.setProductionOpen(true) }
  ];
}

export type ToolMenuContext = {
  catalogSource: 'runtime' | 'fallback';
  catalogCount: number;
  catalogSchemaVersion: number;
  loadedPluginCount: number;
  history: { canUndo: boolean; canRedo: boolean; undoLabel?: string; redoLabel?: string };
  themeMode: 'light' | 'dark';
  undo: () => void;
  redo: () => void;
  toggleTheme: () => void;
  openWorkflowPicker: () => void;
};

export function buildToolMenuItems(context: ToolMenuContext, openers: PanelOpeners): NonNullable<MenuProps['items']> {
  return [
    // 目录/插件规模从顶栏收进菜单，降低顶栏视觉权重
    { type: 'group', label: `节点目录 · ${context.catalogSource === 'runtime' ? '运行时' : '本地'} ${context.catalogCount}（schema ${context.catalogSchemaVersion}）` },
    { type: 'group', label: `插件 ${context.loadedPluginCount} 个已加载` },
    { type: 'divider' },
    { key: 'undo', label: `撤销${context.history.undoLabel ? ` · ${context.history.undoLabel}` : ''}（Ctrl+Z）`, disabled: !context.history.canUndo, onClick: context.undo },
    { key: 'redo', label: `重做${context.history.redoLabel ? ` · ${context.history.redoLabel}` : ''}（Ctrl+Shift+Z）`, disabled: !context.history.canRedo, onClick: context.redo },
    { key: 'shortcuts', label: '键盘快捷键…', onClick: () => openers.setShortcutsOpen(true) },
    { type: 'divider' },
    { key: 'plugins', label: '插件', onClick: () => openers.setPluginsOpen(true) },
    { key: 'modules', label: '可复用模块', onClick: () => openers.setModulesOpen(true) },
    { key: 'tuning', label: '参数调优', onClick: () => openers.setTuningOpen(true) },
    { key: 'model-packages', label: '外部训练包', onClick: () => openers.setModelPackagesOpen(true) },
    { key: 'model-assets', label: '模型资产', onClick: () => openers.setModelAssetsOpen(true) },
    { key: 'validation', label: '数据集验证', onClick: () => openers.setValidationOpen(true) },
    { type: 'divider' },
    { key: 'layout-edit', label: '布局：标准布局' },
    { key: 'layout-image', label: '布局：宽画布' },
    { key: 'layout-reset', label: '布局：复位为预设尺寸' },
    { type: 'divider' },
    { key: 'load', label: '加载已保存工作流', onClick: () => context.openWorkflowPicker() },
    { key: 'theme', label: context.themeMode === 'dark' ? '切换到浅色主题' : '切换到深色主题', onClick: context.toggleTheme },
    { key: 'about', label: '关于 VisionStudio', onClick: () => openers.setAboutOpen(true) }
  ];
}

export type MoreMenuContext = {
  showTemplates: () => void;
  openWorkflowPicker: () => void;
  applyLayoutPreset: (preset: LayoutPreset) => void;
  layoutPreset: LayoutPreset | 'custom';
};

export function buildMoreMenu(
  context: MoreMenuContext, debugMenuItems: NonNullable<MenuProps['items']>, deviceMenuItems: NonNullable<MenuProps['items']>,
  productionMenuItems: NonNullable<MenuProps['items']>, toolMenuItems: NonNullable<MenuProps['items']>
): MenuProps {
  return {
    items: [
      { key: 'more-template', label: '从模板新建', onClick: context.showTemplates },
      { key: 'more-open', label: '打开已有流程', onClick: () => context.openWorkflowPicker() },
      { type: 'divider' },
      ...debugMenuItems,
      ...deviceMenuItems,
      ...productionMenuItems,
      ...toolMenuItems
    ],
    selectable: true,
    selectedKeys: context.layoutPreset === 'custom' ? [] : [context.layoutPreset],
    onClick: ({ key }) => {
      if (key === 'layout-edit') context.applyLayoutPreset('edit');
      else if (key === 'layout-image') context.applyLayoutPreset('image');
      else if (key === 'layout-reset') context.applyLayoutPreset(context.layoutPreset === 'custom' ? 'edit' : context.layoutPreset);
    }
  };
}
