/**
 * 节点配置对话框（巨型组件拆分，2026-11-13）。
 *
 * 状态与派生模型见 ./useNodeConfigurationDialogModel，渲染见 ./NodeConfigurationDialogView；
 * 本文件只保留提交 / 关闭 / 测试运行等动作与弹窗外壳。行为、文案与 ARIA 名称保持不变。
 */
import { useEffect, useRef } from 'react';
import type { Edge, Node } from '@xyflow/react';
import { Alert, Button, Modal, Tabs, Tooltip } from 'antd';
import type { NodeCatalogItem, RunResult, VisionRoi } from '../types';
import NodeCategoryIcon from './NodeCategoryIcon';
import NodeExecutionResult from './NodeExecutionResult';
import TemplateAssetPicker from './TemplateAssetPicker';
import {
  cloneParameters, parameterFingerprint, stripInlineModelWhenAssetBound, withParameter, type NodeParameters
} from '../nodeConfigurationState';
import { appendShapeModelVersion, createShapeModelAsset } from '../modelAssets';
import { capturedReference } from '../roiReference';
import { maturityLabel, maturityTitle } from '../nodeMaturity';
import { useNodeConfigurationDialogModel } from './useNodeConfigurationDialogModel';
import {
  NodeConfigurationDetails, NodeConfigurationWorkspace, type NodeConfigurationView
} from './NodeConfigurationDialogView';

export type NodeConfigurationTab = 'parameters' | 'image' | 'details' | 'result';

export type Props = {
  node: Node;
  catalogItem: NodeCatalogItem;
  catalogItems?: NodeCatalogItem[];
  nodes: Node[];
  edges: Edge[];
  open: boolean;
  disabled: boolean;
  onClose: () => void;
  onApply: (parameters: NodeParameters) => boolean | void;
  onPreview: (parameters: NodeParameters) => Promise<RunResult>;
  previewBlockedReason?: string;
  initialTab?: NodeConfigurationTab;
  result?: RunResult;
  resultReceivedAt?: string;
  resultStale?: boolean;
};

export default function NodeConfigurationDialog(props: Props) {
  const { onClose, onApply } = props;
  const model = useNodeConfigurationDialogModel(props);
  const {
    node, catalogItem, open, onPreview, previewBlockedReason,
    initialTab, result, resultReceivedAt, setImageSource, selectedInputAvailable, shapeWorkspaceTab,
    setShapeWorkspaceTab, changeShapeWorkspaceTab, setShapeImageKind, setTrainingView, setShowSavedTrainingImage, setSearchWorkspacePreview,
    trainingBusy, trainedModelData, legacyWorkflow, trainingStale, shapeSearchBlocked, hasLosslessTrainingImage,
    fetchTrainingImage, fetchSearchImage, trainShapeModel, catalogMaturity, activeTab, setActiveTab,
    maximized, setMaximized, workspaceRef, resultRef, focusResult, draft,
    setDraft, setAssetBusy, setAssetMessage, cropClearUndo, setCropClearUndo, setPreviewNodeId,
    previewBusy, setPreviewBusy, setRegionEditMode, setRoiImageView, previewError, setPreviewError,
    preview, setPreview, searchPreview, setSearchPreview, setFocusTick, dirty,
    changedCount, discardOpen, setDiscardOpen, externalChange, locked, isShapeMatch,
    modelAssetId, isImageCrop, isRoiTransform, connectedRoiImageFrameMismatch, roiInputConnected,
    cropRoiInputConnected, legacyCropBoundsPresent, cropClearUndoAvailable, measurementGeometryConflict, measurementGeometryConflictMessage, debugImageConnected,
    referenceBindings, referenceMismatch, displayedResult, resultExpanded, setResultExpanded,
    receivedAt, stale, matchStale, issues,
  } = model;
  const changeParameter = (key: string, value: unknown) => {
    if (locked) return;
    setDraft((current) => withParameter(current, key, value));
    if (isImageCrop && key === 'roi') setImageSource('input');
  };
  const changeCropRoi = (roi?: VisionRoi) => {
    if (locked) return;
    setCropClearUndo(undefined);
    setImageSource('input');
    setDraft((current) => {
      const next = { ...current };
      for (const key of ['x', 'y', 'width', 'height']) delete next[key];
      if (roi) next.roi = roi; else delete next.roi;
      return next;
    });
  };
  const changePerspectiveQuad = (roi?: VisionRoi) => {
    if (locked) return;
    setImageSource('input');
    setDraft((current) => {
      const next = { ...current };
      const points = roi?.type === 'Polygon' && roi.points.length === 4 ? roi.points : undefined;
      if (!points) {
        for (const key of ['x1', 'y1', 'x2', 'y2', 'x3', 'y3', 'x4', 'y4']) delete next[key];
        return next;
      }
      points.forEach((point, index) => {
        next[`x${index + 1}`] = Number(point.x.toFixed(3));
        next[`y${index + 1}`] = Number(point.y.toFixed(3));
      });
      return next;
    });
  };
  const clearCropSelection = () => {
    if (locked || cropRoiInputConnected || (!draft.roi && !legacyCropBoundsPresent)) return;
    const next = { ...draft };
    for (const key of ['x', 'y', 'width', 'height']) delete next[key];
    delete next.roi;
    setCropClearUndo({ previous: cloneParameters(draft), afterFingerprint: parameterFingerprint(next) });
    setImageSource('input');
    setDraft(next);
  };
  const undoCropSelectionClear = () => {
    if (!cropClearUndoAvailable || !cropClearUndo) return;
    setDraft(cloneParameters(cropClearUndo.previous));
    setCropClearUndo(undefined);
    setImageSource('input');
  };
  const runPreview = async () => {
    if (locked || issues.length || externalChange || previewBlockedReason || shapeSearchBlocked || referenceMismatch || measurementGeometryConflict
      || connectedRoiImageFrameMismatch) return;
    if (isShapeMatch) { setShapeWorkspaceTab('search'); setShapeImageKind(undefined); setRegionEditMode('search'); }
    setTrainingView(false);
    setShowSavedTrainingImage(false);
    const submitted = cloneParameters(draft);
    if (isRoiTransform && debugImageConnected && !roiInputConnected && !draft.roi)
      submitted.debugImagePreviewOnly = true;
    else
      delete submitted.debugImagePreviewOnly;
    setPreviewBusy(true);
    setPreviewError(undefined);
    try {
      const next = await onPreview(submitted);
      const completedPreview = { result: next, fingerprint: parameterFingerprint(submitted), receivedAt: new Date().toISOString() };
      setPreview(completedPreview);
      if (isShapeMatch) { setSearchPreview(completedPreview); setSearchWorkspacePreview(completedPreview); setPreviewNodeId(node.id); setImageSource('output'); }
      if (isRoiTransform) { setRoiImageView('output'); setImageSource('output'); }
      if (isShapeMatch) setShapeImageKind('search');
      if (isImageCrop) { setPreviewNodeId(node.id); setImageSource('output'); }
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '节点测试失败。');
      setResultExpanded(true);
    } finally { setPreviewBusy(false); }
  };
  const loadMetrologyImage = async () => {
    if (locked || externalChange || previewBlockedReason) return;
    setPreviewBusy(true); setPreviewError(undefined);
    try {
      const next = await onPreview({ ...cloneParameters(draft), debugImagePreviewOnly: true });
      if (!next.success) throw new Error(next.error || '无法获取输入图像。');
      setPreview({ result: next, fingerprint: parameterFingerprint(draft), receivedAt: new Date().toISOString() });
      setPreviewNodeId(node.id); setImageSource('input');
    } catch (error) { setPreviewError(error instanceof Error ? error.message : '无法获取输入图像。'); }
    finally { setPreviewBusy(false); }
  };
  const captureRoiReference = async () => {
    if (locked || externalChange || previewBlockedReason || !debugImageConnected) return;
    setPreviewBusy(true); setPreviewError(undefined);
    try {
      const next = await onPreview({ ...cloneParameters(draft), captureReference: true });
      const report = next.nodeReports.filter(item => item.nodeId === node.id).slice(-1)[0];
      if (!next.success || !report?.success)
        throw new Error(report?.error || next.error || '无法建立基准，请确认定位已找到目标。');
      const receivedAt = new Date().toISOString();
      const completed = capturedReference(draft, report.summary, referenceBindings, receivedAt);
      setDraft(completed); setRoiImageView('reference'); setPreviewNodeId(node.id); setResultExpanded(false);
      const safeResult = { ...next, nodeReports: next.nodeReports.map(item => ({ ...item,
        summary: Object.fromEntries(Object.entries(item.summary).filter(([key]) => key !== 'referenceImage')) })) };
      setPreview({ result: safeResult, fingerprint: parameterFingerprint(completed), receivedAt, preserveTrainingWorkspace: true });
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '基准建立失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); }
  };
  const saveAsAsset = async () => {
    const modelBase64 = typeof draft.trainedModelData === 'string' ? draft.trainedModelData : '';
    if (!modelBase64) return;
    const trainingConfig = typeof draft.trainedModelConfig === 'string' ? draft.trainedModelConfig : '';
    setAssetBusy(true);
    try {
      if (modelAssetId) {
        const version = await appendShapeModelVersion(modelAssetId, { modelBase64, trainingConfig, note: 'Saved from training workspace' });
        setDraft((previous) => ({ ...previous, modelAssetVersion: version.version, modelAssetSha256: version.sha256 }));
        setAssetMessage(`已追加为资产 ${modelAssetId} v${version.version}；应用后节点优先使用该版本。`);
      } else {
        const created = await createShapeModelAsset({
          name: `${catalogItem.displayName} · ${node.id}`,
          description: `Saved from node ${node.id}`,
          modelBase64,
          trainingConfig,
          note: 'Saved from training workspace'
        });
        const latest = created.versions.find((item) => item.version === created.latestVersion);
        setDraft((previous) => ({ ...previous, modelAssetId: created.id, modelAssetVersion: created.latestVersion, modelAssetSha256: latest?.sha256 ?? '' }));
        setAssetMessage(`已保存为资产 ${created.id} v${created.latestVersion}；应用后节点优先使用该资产。`);
      }
    } catch (error) {
      setAssetMessage(error instanceof Error ? `保存模型资产失败：${error.message}` : '保存模型资产失败。');
    } finally {
      setAssetBusy(false);
    }
  };
  const apply = () => {
    if (locked || issues.length || externalChange || referenceMismatch || !dirty || isShapeMatch && !legacyWorkflow && trainingStale) return;
    // 已保存为资产后不再把内联模型写回文档：体积从 MB 级降到 KB 级；再次调参时由资产回读。
    const parameters = stripInlineModelWhenAssetBound(catalogItem.type, cloneParameters(draft));
    if (onApply(parameters) !== false) onClose();
  };

  // P0（评审，实测复现）：预览结束后运行按钮短暂 disabled，焦点落回 body——antd Modal 的 Esc
  // 监听依赖弹窗内焦点，于是“运行完按 Esc 关不掉”。完成后把焦点还给弹窗（正在输入参数时不抢焦点）。
  const previewPreviousBusy = useRef(false);
  useEffect(() => {
    const wasBusy = previewPreviousBusy.current;
    previewPreviousBusy.current = previewBusy;
    if (!wasBusy || previewBusy) return;
    const active = document.activeElement as HTMLElement | null;
    if (active && active.closest('input, textarea, select, [contenteditable="true"], .ant-select, .ant-input-number')) return;
    workspaceRef.current?.focus({ preventScroll: true });
  }, [previewBusy]);

  // P1（评审）：弹窗打开时主程序会吞掉 Ctrl+S（preventDefault 后无动作，属“按键黑洞”）。
  // 这里把它变成“应用”，并补 Ctrl+Enter“运行此节点”；用 ref 读取最新闭包，避免每次渲染重注册监听。
  const applyRef = useRef(apply); applyRef.current = apply;
  const runPreviewRef = useRef(runPreview); runPreviewRef.current = isShapeMatch && shapeWorkspaceTab === 'training' ? trainShapeModel : runPreview;
  useEffect(() => {
    if (!open) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      const key = event.key.toLowerCase();
      if (key === 's') { event.preventDefault(); applyRef.current(); }
      else if (key === 'enter') { event.preventDefault(); runPreviewRef.current(); }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [open]);

  // P0（评审）：未应用的修改不能再被 ✕/Esc 静默丢弃——“取消”保持直接废弃语义，
  // 但 ✕ 与 Esc（遮罩关闭本来就没开）统一先确认，避免误按丢失整轮参数调整。
  const requestClose = () => {
    if (dirty) { setDiscardOpen(true); return; }
    onClose();
  };
  const locateRoi = (key?: string) => {
    if (isShapeMatch) {
      if (key === 'searchRoi') { setShapeWorkspaceTab('search'); void fetchSearchImage(); return; }
      setShapeWorkspaceTab('training');
      setRegionEditMode(key === 'clutterRoi' ? 'clutter' : 'model');
      void fetchTrainingImage();
      return;
    }
    setActiveTab('workspace');
    setPreviewNodeId(node.id);
    if (isRoiTransform) { setRoiImageView('reference'); setFocusTick(value => value + 1); return; }
    if (isImageCrop) {
      setImageSource('input');
      setFocusTick((value) => value + 1);
      return;
    }
    setImageSource(selectedInputAvailable ? 'input' : 'output');
    setFocusTick((value) => value + 1);
  };

  const view: NodeConfigurationView = {
    ...model, changeParameter, changeCropRoi, changePerspectiveQuad, runPreview, loadMetrologyImage,
    captureRoiReference, saveAsAsset, clearCropSelection, undoCropSelectionClear, locateRoi
  };

  return <>
    <Modal open={open} onCancel={requestClose} mask={{ closable: false }} destroyOnHidden
      afterOpenChange={(visible) => { if (visible && initialTab === 'result') focusResult(); }}
      className={'node-configuration-modal' + (maximized ? ' is-maximized' : '')} width={maximized ? '100vw' : 'min(1680px, calc(100vw - 32px))'} style={{ top: maximized ? 0 : 16 }}
      title={<div className="node-configuration-title"><NodeCategoryIcon type={catalogItem.type} /><span>节点配置 · {String(node.data.label ?? catalogItem.displayName)}</span>
        {catalogMaturity !== 'stable' && <span className={`maturity-badge maturity-${catalogMaturity}`}
          data-maturity={catalogMaturity} title={maturityTitle(catalogMaturity, 'zh')}>{maturityLabel(catalogMaturity, 'zh')}</span>}
        <Button className="node-dialog-maximize" size="small" type="text" aria-label={maximized ? '还原窗口' : '最大化窗口'} title={maximized ? '还原窗口' : '最大化窗口'} onClick={() => setMaximized((value) => !value)}>{maximized ? '⤡' : '⤢'}</Button>
      </div>}
      footer={<div className="node-configuration-footer">
        <span>{dirty ? '修改已暂存；取消或关闭将丢弃本次修改' : '参数与当前流程一致'}{previewBusy ? ' · 测试中' : ''}</span>
        <div>
          <Tooltip title={measurementGeometryConflictMessage || previewBlockedReason || (isShapeMatch && shapeWorkspaceTab === 'training' ? '使用当前显示的训练图像建立模型（Ctrl+Enter）' : shapeSearchBlocked ? '请先训练或重新训练模型' : '使用暂存参数测试此节点，并预热上游输入；不写回流程参数。（Ctrl+Enter）')}>
            <Button aria-label={isShapeMatch ? shapeWorkspaceTab === 'training' ? legacyWorkflow ? '训练并切换到训练模式' : trainedModelData ? '重新训练模型' : '训练模型' : '运行搜索' : '运行此节点'}
              onClick={() => void (isShapeMatch && shapeWorkspaceTab === 'training' ? trainShapeModel() : runPreview())} loading={isShapeMatch && shapeWorkspaceTab === 'training' ? trainingBusy : previewBusy}
              disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || measurementGeometryConflict || connectedRoiImageFrameMismatch || (shapeWorkspaceTab !== 'training' && shapeSearchBlocked) || (isShapeMatch && shapeWorkspaceTab === 'training' && !hasLosslessTrainingImage) || referenceMismatch}>
              {isShapeMatch ? shapeWorkspaceTab === 'training' ? legacyWorkflow ? '训练并切换到训练模式' : trainedModelData ? '重新训练模型' : '训练模型' : '运行搜索' : '运行此节点'}
            </Button>
          </Tooltip>
          {/* 关闭语义（2026-10-25 复核）：✕/Esc 是"手滑也会碰到"的路径，因此二次确认；
              "取消"是明确点选的动作，保持一次点击关闭——差异是刻意的，不强行统一。
              仅补 title 说明后果，避免用户以为"取消"会保留暂存修改。 */}
          <Button onClick={onClose} title={dirty ? '丢弃本次未应用的参数修改并关闭' : '关闭'}>取消</Button><Button type="primary" onClick={apply} disabled={locked || !dirty || issues.length > 0 || externalChange || referenceMismatch || isShapeMatch && !legacyWorkflow && trainingStale} title="应用暂存参数到流程（Ctrl+S）">应用</Button>
        </div>
      </div>}>
      {externalChange && <Alert type="warning" showIcon title="流程中的节点参数已更新，请关闭后重新打开，避免覆盖其他修改。" />}
      {catalogItem.type === 'match.template' && <TemplateAssetPicker
        assetId={typeof draft.templateAssetId === 'string' ? draft.templateAssetId.trim() : ''}
        version={Number(draft.templateAssetVersion ?? 0)}
        disabled={locked}
        onChange={(assetId, version) => setDraft((previous) => ({ ...previous, templateAssetId: assetId, templateAssetVersion: version }))} />}
      <Tabs activeKey={activeTab} onChange={setActiveTab} items={[
        { key: 'workspace', label: '配置', forceRender: true, children: <>
          {isShapeMatch
            ? <Tabs className="shape-match-workspace-tabs" activeKey={shapeWorkspaceTab} onChange={changeShapeWorkspaceTab} items={[
              { key: 'training', label: '模型训练', disabled: locked, children: shapeWorkspaceTab === 'training' ? <NodeConfigurationWorkspace model={view} shapeMode="training" /> : null },
              { key: 'search', label: '搜索推理', disabled: locked, children: shapeWorkspaceTab === 'search' ? <NodeConfigurationWorkspace model={view} shapeMode="search" /> : null }
            ]} />
            : <NodeConfigurationWorkspace model={view} />}
          <div ref={resultRef} tabIndex={-1} className="node-result-anchor">
            <NodeExecutionResult nodeId={node.id} catalog={catalogItem} result={isShapeMatch ? searchPreview?.result ?? result : displayedResult}
              receivedAt={isShapeMatch ? searchPreview?.receivedAt ?? resultReceivedAt : receivedAt}
              stale={isShapeMatch ? matchStale : stale} draftResult={isShapeMatch ? Boolean(searchPreview) : Boolean(preview)}
              expanded={resultExpanded} onExpandedChange={setResultExpanded} requestError={previewError} />
          </div>
        </> },
        { key: 'details', label: '详细说明', children: <NodeConfigurationDetails model={view} /> }
      ]} />
    </Modal>
    {/* P0（评审）：✕ / Esc 丢弃未应用修改前的确认（“取消”按钮保持直接废弃语义） */}
    <Modal open={discardOpen} title="放弃未应用的参数修改？" width={440} centered
      onCancel={() => setDiscardOpen(false)}
      footer={[
        <Button key="edit" onClick={() => setDiscardOpen(false)}>继续编辑</Button>,
        <Button key="discard" danger onClick={() => { setDiscardOpen(false); onClose(); }}>放弃修改</Button>
      ]}>
      <p className="node-discard-hint">当前有 {changedCount} 项参数修改尚未“应用”，关闭弹窗后将被丢弃。若要保留，请返回并点击“应用”。</p>
    </Modal>
  </>;
}
