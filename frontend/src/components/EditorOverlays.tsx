/**
 * 编辑器弹层装配（巨型组件拆分，2026-11-13）：DSL 查看、未保存修改守卫、命令面板、
 * 模板库、工作流选择与快捷键说明。开关与回调由 App 注入。
 */
import { lazy, Suspense } from 'react';
import { Button, Input, Modal } from 'antd';
import type { PaletteCommand } from './CommandPalette';
import type { DemoKey } from '../demos';
import type { NodeCatalogItem } from '../types';
import type { Node } from '@xyflow/react';
import type { useWorkflowChanges } from '../useWorkflowChanges';
import type { useWorkflowImport } from '../useWorkflowImport';
import type { WorkflowDocumentSummary } from '../appEditorShared';

const CommandPalette = lazy(() => import('./CommandPalette'));
const ShortcutHelpModal = lazy(() => import('./ShortcutHelpModal'));
const TemplateGalleryModal = lazy(() => import('./TemplateGalleryModal'));
const WorkflowPickerModal = lazy(() => import('./WorkflowPickerModal'));
const SaveAsWorkflowModal = lazy(() => import('./SaveAsWorkflowModal'));

export type EditorOverlaysProps = {
  compiledDsl: string | undefined;
  closeDsl: () => void;
  workflowChanges: ReturnType<typeof useWorkflowChanges>;
  pendingGuard: ReturnType<typeof useWorkflowChanges>['pending'];
  guardingSave: boolean;
  commandOpen: boolean;
  setCommandOpen: (value: boolean) => void;
  catalog: NodeCatalogItem[];
  nodes: Node[];
  onLocateNode: (nodeId: string) => void;
  paletteTargets: PaletteCommand[];
  runPaletteCommand: (key: string) => void;
  addNode: (typeKey: string, position?: { x: number; y: number }) => void;
  templateOpen: boolean;
  setTemplateOpen: (value: boolean) => void;
  demoKey: DemoKey;
  guardUnsaved: (label: string, run: () => void) => void;
  applyDemo: (key: DemoKey) => void;
  newBlankWorkflow: () => void;
  importWorkflowJson: () => void;
  openWorkflowPicker: () => Promise<void>;
  workflowPickerOpen: boolean;
  setWorkflowPickerOpen: (value: boolean) => void;
  savedWorkflows: WorkflowDocumentSummary[];
  workflowListLoading: boolean;
  workflowListError: string | undefined;
  workflowToLoad: string | undefined;
  setWorkflowToLoad: (value: string | undefined) => void;
  loadSelectedSavedWorkflow: () => void;
  loadSavedWorkflowById: (id: string) => void;
  deleteSavedWorkflow: (id: string) => Promise<boolean>;
  workflowDeletingId: string | undefined;
  workflowImport: ReturnType<typeof useWorkflowImport>;
  saveAsOpen: boolean;
  setSaveAsOpen: (value: boolean) => void;
  designerWorkflowId: string;
  designerWorkflowName: string;
  saveBusy: boolean;
  onConfirmSaveAs: (id: string, name: string) => void;
  shortcutsOpen: boolean;
  setShortcutsOpen: (value: boolean) => void;
  renameTarget?: { id: string; label: string };
  renameDraft: string;
  setRenameDraft: (value: string) => void;
  onConfirmRenameNode: () => void;
  onCancelRenameNode: () => void;
};

export default function EditorOverlays({
  compiledDsl, closeDsl, workflowChanges, pendingGuard, guardingSave,
  commandOpen, setCommandOpen, catalog, nodes, onLocateNode, paletteTargets, runPaletteCommand, addNode,
  templateOpen, setTemplateOpen, demoKey, guardUnsaved, applyDemo, newBlankWorkflow, importWorkflowJson, openWorkflowPicker,
  workflowPickerOpen, setWorkflowPickerOpen, savedWorkflows, workflowListLoading, workflowListError,
  workflowToLoad, setWorkflowToLoad, loadSelectedSavedWorkflow, loadSavedWorkflowById, deleteSavedWorkflow, workflowDeletingId, workflowImport,
  saveAsOpen, setSaveAsOpen, designerWorkflowId, designerWorkflowName, saveBusy, onConfirmSaveAs,
  shortcutsOpen, setShortcutsOpen, renameTarget, renameDraft, setRenameDraft, onConfirmRenameNode, onCancelRenameNode
}: EditorOverlaysProps) {
  return (
    <>
      <Modal
        open={compiledDsl !== undefined}
        title="已编译的工作流 DSL"
        width="min(860px, calc(100vw - 32px))"
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
  
      {commandOpen && (
        <Suspense fallback={null}>
          <CommandPalette open onClose={() => setCommandOpen(false)} catalog={catalog}
            nodes={nodes} targets={paletteTargets} onRunCommand={runPaletteCommand}
            onAddNode={(type) => addNode(type)} onLocateNode={onLocateNode} />
        </Suspense>
      )}

      <Modal open={Boolean(renameTarget)} title="重命名节点" width={420}
        onCancel={onCancelRenameNode} onOk={onConfirmRenameNode}
        okButtonProps={{ disabled: !renameDraft.trim() || renameDraft.trim() === renameTarget?.label }}
        destroyOnHidden>
        <Input autoFocus maxLength={80} value={renameDraft} aria-label="节点名称"
          onChange={(event) => setRenameDraft(event.target.value)}
          onPressEnter={() => { if (renameDraft.trim() && renameDraft.trim() !== renameTarget?.label) onConfirmRenameNode(); }} />
      </Modal>
  
      {/* P0-2：模板入口弹窗——分组/用途/预览，替换当前流程前显式确认；“打开已有流程”就近放置 */}
      {templateOpen && (
        <Suspense fallback={null}>
          <TemplateGalleryModal open onClose={() => setTemplateOpen(false)}
            currentDemoKey={demoKey} catalog={catalog}
            onApply={(key) => guardUnsaved('从模板新建', () => applyDemo(key))}
            onNewBlank={newBlankWorkflow}
            onImportJson={importWorkflowJson}
            onOpenSaved={() => void openWorkflowPicker()} />
        </Suspense>
      )}
  
      {workflowPickerOpen && (
        <Suspense fallback={null}>
          <WorkflowPickerModal open onClose={() => setWorkflowPickerOpen(false)}
            workflows={savedWorkflows} loading={workflowListLoading} error={workflowListError}
            selected={workflowToLoad} onSelect={setWorkflowToLoad}
            onLoad={(id) => { if (id) loadSavedWorkflowById(id); else loadSelectedSavedWorkflow(); }}
            onDelete={deleteSavedWorkflow}
            deletingId={workflowDeletingId}
            onImportJson={importWorkflowJson}
            applying={workflowImport.loading} />
        </Suspense>
      )}

      {saveAsOpen && (
        <Suspense fallback={null}>
          <SaveAsWorkflowModal open currentId={designerWorkflowId} currentName={designerWorkflowName}
            busy={saveBusy} onClose={() => setSaveAsOpen(false)} onConfirm={onConfirmSaveAs} />
        </Suspense>
      )}
  
      {/* 键盘快捷键一览（工具菜单）：快捷键此前只散落在悬停提示里，没有可查入口 */}
      {shortcutsOpen && (
        <Suspense fallback={null}>
          <ShortcutHelpModal open onClose={() => setShortcutsOpen(false)} />
        </Suspense>
      )}
    </>
  );
}
