import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import type { Edge, Node } from '@xyflow/react';
import { Alert, Button, Collapse, Modal, Select, Segmented, Tabs, Tag, Tooltip } from 'antd';
import type { NodeCatalogItem, ParameterDescriptor, PortDescriptor, RunResult, VisionOverlay, VisionRoi } from '../types';
import NodeCategoryIcon from './NodeCategoryIcon';
import PropertyPanel from './PropertyPanel';
import ImageViewer from './ImageViewer';
import NodeExecutionResult from './NodeExecutionResult';
import SplitHandle from './SplitHandle';
import { useRunImages } from '../useRunImages';
import { cloneParameters, configurationIssues, configurationParameters, parameterFingerprint, withParameter, type NodeParameters } from '../nodeConfigurationState';

export type NodeConfigurationTab = 'parameters' | 'image' | 'details' | 'result';

type Props = {
  node: Node;
  catalogItem: NodeCatalogItem;
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

function PortTable({ ports, input }: { ports: PortDescriptor[]; input?: boolean }) {
  if (!ports.length) return <p className="node-info-muted">无{input ? '输入' : '输出'}端口</p>;
  return <div className="node-info-table-wrap"><table className="node-info-table">
    <thead><tr><th>端口</th><th>数据类型</th>{input && <th>连接要求</th>}</tr></thead>
    <tbody>{ports.map((port) => <tr key={port.name}>
      <td>{port.label ?? port.name}{port.label && port.label !== port.name && <code>{port.name}</code>}</td>
      <td>{port.dataType}</td>
      {input && <td>{port.required ? '必需' : '可选'}</td>}
    </tr>)}</tbody>
  </table></div>;
}

/** 参数说明行的元信息：默认值 / 取值范围 / 步进——目录里都有，此前说明页只有一句兜底文案。 */
function parameterMeta(parameter: ParameterDescriptor) {
  const bits: string[] = [];
  if (parameter.defaultValue !== undefined && parameter.defaultValue !== null && parameter.defaultValue !== '')
    bits.push(`默认 ${String(parameter.defaultValue)}`);
  if (parameter.min !== undefined || parameter.max !== undefined)
    bits.push(`范围 ${parameter.min ?? '—'} ~ ${parameter.max ?? '—'}`);
  if (parameter.step !== undefined) bits.push(`步进 ${parameter.step}`);
  return bits.join(' · ');
}

export default function NodeConfigurationDialog({ node, catalogItem, nodes, edges, open, disabled, onClose, onApply, onPreview, previewBlockedReason, initialTab = 'parameters', result, resultReceivedAt, resultStale = false }: Props) {
  // result 是节点“详情”的定位入口，结果统一在配置页展示。
  const [activeTab, setActiveTab] = useState<string>(initialTab === 'details' ? 'details' : 'workspace');
  const [shapeWorkspaceTab, setShapeWorkspaceTab] = useState<'training' | 'search'>(() => {
    const parameters = (node.data.parameters ?? {}) as NodeParameters;
    return typeof parameters.trainedModelData === 'string' && parameters.trainedModelData ? 'search' : 'training';
  });
  const [shapeImageKind, setShapeImageKind] = useState<'training' | 'search'>();
  const [maximized, setMaximized] = useState(false);
  const [imageShare, setImageShare] = useState(67);
  const [workspaceWidth, setWorkspaceWidth] = useState(1000);
  const workspaceRef = useRef<HTMLDivElement>(null);
  const columns = (share: number) => `minmax(260px, ${share}fr) 10px minmax(280px, ${100 - share}fr)`;
  const previewShare = (share: number) => workspaceRef.current?.style.setProperty('--node-columns', columns(share));
  const commitShare = (share: number) => { previewShare(share); setImageShare(share); };
  const bindWorkspace = useCallback((workspace: HTMLDivElement | null) => {
    workspaceRef.current = workspace;
    if (!workspace) return;
    const observer = new ResizeObserver(([entry]) => setWorkspaceWidth(Math.max(1, entry.contentRect.width)));
    setWorkspaceWidth(Math.max(1, workspace.getBoundingClientRect().width));
    observer.observe(workspace);
    return () => { observer.disconnect(); workspaceRef.current = null; };
  }, []);
  const resultRef = useRef<HTMLDivElement>(null);
  const focusResult = () => {
    resultRef.current?.focus({ preventScroll: true });
    resultRef.current?.scrollIntoView?.({ block: 'nearest' });
  };
  useEffect(() => setActiveTab(initialTab === 'details' ? 'details' : 'workspace'), [initialTab]);
  useEffect(() => {
    if (open && initialTab === 'result' && activeTab === 'workspace') {
      const frame = requestAnimationFrame(focusResult);
      return () => cancelAnimationFrame(frame);
    }
  }, [open, initialTab, activeTab]);
  const [baseline] = useState(() => configurationParameters((node.data.parameters ?? {}) as NodeParameters, catalogItem));
  const [draft, setDraft] = useState(() => cloneParameters(baseline));
  const [previewNodeId, setPreviewNodeId] = useState(node.id);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [trainBusy, setTrainBusy] = useState(false);
  const [trainingImageBusy, setTrainingImageBusy] = useState(false);
  const [searchImageBusy, setSearchImageBusy] = useState(false);
  const [trainingView, setTrainingView] = useState(false);
  const [regionEditMode, setRegionEditMode] = useState<'model' | 'search'>('model');
  const [showSavedTrainingImage, setShowSavedTrainingImage] = useState(false);
  const [previewError, setPreviewError] = useState<string>();
  const [preview, setPreview] = useState<{ result: RunResult; fingerprint: string; receivedAt: string; preserveTrainingWorkspace?: boolean }>();
  const [focusTick, setFocusTick] = useState(0);
  const draftFingerprint = parameterFingerprint(draft);
  const dirty = draftFingerprint !== parameterFingerprint(baseline);
  // 与打开弹窗时的值不同的参数项数：用于参数区“已改 N 项”与关闭确认
  const changedCount = [...new Set([...Object.keys(baseline), ...Object.keys(draft)])]
    .filter((key) => parameterFingerprint(draft[key]) !== parameterFingerprint(baseline[key])).length;
  const [discardOpen, setDiscardOpen] = useState(false);
  const externalChange = parameterFingerprint(configurationParameters((node.data.parameters ?? {}) as NodeParameters, catalogItem)) !== parameterFingerprint(baseline);
  const locked = disabled || previewBusy;
  const issues = configurationIssues(catalogItem, draft);
  const isShapeMatch = catalogItem.type === 'match.shape';
  const isImageCrop = catalogItem.type === 'image.crop';
  const trainedModelData = typeof draft.trainedModelData === 'string' ? draft.trainedModelData : '';
  const legacyWorkflow = isShapeMatch && draft.modelMode === 'legacy';
  const trainedSettings = draft.trainedModelSettings && typeof draft.trainedModelSettings === 'object'
    ? draft.trainedModelSettings as Record<string, unknown> : undefined;
  const currentTrainingSettings = {
    binarize: draft.binarize ?? 'auto', threshold: draft.threshold ?? 128, invert: draft.invert ?? false,
    angleRangeDeg: draft.angleRangeDeg ?? 180, angleStepDeg: draft.angleStepDeg ?? 1,
    scaleTolerancePercent: draft.scaleTolerancePercent ?? 0, scaleStepPercent: Number(draft.scaleTolerancePercent ?? 0) > 0 ? draft.scaleStepPercent ?? 1 : 0,
    ignorePolarity: draft.ignorePolarity ?? false, minContrast: draft.minContrast ?? 5,
    trainingOriginMode: draft.trainingOriginMode ?? 'centroid', trainingOriginX: draft.trainingOriginX ?? 0,
    trainingOriginY: draft.trainingOriginY ?? 0, roi: draft.roi ?? null, templatePath: draft.templatePath ?? ''
  };
  const trainingSettingsWithDefaults = trainedSettings && (() => {
    const normalized = { scaleTolerancePercent: 0, scaleStepPercent: 1, ignorePolarity: false, minContrast: 5, ...trainedSettings };
    if (Number(normalized.scaleTolerancePercent) === 0) normalized.scaleStepPercent = 0;
    return normalized;
  })();
  const trainingStale = Boolean(trainedModelData && trainingSettingsWithDefaults && parameterFingerprint(trainingSettingsWithDefaults) !== parameterFingerprint(currentTrainingSettings));
  const shapeSearchBlocked = isShapeMatch && (!trainedModelData && !legacyWorkflow || trainingStale);
  const trainingBusy = trainBusy;
  const templateSourceNodeId = edges.find((edge) => edge.target === node.id && (edge.targetHandle === 'template' || edge.targetHandle?.endsWith('template')))?.source;
  const displayedResult = preview?.result ?? result;
  const resultFailed = Boolean(displayedResult && (!displayedResult.success || displayedResult.nodeReports?.filter((report) => report.nodeId === node.id).slice(-1).some((report) => !report.success)));
  const [resultExpanded, setResultExpanded] = useState(() => initialTab === 'result' || resultFailed);
  useEffect(() => { if (initialTab === 'result') setResultExpanded(true); }, [initialTab]);
  // 新的失败报告自动展开；成功报告保留用户当前的展开状态。
  useEffect(() => { if (resultFailed) setResultExpanded(true); }, [displayedResult, resultFailed]);
  // P1（评审）：新的“运行此节点”结果落地后自动展开结果并滚入视口——此前值表格在首屏外
  //（被页脚裁切，需滚动整个弹窗体），参数少时右列还有大片空白。
  useEffect(() => {
    if (!preview || preview.preserveTrainingWorkspace) return;
    setResultExpanded(true);
    const frame = requestAnimationFrame(() => resultRef.current?.scrollIntoView?.({ block: 'nearest' }));
    return () => cancelAnimationFrame(frame);
  }, [preview]);
  const receivedAt = preview?.receivedAt ?? resultReceivedAt;
  const stale = Boolean(previewError) || (preview ? preview.fingerprint !== draftFingerprint : resultStale || dirty);
  const nodeLabels = Object.fromEntries(nodes.map((item) => [item.id, String(item.data.label ?? item.id)]));
  const { imageSource, setImageSource, imageViewerSource, viewerOverlays, selectedOutputAvailable, selectedInputAvailable,
    imageCatalogLoading, imageCatalogError, retryImageCatalog } =
    useRunImages({ result: displayedResult, selectedId: previewNodeId, nodes, edges, nodeLabels });
  useEffect(() => {
    if (!open || !isImageCrop) return;
    setPreviewNodeId(node.id);
    setImageSource('input');
  }, [isImageCrop, node.id, open, setImageSource]);
  const trainingContours = Array.isArray(draft.trainedContours) ? draft.trainedContours as { x: number; y: number }[][]
    : Array.isArray(draft.trainedContour) ? [draft.trainedContour as { x: number; y: number }[]] : [];
  const trainedPreviewData = typeof draft.trainedPreviewImage === 'string' ? draft.trainedPreviewImage : '';
  const showTrainedTemplatePreview = Boolean(showSavedTrainingImage && trainingView && isShapeMatch && trainedPreviewData && imageSource === 'output' &&
    (previewNodeId === templateSourceNodeId || (!templateSourceNodeId && previewNodeId === node.id)));
  const shapeImageMatchesTab = shapeImageKind === shapeWorkspaceTab || (shapeWorkspaceTab === 'training' && showTrainedTemplatePreview);
  const displayImageSource = showTrainedTemplatePreview ? { ...imageViewerSource,
    url: `data:image/jpeg;base64,${trainedPreviewData}`, width: Number(draft.trainedSourceWidth ?? imageViewerSource.width),
    height: Number(draft.trainedSourceHeight ?? imageViewerSource.height), label: '已保存训练图像' }
    : isShapeMatch && !shapeImageMatchesTab ? { ...imageViewerSource, url: '', label: shapeWorkspaceTab === 'training' ? '训练图像' : '搜索场景图像' }
      : imageViewerSource;
  const overlaysWithTrainedContour: VisionOverlay[] = showSavedTrainingImage && trainingView && isShapeMatch && trainedModelData &&
    (previewNodeId === templateSourceNodeId || (!templateSourceNodeId && previewNodeId === node.id))
    ? [...viewerOverlays, ...trainingContours.map((points, index) => ({ id: `saved-shape-training-contour-${index}`, type: 'Polygon' as const, points, label: '已保存模型轮廓片段', stroke: '#13c2c2', strokeWidth: 2 })),
      { id: 'saved-shape-training-origin', type: 'Point', x: Number(draft.trainedOriginImageX ?? 0), y: Number(draft.trainedOriginImageY ?? 0), label: '训练原点', stroke: '#fa541c', strokeWidth: 3 }]
    : viewerOverlays;
  const imagePorts = [...catalogItem.inputs, ...catalogItem.outputs].some((p) => p.dataType === 'Image' || p.dataType === 'DomainImage');
  const showImage = imagePorts || initialTab === 'image';
  const supportsRoi = catalogItem.capabilities?.supportsRoi === true;
  const canEditRoi = supportsRoi && (isShapeMatch
    ? regionEditMode === 'model'
      ? shapeWorkspaceTab === 'training' && shapeImageMatchesTab && imageSource === 'output' && (previewNodeId === templateSourceNodeId || previewNodeId === node.id)
      : shapeWorkspaceTab === 'search' && shapeImageMatchesTab && imageSource === 'output' && previewNodeId === node.id && Boolean(displayImageSource.url)
    : isImageCrop
      ? previewNodeId === node.id && imageSource === 'input' && selectedInputAvailable
      : previewNodeId === node.id && imageSource !== 'preview');
  const draftNode: Node = { ...node, data: { ...node.data, parameters: draft } };
  const changeParameter = (key: string, value: unknown) => {
    if (!locked) setDraft((current) => withParameter(current, key, value));
  };
  const changeCropRoi = (roi?: VisionRoi) => {
    if (locked) return;
    setDraft((current) => {
      const next = { ...current };
      for (const key of ['x', 'y', 'width', 'height']) delete next[key];
      if (roi) next.roi = roi; else delete next.roi;
      return next;
    });
  };
  const runPreview = async () => {
    if (locked || issues.length || externalChange || previewBlockedReason || shapeSearchBlocked) return;
    if (isShapeMatch) { setShapeWorkspaceTab('search'); setShapeImageKind(undefined); setRegionEditMode('search'); }
    setTrainingView(false);
    setShowSavedTrainingImage(false);
    const submitted = cloneParameters(draft);
    setPreviewBusy(true);
    setPreviewError(undefined);
    try {
      const next = await onPreview(submitted);
      setPreview({ result: next, fingerprint: parameterFingerprint(submitted), receivedAt: new Date().toISOString() });
      if (isShapeMatch) setShapeImageKind('search');
      if (isImageCrop) { setPreviewNodeId(node.id); setImageSource('input'); }
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '节点测试失败。');
      setResultExpanded(true);
    } finally { setPreviewBusy(false); }
  };
  const fetchTrainingImage = async () => {
    if (locked || externalChange || previewBlockedReason) return;
    setShapeWorkspaceTab('training'); setShapeImageKind(undefined);
    setPreviewBusy(true); setTrainingImageBusy(true); setPreviewError(undefined); setTrainingView(true); setRegionEditMode('model'); setShowSavedTrainingImage(false);
    try {
      const submitted = cloneParameters({ ...draft, modelMode: 'trained', trainingMode: true, trainingPreviewOnly: true });
      const next = await onPreview(submitted);
      const report = next.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
      if (!next.success || !report?.success || report.summary.trainingPreviewReady !== true)
        throw new Error(report?.error || next.error || '无法获取训练图像，请连接 template 输入或填写模板路径。');
      setPreview({ result: next, fingerprint: draftFingerprint, receivedAt: new Date().toISOString(), preserveTrainingWorkspace: true });
      setShapeImageKind('training');
      setPreviewNodeId(node.id); setImageSource('output');
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '训练图像获取失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); setTrainingImageBusy(false); }
  };
  const fetchSearchImage = async () => {
    if (locked || externalChange || previewBlockedReason) return;
    setShapeWorkspaceTab('search'); setShapeImageKind(undefined);
    setPreviewBusy(true); setSearchImageBusy(true); setPreviewError(undefined); setTrainingView(false); setRegionEditMode('search'); setShowSavedTrainingImage(false);
    try {
      const submitted = cloneParameters({ ...draft, modelMode: 'trained', trainingMode: false, searchPreviewOnly: true });
      const next = await onPreview(submitted);
      const report = next.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
      if (!next.success || !report?.success || report.summary.searchPreviewReady !== true)
        throw new Error(report?.error || next.error || '无法获取场景图像，请确认形状匹配节点已连接 image 输入。');
      setPreview({ result: next, fingerprint: draftFingerprint, receivedAt: new Date().toISOString(), preserveTrainingWorkspace: true });
      setShapeImageKind('search');
      setPreviewNodeId(node.id); setImageSource('output');
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '场景图像获取失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); setSearchImageBusy(false); }
  };
  const trainShapeModel = async () => {
    if (locked || issues.length || externalChange || previewBlockedReason) return;
    const submitted = cloneParameters({ ...draft, modelMode: 'trained', trainingMode: true });
    setShapeWorkspaceTab('training'); setShapeImageKind(undefined); setTrainingView(true);
    setShowSavedTrainingImage(false);
    setPreviewBusy(true); setTrainBusy(true); setPreviewError(undefined);
    try {
      const next = await onPreview(submitted);
      const report = next.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
      const summary = report?.summary ?? {};
      const modelData = typeof summary.trainingModelData === 'string' ? summary.trainingModelData : '';
      if (!next.success || !report?.success || summary.trainingSucceeded !== true || !modelData) {
        throw new Error(report?.error || next.error || '训练没有生成模型，请检查训练图像、ROI 和轮廓参数。');
      }
      const snapshot = {
        binarize: draft.binarize ?? 'auto', threshold: draft.threshold ?? 128, invert: draft.invert ?? false,
        angleRangeDeg: draft.angleRangeDeg ?? 180, angleStepDeg: draft.angleStepDeg ?? 1,
        scaleTolerancePercent: draft.scaleTolerancePercent ?? 0, scaleStepPercent: draft.scaleStepPercent ?? 1,
        ignorePolarity: draft.ignorePolarity ?? false, minContrast: draft.minContrast ?? 5,
        trainingOriginMode: draft.trainingOriginMode ?? 'centroid', trainingOriginX: draft.trainingOriginX ?? 0,
        trainingOriginY: draft.trainingOriginY ?? 0, roi: draft.roi ?? null, templatePath: draft.templatePath ?? ''
      };
      const completed = { ...draft, modelMode: 'trained', trainingMode: false, trainedModelData: modelData,
        trainedModelHash: String(summary.trainingModelHash ?? ''),
        trainedModelConfig: String(summary.trainingConfig ?? ''), trainedModelSettings: snapshot,
        trainedContour: Array.isArray(summary.trainingContour) ? summary.trainingContour : [],
        trainedContours: Array.isArray(summary.trainingContours) ? summary.trainingContours : [],
        trainedPreviewImage: typeof summary.trainingPreviewImage === 'string' ? summary.trainingPreviewImage : '',
        trainedSourceWidth: Number(summary.trainingSourceWidth ?? 0), trainedSourceHeight: Number(summary.trainingSourceHeight ?? 0),
        trainedWidth: Number(summary.trainingWidth ?? 0), trainedHeight: Number(summary.trainingHeight ?? 0),
        trainedOriginX: Number(summary.trainingOriginX ?? 0), trainedOriginY: Number(summary.trainingOriginY ?? 0),
        trainedOriginImageX: Number(summary.trainingOriginImageX ?? 0), trainedOriginImageY: Number(summary.trainingOriginImageY ?? 0) };
      setDraft(completed);
      setShapeImageKind('training');
      const safeResult = { ...next, nodeReports: next.nodeReports.map((item) => {
        if (item.nodeId !== node.id || !item.summary.trainingSucceeded) return item;
        const safeSummary = Object.fromEntries(Object.entries(item.summary).filter(([key]) => key !== 'trainingModelData' && key !== 'trainingPreviewImage'));
        return { ...item, summary: safeSummary };
      }) };
      setPreview({ result: safeResult, fingerprint: parameterFingerprint(completed), receivedAt: new Date().toISOString(), preserveTrainingWorkspace: true });
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '形状模型训练失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); setTrainBusy(false); }
  };
  const apply = () => {
    if (locked || issues.length || externalChange || !dirty) return;
    if (onApply(cloneParameters(draft)) !== false) onClose();
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
  const runPreviewRef = useRef(runPreview); runPreviewRef.current = runPreview;
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
      setRegionEditMode('model');
      void fetchTrainingImage();
      return;
    }
    setActiveTab('workspace');
    setPreviewNodeId(node.id);
    if (isImageCrop) {
      setImageSource('input');
      setFocusTick((value) => value + 1);
      return;
    }
    setImageSource(selectedInputAvailable ? 'input' : 'output');
    setFocusTick((value) => value + 1);
  };
  const locateSavedTrainingImage = () => {
    setShapeWorkspaceTab('training'); setShapeImageKind('training'); setRegionEditMode('model');
    setTrainingView(true); setShowSavedTrainingImage(true); setPreviewNodeId(templateSourceNodeId ?? node.id); setImageSource('output'); setFocusTick((value) => value + 1);
  };
  const changeShapeWorkspaceTab = (key: string) => {
    if (locked) return;
    const next = key === 'search' ? 'search' : 'training';
    setShapeWorkspaceTab(next);
    setShapeImageKind(undefined);
    setShowSavedTrainingImage(false);
    if (next === 'training') void fetchTrainingImage();
    else void fetchSearchImage();
  };
  const details = <div className="node-info">
    <p className="node-info-description">{catalogItem.description || '暂无节点说明。'}</p>
    <h3>输入端口</h3><PortTable ports={catalogItem.inputs} input />
    <h3>输出端口</h3><PortTable ports={catalogItem.outputs} />
    {catalogItem.parameters.length > 0 && <>
      <h3>参数说明</h3>
      <dl className="node-info-parameters">{catalogItem.parameters.map((parameter) => {
        // P1（评审）：说明行补充“默认值 · 范围 · 步进”，描述缺失时不再重复兜底句
        const meta = parameterMeta(parameter);
        return <div key={parameter.name}>
          <dt>{parameter.label}{parameter.unit ? `（${parameter.unit}）` : ''}</dt>
          <dd>
            {parameter.description && <span>{parameter.description}</span>}
            {meta && <span className="node-info-param-meta">{meta}</span>}
            {parameter.options?.length ? <span>可选：{parameter.options.map((option) => option.label).join('、')}</span> : null}
          </dd>
        </div>;
      })}</dl>
    </>}
    <Collapse size="small" items={[{ key: 'technical', label: '技术信息', children: <div className="node-info-technical">
      <span>节点类型：<code>{catalogItem.type}</code></span>
      <span>来源：{catalogItem.pluginId ?? 'builtin'}</span>
      <div>
        {catalogItem.capabilities?.supportsRoi && <Tag>支持 ROI</Tag>}
        {catalogItem.capabilities?.emitsOverlay && <Tag>输出覆盖层</Tag>}
        {catalogItem.capabilities?.deterministic !== undefined && <Tag>{catalogItem.capabilities.deterministic ? '确定性执行' : '非确定性执行'}</Tag>}
      </div>
    </div> }]} />
  </div>;

  const renderWorkspace = (shapeMode?: 'training' | 'search') => {
    const trainingMode = shapeMode === 'training';
    const shapePanelCatalogItem = shapeMode ? {
      ...catalogItem,
      parameters: catalogItem.parameters.filter((parameter) => {
        const group = parameter.group ?? 'General';
        const searchRange = parameter.name === 'angleRangeDeg' || parameter.name === 'scaleTolerancePercent';
        return trainingMode
          ? ['Training', 'Binarize', 'Candidates', 'General'].includes(group) && !searchRange
          : ['Matching', 'Multi-target', 'Search'].includes(group) || searchRange;
      }).map((parameter) => ({
        ...parameter,
        group: ({
          Training: '模型训练', Binarize: '图像预处理', Candidates: '特征筛选', General: '训练图像来源',
          Matching: '匹配条件', 'Multi-target': '多目标定位', Search: '搜索范围'
        } as Record<string, string>)[parameter.name === 'angleRangeDeg' || parameter.name === 'scaleTolerancePercent'
          ? 'Search' : parameter.group ?? 'General'] ?? parameter.group
      }))
    } : catalogItem;
    const roiFields = shapeMode ? [trainingMode
      ? { key: 'roi', label: '模型区域', emptyText: '未设置 · 使用整幅训练图像', showEmpty: true }
      : { key: 'searchRoi', label: '搜索区域', emptyText: '未设置 · 使用整幅场景图像', showEmpty: true }
    ] : isImageCrop ? [{ key: 'roi', label: '裁剪区域', emptyText: '未绘制 · 使用旧版坐标或整幅图像', showEmpty: true }] : undefined;
    return <div ref={bindWorkspace} tabIndex={-1} className={'node-configuration-workspace' + (showImage ? '' : ' without-image')}
      style={{ '--node-columns': columns(imageShare) } as CSSProperties}>
      {showImage && <section className="node-configuration-image" aria-label={shapeMode === 'training' ? '模型训练图像' : shapeMode === 'search' ? '搜索推理图像' : '图像预览'}>
        <div className="node-configuration-image-source">
          {shapeMode ? <>
            <span>{trainingMode ? '训练图像' : '搜索场景图像'}</span>
            <Button size="small" onClick={() => void (trainingMode ? fetchTrainingImage() : fetchSearchImage())}
              loading={trainingMode ? trainingImageBusy : searchImageBusy}
              disabled={locked || externalChange || Boolean(previewBlockedReason)}
              title={previewBlockedReason || (trainingMode ? '读取训练源图像并编辑模型区域' : '读取运行场景图像并编辑搜索区域')}>
              {trainingMode ? '获取训练图像' : '获取场景图像'}
            </Button>
            {trainingMode && trainedModelData && trainedPreviewData &&
              <Button size="small" onClick={locateSavedTrainingImage}>查看已保存训练图像</Button>}
          </> : isImageCrop ? <>
            <span>裁剪输入图像</span>
            <Segmented size="small" value={imageSource === 'output' ? 'output' : 'input'} onChange={(value) => setImageSource(value as 'input' | 'output')}
              options={[{ label: '输入图像', value: 'input', disabled: !selectedInputAvailable }, { label: '裁剪输出', value: 'output', disabled: !selectedOutputAvailable }]} />
          </> : <>
            <span>预览节点</span><Select size="small" aria-label="预览节点" showSearch optionFilterProp="label" value={previewNodeId}
              style={{ minWidth: 130, flex: '1 1 130px' }} options={nodes.map((item) => ({ value: item.id, label: nodeLabels[item.id] }))}
              onChange={(id) => { setPreviewNodeId(id); setImageSource('output'); }} />
            <Segmented size="small" value={imageSource} onChange={(value) => setImageSource(value as 'preview' | 'output' | 'input')}
              options={[{ label: '最终预览', value: 'preview' }, { label: '输出', value: 'output', disabled: !selectedOutputAvailable }, { label: '输入', value: 'input', disabled: !selectedInputAvailable }]} />
          </>}
        </div>
        <div className="node-image-provenance"><span>{displayImageSource.label}</span>
          {imageCatalogLoading && <span>正在加载节点图像…</span>}
          {imageCatalogError && <span role="alert" title={imageCatalogError}>节点图像加载失败 <Button size="small" onClick={retryImageCatalog}>重试加载</Button></span>}
          {receivedAt && <span>更新于 {new Date(receivedAt).toLocaleString()}</span>}
          {stale && displayedResult && <Tag color="gold">上次运行图像 · 当前参数待验证</Tag>}
        </div>
        <div className="node-configuration-viewer"><ImageViewer previewUrl={displayImageSource.url} imageWidth={displayImageSource.width} imageHeight={displayImageSource.height}
          overlays={overlaysWithTrainedContour} roi={canEditRoi ? (shapeMode === 'search' ? draft.searchRoi as VisionRoi | undefined : draft.roi as VisionRoi | undefined) : undefined} showRoiTools={canEditRoi}
          roiEnabled={canEditRoi && !locked && Boolean(displayImageSource.url)} roiTargetLabel={isImageCrop ? '裁剪输入图像' : shapeMode === 'search' ? '搜索区域' : shapeMode === 'training' ? '模型区域' : String(node.data.label ?? node.id)}
          loading={previewBusy} onRoiChange={(roi) => isImageCrop ? changeCropRoi(roi) : changeParameter(shapeMode === 'search' ? 'searchRoi' : 'roi', roi)} nodeLabels={nodeLabels} focusTick={focusTick}
          emptyHint={isImageCrop ? '运行节点以加载输入图像，然后绘制裁剪区域' : shapeMode === 'training' ? '获取训练图像后可绘制模型区域' : shapeMode === 'search' ? '获取场景图像后可绘制搜索区域' : '运行此节点以显示图像与矢量覆盖层'}
          emptyAction={{
            label: isImageCrop ? '运行以获取输入图像' : shapeMode === 'training' ? '获取训练图像' : shapeMode === 'search' ? '获取场景图像' : '运行此节点',
            onClick: () => void (shapeMode === 'training' ? fetchTrainingImage() : shapeMode === 'search' ? fetchSearchImage() : runPreview()),
            disabled: locked || (!shapeMode && issues.length > 0) || externalChange || Boolean(previewBlockedReason),
            title: previewBlockedReason || (shapeMode ? '读取当前步骤的图像' : '使用暂存参数测试此节点（Ctrl+Enter）')
          }} /></div>
      </section>}
      {showImage && <SplitHandle axis="x" label="调整图像与参数宽度" value={imageShare} min={35} max={80} scale={100 / workspaceWidth}
        collapsed={false} collapsible={false} collapseGlyph="" expandGlyph="" onToggleCollapse={() => {}}
        preview={previewShare} commit={commitShare} reset={() => commitShare(67)} />}
      <section className="node-configuration-parameters" aria-label={trainingMode ? '模型训练参数' : shapeMode === 'search' ? '搜索推理参数' : '节点参数'}>
        <div className="node-parameter-heading"><strong>{shapeMode ? trainingMode ? '模型训练参数（暂存）' : '搜索推理参数（暂存）' : '参数（暂存）'}</strong>
          <span className="node-parameter-tools">
            {dirty && <Tag color="gold" className="node-parameter-dirty" title="与打开弹窗时的值不同">已改 {changedCount} 项</Tag>}
            <Button size="small" type="link" disabled={!dirty || locked} onClick={() => setDraft(cloneParameters(baseline))}>还原为打开时</Button>
          </span>
        </div>
        {shapeMode === 'training' && <div className="shape-training-panel">
          <strong>建立形状模型</strong>
          <p>选择训练图像和模型区域，设置特征提取、角度/缩放采样步长及训练原点，然后显式训练模型。模型会按搜索页配置的角度与缩放范围构建并随工作流保存。</p>
          {legacyWorkflow
            ? <Alert showIcon type="info" title="旧模板图像兼容模式" description="本次运行使用 template 输入或模板路径；已保存的训练模型仍保留，训练后可切换到训练模型模式。" />
            : trainedModelData
              ? <Alert showIcon type={trainingStale ? 'warning' : 'success'} title={trainingStale ? '训练参数已变化，需要重新训练' : dirty ? '模型已训练，待应用到工作流' : '模型已保存在此工作流'} description={trainingStale ? '应用前请重新训练，运行阶段会拒绝使用过期模型。' : `模型尺寸 ${draft.trainedWidth} × ${draft.trainedHeight} px · 原点 (${Number(draft.trainedOriginX).toFixed(1)}, ${Number(draft.trainedOriginY).toFixed(1)}) px`} />
              : <Alert showIcon type="info" title="尚未训练" description="先获取训练图像并设置模型区域，再训练形状模型。" />}
          <Button type="primary" onClick={() => void trainShapeModel()} loading={trainingBusy}
            disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason)}
            title={previewBlockedReason || '使用当前训练图像、模型区域及训练参数创建并预览形状模型'}>
            {legacyWorkflow ? '训练并切换到训练模式' : trainedModelData ? '重新训练模型' : '训练模型'}
          </Button>
        </div>}
        {shapeMode === 'search' && <div className="shape-training-panel shape-search-panel">
          <strong>搜索与定位</strong>
          <p>设置角度/缩放搜索范围、最低分数和多目标约束；搜索区域仅作用于当前场景图像。角度与缩放范围参与模型构建，修改后需重新训练。</p>
          {draft.searchRoi && <Button size="small" onClick={() => changeParameter('searchRoi', undefined)} disabled={locked}>清除搜索区域（恢复全图搜索）</Button>}
          {!trainedModelData && !legacyWorkflow && <Alert showIcon type="warning" title="尚未训练模型" description="先到“模型训练”页建立并应用模型，才能运行搜索。" />}
          {trainedModelData && trainingStale && <Alert showIcon type="warning" title="搜索范围已变化，模型需要重新训练" description="当前训练模型与角度/缩放范围不一致。请到“模型训练”页重新训练后再运行搜索。" />}
          <Button type="primary" onClick={() => void runPreview()} loading={previewBusy}
            disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || shapeSearchBlocked}
            title={previewBlockedReason || '使用当前搜索图像与匹配参数定位目标'}>运行搜索</Button>
        </div>}
        {isImageCrop && <div className="shape-training-panel crop-settings-panel">
          <strong>选择裁剪区域</strong>
          <p>在输入图像上绘制矩形、圆形或多边形；拖动控制点可微调。输出图像按选区紧包围框裁剪，选区外清零；ROI 蒙版端口输出二值选区。</p>
          {draft.roi && <Button size="small" disabled={locked} onClick={() => changeCropRoi(undefined)}>清除选区（使用整图）</Button>}
          <Button type="primary" size="small" onClick={() => void runPreview()} loading={previewBusy}
            disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason)}
            title={previewBlockedReason || '按当前 ROI 预览裁剪图像与 ROI 蒙版'}>预览裁剪结果</Button>
        </div>}
        {issues.length > 0 && <Alert type="error" title="请修正参数后再运行或应用" description={issues.join('；')} />}
        <PropertyPanel node={draftNode} catalogItem={shapePanelCatalogItem} onChange={changeParameter} disabled={locked}
          roiFields={roiFields} onLocateRoi={showImage && supportsRoi ? locateRoi : undefined} />
      </section>
    </div>;
  };

  return <>
    <Modal open={open} onCancel={requestClose} mask={{ closable: false }} destroyOnHidden
      afterOpenChange={(visible) => { if (visible && initialTab === 'result') focusResult(); }}
      className={'node-configuration-modal' + (maximized ? ' is-maximized' : '')} width={maximized ? '100vw' : 'min(1680px, calc(100vw - 32px))'} style={{ top: maximized ? 0 : 16 }}
      title={<div className="node-configuration-title"><NodeCategoryIcon type={catalogItem.type} /><span>节点配置 · {String(node.data.label ?? catalogItem.displayName)}</span>
        <Button className="node-dialog-maximize" size="small" type="text" aria-label={maximized ? '还原窗口' : '最大化窗口'} title={maximized ? '还原窗口' : '最大化窗口'} onClick={() => setMaximized((value) => !value)}>{maximized ? '⤡' : '⤢'}</Button>
      </div>}
      footer={<div className="node-configuration-footer">
        <span>{dirty ? '修改已暂存；取消或关闭将丢弃本次修改' : '参数与当前流程一致'}{previewBusy ? ' · 测试中' : ''}</span>
        <div>
          <Tooltip title={previewBlockedReason || '使用暂存参数测试此节点，并预热上游输入；不写回流程参数。（Ctrl+Enter）'}>
            <Button onClick={() => void runPreview()} loading={previewBusy} disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || shapeSearchBlocked}>运行此节点</Button>
          </Tooltip>
          <Button onClick={onClose}>取消</Button><Button type="primary" onClick={apply} disabled={locked || !dirty || issues.length > 0 || externalChange} title="应用暂存参数到流程（Ctrl+S）">应用</Button>
        </div>
      </div>}>
      {externalChange && <Alert type="warning" showIcon title="流程中的节点参数已更新，请关闭后重新打开，避免覆盖其他修改。" />}
      <Tabs activeKey={activeTab} onChange={setActiveTab} items={[
        { key: 'workspace', label: '配置', forceRender: true, children: <>
          {isShapeMatch
            ? <Tabs className="shape-match-workspace-tabs" activeKey={shapeWorkspaceTab} onChange={changeShapeWorkspaceTab} items={[
              { key: 'training', label: '模型训练', disabled: locked, children: shapeWorkspaceTab === 'training' ? renderWorkspace('training') : null },
              { key: 'search', label: '搜索推理', disabled: locked, children: shapeWorkspaceTab === 'search' ? renderWorkspace('search') : null }
            ]} />
            : renderWorkspace()}
          <div ref={resultRef} tabIndex={-1} className="node-result-anchor">
            <NodeExecutionResult nodeId={node.id} catalog={catalogItem} result={displayedResult}
              receivedAt={receivedAt} stale={stale} draftResult={Boolean(preview)}
              expanded={resultExpanded} onExpandedChange={setResultExpanded} requestError={previewError} />
          </div>
        </> },
        { key: 'details', label: '详细说明', children: details }
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
