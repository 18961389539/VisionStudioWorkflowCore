/**
 * 编辑器弹层装配（巨型组件拆分，2026-11-13）：DSL 查看、未保存修改守卫、命令面板、
 * 模板库、工作流选择与快捷键说明。开关与回调由 App 注入。
 */
import { Button, Modal } from 'antd';
import CommandPalette, { type PaletteCommand } from './CommandPalette';
import ShortcutHelpModal from './ShortcutHelpModal';
import TemplateGalleryModal from './TemplateGalleryModal';
import WorkflowPickerModal from './WorkflowPickerModal';
import type { DemoKey } from '../demos';
import type { NodeCatalogItem } from '../types';
import type { useWorkflowChanges } from '../useWorkflowChanges';
import type { useWorkflowImport } from '../useWorkflowImport';
import type { WorkflowDocumentSummary } from '../appEditorShared';

export type EditorOverlaysProps = {
  compiledDsl: string | undefined;
  closeDsl: () => void;
  workflowChanges: ReturnType<typeof useWorkflowChanges>;
  pendingGuard: ReturnType<typeof useWorkflowChanges>['pending'];
  guardingSave: boolean;
  commandOpen: boolean;
  setCommandOpen: (value: boolean) => void;
  catalog: NodeCatalogItem[];
  paletteTargets: PaletteCommand[];
  runPaletteCommand: (key: string) => void;
  addNode: (typeKey: string, position?: { x: number; y: number }) => void;
  templateOpen: boolean;
  setTemplateOpen: (value: boolean) => void;
  demoKey: DemoKey;
  guardUnsaved: (label: string, run: () => void) => void;
  applyDemo: (key: DemoKey) => void;
  openWorkflowPicker: () => Promise<void>;
  workflowPickerOpen: boolean;
  setWorkflowPickerOpen: (value: boolean) => void;
  savedWorkflows: WorkflowDocumentSummary[];
  workflowListLoading: boolean;
  workflowListError: string | undefined;
  workflowToLoad: string | undefined;
  setWorkflowToLoad: (value: string | undefined) => void;
  loadSelectedSavedWorkflow: () => void;
  workflowImport: ReturnType<typeof useWorkflowImport>;
  shortcutsOpen: boolean;
  setShortcutsOpen: (value: boolean) => void;
};

export default function EditorOverlays({
  compiledDsl, closeDsl, workflowChanges, pendingGuard, guardingSave,
  commandOpen, setCommandOpen, catalog, paletteTargets, runPaletteCommand, addNode,
  templateOpen, setTemplateOpen, demoKey, guardUnsaved, applyDemo, openWorkflowPicker,
  workflowPickerOpen, setWorkflowPickerOpen, savedWorkflows, workflowListLoading, workflowListError,
  workflowToLoad, setWorkflowToLoad, loadSelectedSavedWorkflow, workflowImport,
  shortcutsOpen, setShortcutsOpen
}: EditorOverlaysProps) {
  return (
    <>
      <Modal
        open={compiledDsl !== undefined}
        title="已编译的工作流 DSL"
        width={860}
        footer={null}
        onCancel={closeDsl}
      >
        <pre className="dsl-view">{compiledDsl}</pre>
      </Modal>
  
      <Modal
        open={pendingGuard !== undefined}
        title="存在未保存的修改"
        width={460}
        closable={!guardingSave}
        keyboard={!guardingSave}
        maskClosable={!guardingSave}
        onCancel={() => { if (!guardingSave) workflowChanges.cancel(); }}
        footer={[
          <Button key="cancel" disabled={guardingSave} onClick={workflowChanges.cancel}>取消</Button>,
          <Button key="discard" danger disabled={guardingSave} onClick={workflowChanges.discard}>丢弃修改</Button>,
          <Button
            key="save"
            type="primary"
            loading={guardingSave}
            onClick={() => void workflowChanges.saveAndContinue()}
          >
            保存并继续
          </Button>
        ]}
      >
        <p>当前工作流有未保存的修改，{pendingGuard?.label}前请选择如何处理：</p>
      </Modal>
  
      <CommandPalette open={commandOpen} onClose={() => setCommandOpen(false)} catalog={catalog}
        targets={paletteTargets} onRunCommand={runPaletteCommand} onAddNode={(type) => addNode(type)} />
  
      {/* P0-2：模板入口弹窗——分组/用途/预览，替换当前流程前显式确认；“打开已有流程”就近放置 */}
      <TemplateGalleryModal open={templateOpen} onClose={() => setTemplateOpen(false)}
        currentDemoKey={demoKey} catalog={catalog}
        onApply={(key) => guardUnsaved('从模板新建', () => applyDemo(key))}
        onOpenSaved={() => void openWorkflowPicker()} />
  
      <WorkflowPickerModal open={workflowPickerOpen} onClose={() => setWorkflowPickerOpen(false)}
        workflows={savedWorkflows} loading={workflowListLoading} error={workflowListError}
        selected={workflowToLoad} onSelect={setWorkflowToLoad}
        onLoad={loadSelectedSavedWorkflow} applying={workflowImport.loading} />
  
      {/* 键盘快捷键一览（工具菜单）：快捷键此前只散落在悬停提示里，没有可查入口 */}
      <ShortcutHelpModal open={shortcutsOpen} onClose={() => setShortcutsOpen(false)} />
    </>
  );
}
