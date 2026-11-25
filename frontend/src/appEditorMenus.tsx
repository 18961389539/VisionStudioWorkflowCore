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
import type { Lang } from './i18n/lang';

const txt = (lang: Lang, zh: string, en: string) => (lang === 'en' ? en : zh);

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
  setSpcOpen: (value: boolean) => void;
  setDiagnosticsOpen: (value: boolean) => void;
  setStorageOpen: (value: boolean) => void;
  setSecurityOpen: (value: boolean) => void;
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
  newBlank?: () => void;
  saveAs?: () => void;
  importJson?: () => void;
  exportJson?: () => void;
  autoLayout?: () => void;
  fitView: () => void;
  toggleFocusMode: () => void;
  toggleTheme: () => void;
  toggleLang: () => void;
};

export type PaletteContext = PanelOpeners & EditorCommands & { focusMode: boolean };

export function buildPaletteTargets(focusMode: boolean, lang: Lang = 'zh'): PaletteCommand[] {
  const t = (zh: string, en: string) => txt(lang, zh, en);
  return [
    { key: 'run', title: t('运行流程', 'Run workflow'), group: '命令', hint: 'Ctrl+Shift+Enter', keywords: 'run execute' },
    { key: 'validate', title: t('校验流程', 'Validate workflow'), group: '命令', hint: 'Validate', keywords: 'validate check' },
    { key: 'save', title: t('保存工作流', 'Save workflow'), group: '命令', hint: 'Ctrl+S', keywords: 'save' },
    { key: 'save-as', title: t('另存为新工作流…', 'Save workflow as…'), group: '命令', keywords: 'save as copy duplicate 另存为' },
    { key: 'new-blank', title: t('新建空白流程', 'New blank workflow'), group: '命令', keywords: 'new blank empty 新建 空白' },
    { key: 'import-json', title: t('导入工作流 JSON…', 'Import workflow JSON…'), group: '命令', keywords: 'import json file 导入' },
    { key: 'export-json', title: t('导出工作流 JSON', 'Export workflow JSON'), group: '命令', keywords: 'export json download 导出' },
    { key: 'auto-layout', title: t('自动整理画布布局', 'Auto-layout canvas'), group: '命令', keywords: 'auto layout arrange tidy 整理 排布' },
    { key: 'fit', title: t('适应画布', 'Fit view'), group: '命令', hint: 'Ctrl+0', keywords: 'fit zoom 缩放' },
    { key: 'focus', title: focusMode ? t('退出专注画布', 'Exit focus mode') : t('专注画布', 'Focus canvas'), group: '命令', keywords: 'focus fullscreen 专注' },
    { key: 'theme', title: t('切换深浅主题', 'Toggle light/dark theme'), group: '命令', keywords: 'theme dark light 主题' },
    { key: 'lang', title: '切换界面语言 / Switch language', group: '命令', keywords: 'language lang 语言 english' },
    { key: 'shortcuts', title: t('键盘快捷键', 'Keyboard shortcuts'), group: '命令', keywords: 'shortcut keyboard 快捷键 help' },
    { key: 'about', title: t('关于 VisionStudio', 'About VisionStudio'), group: '命令', keywords: 'about version 版本 关于' },
    { key: 'panel:calibration', title: t('标定', 'Calibration'), group: '面板', keywords: 'calibration' },
    { key: 'panel:traces', title: t('运行追溯', 'Traceability'), group: '面板', keywords: 'trace' },
    { key: 'panel:jobs', title: t('配方管理', 'Recipes & jobs'), group: '面板', keywords: 'job recipe' },
    { key: 'panel:validation', title: t('数据集 / 批量验证', 'Dataset validation'), group: '面板', keywords: 'dataset validation' },
    { key: 'panel:tuning', title: t('参数调优', 'Parameter tuning'), group: '面板', keywords: 'tuning' },
    { key: 'panel:modules', title: t('可复用模块', 'Reusable modules'), group: '面板', keywords: 'module' },
    { key: 'panel:cameras', title: t('相机采集', 'Cameras'), group: '面板', keywords: 'camera' },
    { key: 'panel:robots', title: t('机器人', 'Robots'), group: '面板', keywords: 'robot' },
    { key: 'panel:devices', title: t('设备运行时', 'Devices / PLC'), group: '面板', keywords: 'device plc' },
    { key: 'panel:production', title: t('生产运行', 'Production'), group: '面板', keywords: 'production' },
    { key: 'panel:spc', title: t('SPC 看板', 'SPC dashboard'), group: '面板', keywords: 'spc control chart 控制图 过程能力' },
    { key: 'panel:diagnostics', title: t('诊断中心', 'Diagnostics'), group: '面板', keywords: 'diagnostics' },
    { key: 'panel:storage', title: t('存储维护与备份', 'Storage & backups'), group: '面板', keywords: 'storage backup restore 备份 还原' },
    { key: 'panel:security', title: t('安全与运维管理', 'Security & admin'), group: '面板', keywords: 'security user token audit 用户 令牌 审计' },
    { key: 'panel:plugins', title: t('插件包管理', 'Plugin packages'), group: '面板', keywords: 'plugin' },
    { key: 'panel:models', title: t('外部训练包', 'Model packages'), group: '面板', keywords: 'model package' },
    { key: 'panel:model-assets', title: t('模型资产', 'Model assets'), group: '面板', keywords: 'model asset shape 资产' },
    { key: 'panel:frames', title: t('坐标系树', 'Coordinate frames'), group: '面板', keywords: 'frame 坐标' },
    { key: 'panel:provenance', title: t('硬件溯源', 'Hardware provenance'), group: '面板', keywords: 'provenance' }
  ];
}

export function runPaletteCommand(key: string, context: PaletteContext): void {
  switch (key) {
    case 'run': context.run(); return;
    case 'validate': context.validate(); return;
    case 'save': context.save(); return;
    case 'save-as': context.saveAs?.(); return;
    case 'new-blank': context.newBlank?.(); return;
    case 'import-json': context.importJson?.(); return;
    case 'export-json': context.exportJson?.(); return;
    case 'auto-layout': context.autoLayout?.(); return;
    case 'fit': context.fitView(); return;
    case 'focus': context.toggleFocusMode(); return;
    case 'theme': context.toggleTheme(); return;
    case 'lang': context.toggleLang(); return;
    case 'shortcuts': context.setShortcutsOpen(true); return;
    case 'about': context.setAboutOpen(true); return;
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
    case 'panel:spc': context.setSpcOpen(true); return;
    case 'panel:diagnostics': context.setDiagnosticsOpen(true); return;
    case 'panel:storage': context.setStorageOpen(true); return;
    case 'panel:security': context.setSecurityOpen(true); return;
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
  requestRenameNode: (nodeId: string) => void;
  runNode: () => void;
  runFromHere: () => void;
  toggleBreakpoint: () => void;
  copySelection: () => boolean;
  deleteElements: (nodeIds: string[], edgeIds: string[], bridge?: boolean) => void;
};

export type ContextMenuState = {
  contextNode: Node | undefined;
  contextTargetSuffix: string;
  anyBusy: boolean;
  debugSession: { status: string } | undefined;
  nodeContextMenu: { nodeId: string } | undefined;
  lang?: Lang;
};

export function buildNodeContextMenuItems(state: ContextMenuState, handlers: ContextMenuHandlers): NonNullable<MenuProps['items']> {
  const { contextNode, contextTargetSuffix, anyBusy, debugSession, nodeContextMenu, lang = 'zh' } = state;
  const t = (zh: string, en: string) => txt(lang, zh, en);
  return [
    { key: 'configure', label: `配置节点${contextTargetSuffix}`, disabled: !contextNode, onClick: () => { if (contextNode) handlers.openNodeConfiguration(contextNode.id); } },
    { key: 'rename', label: t('重命名节点…', 'Rename node…'), disabled: !contextNode || anyBusy,
      onClick: () => { if (contextNode) handlers.requestRenameNode(contextNode.id); } },
    { key: 'view-image', label: `查看图像${contextTargetSuffix}`, disabled: !contextNode, onClick: () => { if (contextNode) handlers.openNodeConfiguration(contextNode.id, 'image'); } },
    { key: 'run-node', label: `运行此节点${contextTargetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !contextNode || contextNode.data.typeKey === 'module.call', onClick: handlers.runNode },
    { key: 'run-from-here', label: `从此处运行${contextTargetSuffix}`, disabled: anyBusy || Boolean(debugSession) || !contextNode || contextNode.data.typeKey === 'module.call', onClick: handlers.runFromHere },
    { key: 'breakpoint', label: `切换断点${contextTargetSuffix}`, disabled: !contextNode, onClick: handlers.toggleBreakpoint },
    { type: 'divider' },
    { key: 'copy', label: `复制节点${contextTargetSuffix}`, extra: 'Ctrl+C', onClick: handlers.copySelection },
    { key: 'delete', label: `删除节点${contextTargetSuffix}`, extra: 'Del', danger: true, disabled: !contextNode, onClick: () => { if (nodeContextMenu) handlers.deleteElements([nodeContextMenu.nodeId], []); } }
  ];
}

export function buildDeviceMenuItems(openers: PanelOpeners, lang: Lang = 'zh'): NonNullable<MenuProps['items']> {
  const t = (zh: string, en: string) => txt(lang, zh, en);
  return [
    { key: 'cameras', label: t('相机', 'Cameras'), onClick: () => openers.setCamerasOpen(true) },
    { key: 'robots', label: t('机器人', 'Robots'), onClick: () => openers.setRobotsOpen(true) },
    { key: 'devices', label: t('设备 / PLC', 'Devices / PLC'), onClick: () => openers.setDevicesOpen(true) },
    { type: 'divider' },
    { key: 'calibration', label: t('标定', 'Calibration'), onClick: () => openers.setCalibrationOpen(true) },
    { key: 'frames', label: t('坐标系', 'Frames'), onClick: () => openers.setFramesOpen(true) },
    { type: 'divider' },
    { key: 'hardware', label: t('硬件信息', 'Hardware provenance'), onClick: () => openers.setHardwareProvenanceOpen(true) },
    { key: 'diagnostics', label: t('诊断中心', 'Diagnostics'), onClick: () => openers.setDiagnosticsOpen(true) }
  ];
}

export function buildProductionMenuItems(openers: PanelOpeners, lang: Lang = 'zh'): NonNullable<MenuProps['items']> {
  const t = (zh: string, en: string) => txt(lang, zh, en);
  return [
    { key: 'jobs', label: t('配方管理', 'Recipes'), onClick: () => openers.setJobsOpen(true) },
    { key: 'traces', label: t('运行追溯', 'Traceability'), onClick: () => openers.setTracesOpen(true) },
    { key: 'production', label: t('生产运行', 'Production'), onClick: () => openers.setProductionOpen(true) },
    { key: 'spc', label: t('SPC 看板', 'SPC dashboard'), onClick: () => openers.setSpcOpen(true) }
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
  reopenOnboarding: () => void;
};

export function buildToolMenuItems(context: ToolMenuContext, openers: PanelOpeners, lang: Lang = 'zh'): NonNullable<MenuProps['items']> {
  const t = (zh: string, en: string) => txt(lang, zh, en);
  return [
    // 目录/插件规模从顶栏收进菜单，降低顶栏视觉权重
    { type: 'group', label: `${t('节点目录', 'Catalog')} · ${context.catalogSource === 'runtime' ? t('运行时', 'runtime') : t('本地', 'local')} ${context.catalogCount}（schema ${context.catalogSchemaVersion}）` },
    { type: 'group', label: t(`插件 ${context.loadedPluginCount} 个已加载`, `${context.loadedPluginCount} plugins loaded`) },
    { type: 'divider' },
    { key: 'undo', label: `${t('撤销', 'Undo')}${context.history.undoLabel ? ` · ${context.history.undoLabel}` : ''}（Ctrl+Z）`, disabled: !context.history.canUndo, onClick: context.undo },
    { key: 'redo', label: `${t('重做', 'Redo')}${context.history.redoLabel ? ` · ${context.history.redoLabel}` : ''}（Ctrl+Shift+Z）`, disabled: !context.history.canRedo, onClick: context.redo },
    { key: 'shortcuts', label: t('键盘快捷键…', 'Keyboard shortcuts…'), onClick: () => openers.setShortcutsOpen(true) },
    { key: 'onboarding', label: t('重新打开上手引导', 'Reopen onboarding guide'), onClick: context.reopenOnboarding },
    { type: 'divider' },
    { key: 'plugins', label: t('插件', 'Plugins'), onClick: () => openers.setPluginsOpen(true) },
    { key: 'modules', label: t('可复用模块', 'Reusable modules'), onClick: () => openers.setModulesOpen(true) },
    { key: 'tuning', label: t('参数调优', 'Parameter tuning'), onClick: () => openers.setTuningOpen(true) },
    { key: 'model-packages', label: t('外部训练包', 'Model packages'), onClick: () => openers.setModelPackagesOpen(true) },
    { key: 'model-assets', label: t('模型资产', 'Model assets'), onClick: () => openers.setModelAssetsOpen(true) },
    { key: 'validation', label: t('数据集验证', 'Dataset validation'), onClick: () => openers.setValidationOpen(true) },
    { key: 'storage', label: t('存储维护与备份', 'Storage & backups'), onClick: () => openers.setStorageOpen(true) },
    { key: 'security', label: t('安全与运维管理', 'Security & admin'), onClick: () => openers.setSecurityOpen(true) },
    { type: 'divider' },
    { key: 'layout-edit', label: t('布局：标准布局', 'Layout: Standard') },
    { key: 'layout-image', label: t('布局：宽画布', 'Layout: Wide') },
    { key: 'layout-reset', label: t('布局：复位为预设尺寸', 'Layout: Reset sizes') },
    { type: 'divider' },
    { key: 'load', label: t('加载已保存工作流', 'Open saved workflow'), onClick: () => context.openWorkflowPicker() },
    { key: 'theme', label: context.themeMode === 'dark' ? t('切换到浅色主题', 'Switch to light theme') : t('切换到深色主题', 'Switch to dark theme'), onClick: context.toggleTheme },
    { key: 'about', label: t('关于 VisionStudio', 'About VisionStudio'), onClick: () => openers.setAboutOpen(true) }
  ];
}

export type FileMenuHandlers = {
  newBlank: () => void;
  saveAs: () => void;
  importJson: () => void;
  exportJson: () => void;
};

export function buildFileMenuItems(handlers: FileMenuHandlers, lang: Lang = 'zh'): NonNullable<MenuProps['items']> {
  const t = (zh: string, en: string) => txt(lang, zh, en);
  return [
    { key: 'file-new-blank', label: t('新建空白流程', 'New blank workflow'), onClick: handlers.newBlank },
    { key: 'file-save-as', label: t('另存为…', 'Save as…'), onClick: handlers.saveAs },
    { type: 'divider' },
    { key: 'file-import-json', label: t('导入工作流 JSON…', 'Import workflow JSON…'), onClick: handlers.importJson },
    { key: 'file-export-json', label: t('导出工作流 JSON', 'Export workflow JSON'), onClick: handlers.exportJson },
  ];
}

export type MoreMenuContext = {
  showTemplates: () => void;
  openWorkflowPicker: () => void;
  newBlank?: () => void;
  saveAs?: () => void;
  importJson?: () => void;
  exportJson?: () => void;
  applyLayoutPreset: (preset: LayoutPreset) => void;
  layoutPreset: LayoutPreset | 'custom';
};

export function buildMoreMenu(
  context: MoreMenuContext, debugMenuItems: NonNullable<MenuProps['items']>, deviceMenuItems: NonNullable<MenuProps['items']>,
  productionMenuItems: NonNullable<MenuProps['items']>, toolMenuItems: NonNullable<MenuProps['items']>, lang: Lang = 'zh'
): MenuProps {
  const t = (zh: string, en: string) => txt(lang, zh, en);
  return {
    items: [
      { key: 'more-template', label: t('从模板新建', 'New from template'), onClick: context.showTemplates },
      { key: 'more-open', label: t('打开已有流程', 'Open workflow'), onClick: () => context.openWorkflowPicker() },
      ...(context.newBlank ? [{ key: 'more-new-blank', label: t('新建空白流程', 'New blank workflow'), onClick: context.newBlank }] : []),
      ...(context.saveAs ? [{ key: 'more-save-as', label: t('另存为…', 'Save as…'), onClick: context.saveAs }] : []),
      ...(context.importJson ? [{ key: 'more-import-json', label: t('导入工作流 JSON…', 'Import workflow JSON…'), onClick: context.importJson }] : []),
      ...(context.exportJson ? [{ key: 'more-export-json', label: t('导出工作流 JSON', 'Export workflow JSON'), onClick: context.exportJson }] : []),
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
