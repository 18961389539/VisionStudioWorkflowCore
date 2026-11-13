/**
 * NodeConfigurationDialog 的状态与派生模型（巨型组件拆分，2026-11-13）。
 *
 * 逐行搬运自原组件：草稿/预览/资产状态、match.shape 工作区、测量几何派生、显示图像来源。
 * 只做搬移，不改变 hook 调用顺序与语义；纯函数见 ./nodeConfigurationMeasurementNotices 与
 * ./nodeConfigurationUtils。
 */
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { RunResult, VisionOverlay, VisionRoi } from '../types';
import type { ScriptPreviewReport } from './ScriptEditor';
import { runImageEntryForPort, useRunImages } from '../useRunImages';
import {
  cloneParameters, configurationIssues, configurationParameters, cropRoiOutsideImageIssue,
  measurementRoiOutsideImageIssue, parameterFingerprint, type NodeParameters
} from '../nodeConfigurationState';
import { fetchShapeModelBase64 } from '../modelAssets';
import { imageFrameFingerprint, poseSourceFingerprint } from '../roiReference';
import { maturityLevel } from '../nodeMaturity';
import { useShapeMatchWorkspace } from './useShapeMatchWorkspace';
import {
  connectedCircleGeometry, connectedSearchLineGeometry,
  mapMeasurementGeometry, rigidPixelTransform
} from './nodeConfigurationGeometry';
import { classifyMeasurementNode, MEASUREMENT_NODE_TYPES, resolveMeasurementConnections } from './nodeConfigurationMeasurementConflicts';
import {
  buildMeasurementGeometry, fitGeometryToBounds,
  measurementGeometryParameters as buildGeometryParameters, parameterNumber
} from './nodeConfigurationMeasurementGeometryModel';
import { apiFetch } from '../apiClient';
import { diagnostics, runs } from '../endpoints';
import {
  connectedRoiGeometry, hasInputEdge, imageFrameDescendsFrom, imageFramePreservingNodeTypes,
  imageFramesSharePixelPreservingAncestor, imageSourceMatchesPort, inputEdge, inputSnapshotValue,
  legacyCropRectangle, measurementCropMaskSources, outputPortMatches
} from './nodeConfigurationUtils';
import { circleSamplingModel, measurementBoundsNotices } from './nodeConfigurationMeasurementNotices';
import type { MeasurementGeometry } from './ImageViewer';
import { useCaliperCircleDiagnostics } from '../caliperCircleDiagnostics';
import { caliperProfilePoint, useCaliperEdgeDiagnostics } from '../caliperEdgeDiagnostics';
import { fallbackCatalogRaw } from '../catalog';
import { parseNodeDuration, type NodeDurationHint } from './nodeConfigurationDialogModel';
import type { Props } from './NodeConfigurationDialog';

export function useNodeConfigurationDialogModel(props: Props) {
  const {
    node, catalogItem, catalogItems = fallbackCatalogRaw, nodes, edges, open, disabled, onPreview,
    previewBlockedReason, initialTab = 'parameters', result, resultReceivedAt, resultStale = false
  } = props;
  const cropMaskFramePreservingNodeTypes = useMemo(() => imageFramePreservingNodeTypes(catalogItems), [catalogItems]);
  // 成熟度只在标题栏画一次徽标：Stable 不画（目录里该字段缺省即 Stable）。
  const catalogMaturity = maturityLevel(catalogItem);
  // EmitsOverlay 是"可视化产物"能力位（viewer 叠加和/或 overlay 图像端口），不等于一定有端口；
  // 端口存在性只看 outputs，避免把 66 个只画 viewer 叠加的节点标成"缺端口"。
  const hasOverlayPort = catalogItem.outputs.some((port) => port.name === 'overlay');
  // result 是节点“详情”的定位入口，结果统一在配置页展示。
  const [activeTab, setActiveTab] = useState<string>(initialTab === 'details' ? 'details' : 'workspace');
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
  // __timeoutMs 的取值建议：打开配置页时读取该节点类型的 P95（/api/diagnostics/node-durations）。
  // 诊断属于附加信息：无权限/离线/格式异常都不得打断配置。
  const [nodeDuration, setNodeDuration] = useState<NodeDurationHint>();
  useEffect(() => {
    if (!open) { setNodeDuration(undefined); return; }
    setNodeDuration(undefined);
    const controller = new AbortController();
    let disposed = false;
    void (async () => {
      try {
        const response = await apiFetch(diagnostics.nodeDurations, { signal: controller.signal });
        if (!response.ok) return;
        const data: unknown = await response.json();
        if (disposed || !Array.isArray(data)) return;
        const entry = data.find((item) => typeof item === 'object' && item !== null
          && (item as Record<string, unknown>).nodeType === catalogItem.type);
        const hint = parseNodeDuration(entry);
        if (!disposed && hint) setNodeDuration(hint);
      } catch { /* 诊断提示是附加信息，失败不能打断配置 */ }
    })();
    return () => { disposed = true; controller.abort(); };
  }, [open, catalogItem.type]);
  const [baseline] = useState(() => configurationParameters((node.data.parameters ?? {}) as NodeParameters, catalogItem));
  // 资产引用节点打开时会把模型回读到草稿；基线同时补齐，避免"刚打开就显示未保存修改"。
  const [baselineOverride, setBaselineOverride] = useState<NodeParameters>();
  const effectiveBaseline = baselineOverride ?? baseline;
  const [draft, setDraft] = useState(() => cloneParameters(baseline));
  const [assetBusy, setAssetBusy] = useState(false);
  const [assetMessage, setAssetMessage] = useState<string>();
  const [cropClearUndo, setCropClearUndo] = useState<{ previous: NodeParameters; afterFingerprint: string }>();
  const [measurementFitUndo, setMeasurementFitUndo] = useState<{ previous: NodeParameters; afterFingerprint: string }>();
  const [previewNodeId, setPreviewNodeId] = useState(node.id);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [caliperDiagnosticMode, setCaliperDiagnosticMode] = useState(false);
  const [edgeInspection, setEdgeInspection] = useState<{ key: string; position: number }>();
  // match.shape 工作区专属状态（页签/训练样本/图像获取/模型训练）见 useShapeMatchWorkspace。
  const [regionEditMode, setRegionEditMode] = useState<'model' | 'search' | 'clutter'>('model');
  const [roiImageView, setRoiImageView] = useState<'reference' | 'input' | 'output'>('reference');
  const [measurementImageView, setMeasurementImageView] = useState<'reference' | 'current'>('reference');
  const [previewError, setPreviewError] = useState<string>();
  const [preview, setPreview] = useState<{ result: RunResult; fingerprint: string; receivedAt: string; preserveTrainingWorkspace?: boolean }>();
  const [searchPreview, setSearchPreview] = useState<{ result: RunResult; fingerprint: string; receivedAt: string }>();
  const [focusTick, setFocusTick] = useState(0);
  const draftFingerprint = parameterFingerprint(draft);
  useEffect(() => {
    if (cropClearUndo && cropClearUndo.afterFingerprint !== draftFingerprint) setCropClearUndo(undefined);
  }, [cropClearUndo, draftFingerprint]);
  useEffect(() => {
    if (measurementFitUndo && measurementFitUndo.afterFingerprint !== draftFingerprint) setMeasurementFitUndo(undefined);
  }, [measurementFitUndo, draftFingerprint]);
  const dirty = draftFingerprint !== parameterFingerprint(effectiveBaseline);
  // 与打开弹窗时的值不同的参数项数：用于参数区“已改 N 项”与关闭确认
  const changedCount = [...new Set([...Object.keys(baseline), ...Object.keys(draft)])]
    .filter((key) => parameterFingerprint(draft[key]) !== parameterFingerprint(baseline[key])).length;
  const [discardOpen, setDiscardOpen] = useState(false);
  const externalChange = parameterFingerprint(configurationParameters((node.data.parameters ?? {}) as NodeParameters, catalogItem)) !== parameterFingerprint(baseline);
  const locked = disabled || previewBusy;
  // 节点类型分流集中到 classifyMeasurementNode：此前 isShapeMatch/isImageCrop/... 七处散落定义，
  // 加一种节点类型要翻整个文件。
  const kind = classifyMeasurementNode(catalogItem.type);
  const { isShapeMatch, isImageCrop, isPerspective, isRoiTransform, isCaliperCircle, isCaliperEdge, isMetrology } = kind;
  const roiInputEdge = inputEdge(edges, node.id, 'roi');
  // 保持 string[] 而不是 readonly：它经 return 暴露给渲染层，下游按可变数组消费。
  const measurementNodeTypes: string[] = [...MEASUREMENT_NODE_TYPES];
  const measurementImageInputEdge = kind.isMeasurement
    ? inputEdge(edges, node.id, 'image')
    : undefined;
  const measurementImageSource = nodes.find((item) => item.id === measurementImageInputEdge?.source);
  const cropMaskSources = measurementCropMaskSources(nodes, edges, measurementImageSource, cropMaskFramePreservingNodeTypes);
  const directCropMaskSource = cropMaskSources.length === 1 && cropMaskSources[0].maskIsInCurrentFrame
    ? cropMaskSources[0].node : undefined;
  const croppedRoiConnected = Boolean(directCropMaskSource
    && outputPortMatches(roiInputEdge, directCropMaskSource.id, 'croppedRoi'));
  const croppedImageNeedsRoiWarning = Boolean(kind.isMeasurement
    && cropMaskSources.length > 0 && !croppedRoiConnected);
  const connectedInputPorts = catalogItem.inputs.filter((input) => hasInputEdge(edges, node.id, input.name)).map((input) => input.name);
  const connectedRoiInput = Boolean(roiInputEdge && connectedInputPorts.some((port) => port.toLowerCase() === 'roi'));
  const modelAssetId = typeof draft.modelAssetId === 'string' ? draft.modelAssetId.trim() : '';
  const modelAssetVersion = Number(draft.modelAssetVersion ?? 0);
  const hydratedAssetRef = useRef('');
  // P0 M3：资产引用节点打开时把模型回读到草稿，训练/搜索工作区照常可用；应用时再剥离内联模型。
  useEffect(() => {
    if (!open) { hydratedAssetRef.current = ''; return; }
    if (!isShapeMatch || !modelAssetId) return;
    if (typeof draft.trainedModelData === 'string' && draft.trainedModelData) return;
    const key = `${node.id}:${modelAssetId}@${modelAssetVersion > 0 ? modelAssetVersion : 'latest'}`;
    if (hydratedAssetRef.current === key) return;
    hydratedAssetRef.current = key;
    let cancelled = false;
    setAssetBusy(true);
    void fetchShapeModelBase64(modelAssetId, modelAssetVersion)
      .then((base64) => {
        if (cancelled) return;
        setDraft((previous) => (previous.trainedModelData ? previous : { ...previous, trainedModelData: base64 }));
        setBaselineOverride((previous) => ({ ...(previous ?? baseline), trainedModelData: base64 }));
        setAssetMessage(`已从资产 ${modelAssetId} 载入模型。`);
      })
      .catch((error: unknown) => {
        if (!cancelled) setAssetMessage(error instanceof Error ? `读取模型资产失败：${error.message}` : '读取模型资产失败。');
      })
      .finally(() => { if (!cancelled) setAssetBusy(false); });
    return () => { cancelled = true; };
  }, [open, isShapeMatch, modelAssetId, modelAssetVersion, draft.trainedModelData, node.id, baseline]);
  // match.shape 派生（模型数据/训练过期判定/样本计数等）见 useShapeMatchWorkspace。
  const templateSourceNodeId = edges.find((edge) => edge.target === node.id
    && (edge.targetHandle?.toLowerCase() === 'template' || edge.targetHandle?.toLowerCase().endsWith('template')))?.source;
  const trainingSourceKey = templateSourceNodeId ?? String(draft.templatePath ?? '');
  const roiInputConnected = isRoiTransform && Boolean(roiInputEdge);
  const roiMaskInputConnected = !isRoiTransform && !isImageCrop && Boolean(roiInputEdge);
  const cropRoiEdge = isImageCrop ? roiInputEdge : undefined;
  const cropRoiSourceId = cropRoiEdge?.source;
  const cropRoiInputConnected = Boolean(cropRoiEdge);
  const legacyCropBoundsPresent = isImageCrop && ['x', 'y', 'width', 'height'].some(key => draft[key] !== undefined && draft[key] !== null);
  const cropClearUndoAvailable = Boolean(isImageCrop && !cropRoiInputConnected && cropClearUndo
    && cropClearUndo.afterFingerprint === draftFingerprint);
  const measurementFitUndoAvailable = Boolean(measurementFitUndo && measurementFitUndo.afterFingerprint === draftFingerprint);
  // 测量连接与坐标系一致性：判定顺序、提示文案的选择优先级都在 nodeConfigurationMeasurementConflicts。
  // 这些判定此前只能靠整页渲染验证，提成纯函数后可单独覆盖「错配但不报错、结果静默偏移」这类情形。
  const {
    roiImageInputPort, roiImageInputEdge, roiSourceImageFrameNodeIds, roiImageInputSourceNode,
    connectedRoiImageFrameMismatch, connectedRoiImageFrameMismatchMessage,
    searchLineInputEdge, circleInputEdge, circleSourceNode, transformInputEdge, transformLineageNodes,
    connectedGeometryEdge, connectedGeometryImageFrameNodeIds, transformSourceImageFrames, transformMappedImageFrames,
    connectedGeometryAlreadyInMappedFrame, connectedGeometryTransformSourceFrameMismatch, connectedGeometryImageFrameMismatch,
    directCorrectedCircleTransformConflict, correctedCircleTransformConflict, correctedSearchLineTransformConflict,
    connectedCircleImageFrameMismatch, connectedSearchLineImageFrameMismatch,
    connectedCircleTransformSourceMismatch, connectedSearchLineTransformSourceMismatch,
    measurementGeometryTransformConflict, measurementGeometryFrameMismatchConflict, measurementGeometryConflict,
    measurementGeometryConflictMessage,
    correctedCircleTransformMessage, correctedSearchLineTransformMessage,
    circleImageFrameMismatchMessage, searchLineImageFrameMismatchMessage,
    circleTransformSourceMismatchMessage, searchLineTransformSourceMismatchMessage,
    referenceTransformNode, referenceTransformParameters, savedMeasurementReference,
    cropTransformNode, cropMeasurementReferenceEdge, cropMeasurementReferenceNodeId, cropMeasurementReferencePortName
  } = resolveMeasurementConnections({
    nodeId: node.id, nodes, edges, catalogItems, nodeType: catalogItem.type, kind,
    framePreservingNodeTypes: cropMaskFramePreservingNodeTypes, roiInputEdge, measurementImageInputEdge
  });
  const debugImageSourceId = isRoiTransform
    ? inputEdge(edges, node.id, 'debugImage')?.source
    : undefined;
  const debugImageConnected = isRoiTransform && hasInputEdge(edges, node.id, 'debugImage');
  const poseEdge = inputEdge(edges, node.id, 'currentPose');
  const poseSource = nodes.find(item => item.id === poseEdge?.source);
  const poseSourceParameters = (poseSource?.data.parameters ?? {}) as NodeParameters;
  const referenceBindings = parameterFingerprint({
    inputs: edges.filter(edge => edge.target === node.id && ['currentpose', 'debugimage'].includes((edge.targetHandle ?? '').toLowerCase()))
      .map(edge => ({ port: edge.targetHandle?.toLowerCase(), source: edge.source, sourcePort: edge.sourceHandle?.toLowerCase() }))
      .sort((a, b) => `${a.port}:${a.source}:${a.sourcePort}`.localeCompare(`${b.port}:${b.source}:${b.sourcePort}`)),
    imageFrame: imageFrameFingerprint(debugImageSourceId, nodes, edges),
    poseSource: poseSourceFingerprint(poseEdge?.source, nodes, edges),
    modelOrigin: ['trainedModelHash', 'trainedOriginX', 'trainedOriginY', 'trainedPoseOffsetX', 'trainedPoseOffsetY', 'trainingOriginMode', 'trainingOriginX', 'trainingOriginY']
      .map(key => poseSourceParameters[key] ?? null)
  });
  const referenceImage = isRoiTransform && typeof draft.referenceImage === 'string' ? draft.referenceImage : '';
  const referenceMismatch = Boolean(referenceImage && draft.referenceBindings !== referenceBindings);
  const displayedResult = preview?.result ?? result;
  const resultFailed = Boolean(displayedResult && (!displayedResult.success || displayedResult.nodeReports?.filter((report) => report.nodeId === node.id).slice(-1).some((report) => !report.success)));
  const [resultExpanded, setResultExpanded] = useState(() => initialTab === 'result' || resultFailed);
  useEffect(() => { if (initialTab === 'result') setResultExpanded(true); }, [initialTab]);
  // 新的失败报告自动展开；成功报告保留用户当前的展开状态。
  useEffect(() => { if (resultFailed) setResultExpanded(true); }, [displayedResult, resultFailed]);
  // P1（评审）：新的“运行此节点”结果落地后自动展开结果并滚入视口——此前值表格在首屏外
  //（被页脚裁切，需滚动整个弹窗体），参数少时右列还有大片空白。
  useEffect(() => {
    // script.csharp 的结果已经内联在编辑器下方；再滚到页面底部的通用结果区会把
    // 用户刚看的脚本和预览一起顶出视口。
    if (!preview || preview.preserveTrainingWorkspace || catalogItem.type === 'script.csharp') return;
    setResultExpanded(true);
    const frame = requestAnimationFrame(() => resultRef.current?.scrollIntoView?.({ block: 'nearest' }));
    return () => cancelAnimationFrame(frame);
  }, [catalogItem.type, preview]);
  const receivedAt = preview?.receivedAt ?? resultReceivedAt;
  const stale = Boolean(previewError) || (preview ? preview.fingerprint !== draftFingerprint : resultStale || dirty);
  const showCaliperDiagnostics = (isCaliperCircle || isCaliperEdge) && caliperDiagnosticMode;
  const caliperDiagnosticReport = displayedResult?.nodeReports.filter(report => report.nodeId === node.id).slice(-1)[0];
  const caliperDiagnostics = useCaliperCircleDiagnostics({ enabled: open && isCaliperCircle && showCaliperDiagnostics,
    runId: displayedResult?.runId, nodeId: node.id, report: caliperDiagnosticReport });
  const edgeDiagnostics = useCaliperEdgeDiagnostics({ enabled: open && isCaliperEdge && showCaliperDiagnostics,
    runId: displayedResult?.runId, nodeId: node.id, report: caliperDiagnosticReport });
  const edgeDiagnosticKey = edgeDiagnostics.data ? `${edgeDiagnostics.data.runId}:${node.id}:${edgeDiagnostics.data.executionSequence}` : undefined;
  const edgeInspectionPosition = edgeInspection?.key === edgeDiagnosticKey ? edgeInspection?.position : undefined;
  const inspectEdgePosition = (position?: number) => {
    if (position === undefined || !edgeDiagnosticKey) { setEdgeInspection(undefined); return; }
    setEdgeInspection(current => current?.key === edgeDiagnosticKey && current.position === position ? current : { key: edgeDiagnosticKey, position });
  };
  const freshInputResult = preview && !previewError && preview.fingerprint === draftFingerprint
    ? preview.result : !resultStale ? result : undefined;
  const matchReport = (searchPreview?.result ?? result)?.nodeReports.filter(report => report.nodeId === node.id).slice(-1)[0];
  const matchStale = Boolean(previewError) || (searchPreview ? searchPreview.fingerprint !== draftFingerprint : resultStale || dirty);
  const matchNumber = (port: string, summaryKey = port, digits = 2) => {
    const value = matchReport?.outputs?.[port]?.value ?? matchReport?.summary[summaryKey];
    return typeof value === 'number' && Number.isFinite(value) ? value.toFixed(digits) : '—';
  };
  const nodeLabels = Object.fromEntries(nodes.map((item) => [item.id, String(item.data.label ?? item.id)]));
  const { imageSource, setImageSource, imageViewerSource, viewerOverlays, selectedOutputAvailable, selectedInputAvailable,
    runImageCatalog, imageCatalogLoading, imageCatalogError, retryImageCatalog,
    imageOutputPorts, imageOutputPortName, setImageOutputPortName } =
    useRunImages({ result: displayedResult, selectedId: previewNodeId, nodes, edges, nodeLabels });
  const cropRoiForImage = isImageCrop && !cropRoiInputConnected
    ? (draft.roi as VisionRoi | null | undefined) ?? (legacyCropBoundsPresent
      ? legacyCropRectangle(draft, imageViewerSource.width ?? 0, imageViewerSource.height ?? 0) : undefined)
    : undefined;
  const issues = configurationIssues(catalogItem, draft, { connectedInputPorts });
  // script.csharp：把节点测试结果压缩成编辑器内联预览（输出/日志/预算/图像）。
  const scriptPreviewReport: ScriptPreviewReport | undefined = catalogItem.type !== 'script.csharp' ? undefined
    : previewError ? { success: false, error: previewError, durationMs: 0, summary: {}, receivedAt, stale: true, draft: true }
    : (() => {
      const report = displayedResult?.nodeReports.filter(item => item.nodeId === node.id).slice(-1)[0];
      if (!report) return undefined;
      const outputImage = typeof report.summary.outputImage === 'string' && report.summary.outputImage !== 'none'
        ? report.summary.outputImage : undefined;
      return {
        success: report.success,
        error: report.error ?? undefined,
        durationMs: report.durationMs,
        summary: report.summary,
        inputs: report.inputs,
        outputs: report.outputs,
        imageUrl: outputImage && displayedResult ? runs.nodeImage(displayedResult.runId, node.id, 'image') : undefined,
        receivedAt,
        stale,
        draft: Boolean(preview)
      };
    })();
  const scriptPreviewBlockedReason = catalogItem.type !== 'script.csharp' ? undefined
    : previewBlockedReason
      ?? (issues.length > 0 ? '请先修正参数问题' : undefined)
      ?? (externalChange ? '流程中的节点参数已更新，请关闭后重新打开' : undefined);
  // match.shape 工作区（按节点类型分块）：训练/搜索页签状态、模型训练与图像获取处理器。
  const {
    shapeWorkspaceTab, setShapeWorkspaceTab, changeShapeWorkspaceTab,
    shapeImageKind, setShapeImageKind,
    trainingView, setTrainingView,
    showSavedTrainingImage, setShowSavedTrainingImage,
    trainingSamples, setTrainingSamples,
    // oxlint-disable-next-line no-unused-vars -- 待接线：界面未渲染该预览/样本数据，保留数据流待接回
    searchWorkspacePreview, setSearchWorkspacePreview,
    trainingBusy, trainingImageBusy, searchImageBusy,
    trainedModelData, trainedSourceImage, trainedPreviewData, legacyWorkflow, trainedSettings,
    trainingStale, shapeSearchBlocked, capturedTrainingImage, hasLosslessTrainingImage,
    // oxlint-disable-next-line no-unused-vars -- 待接线：界面未渲染该预览/样本数据，保留数据流待接回
    trainingSampleData, effectiveTrainingSampleCount, changedTrainingLabels, showTrainedTemplatePreview,
    fetchTrainingImage, addCurrentTrainingSample, fetchSearchImage, trainShapeModel, locateSavedTrainingImage
  } = useShapeMatchWorkspace({
    node, result, isShapeMatch, locked, issues, externalChange, previewBlockedReason,
    draft, setDraft, draftFingerprint, onPreview, catalogItem,
    templateSourceNodeId, trainingSourceKey,
    setPreview, setPreviewBusy, setPreviewError, setResultExpanded,
    setPreviewNodeId, setImageSource, setRegionEditMode, setFocusTick
  });
  const measurementInputImageEntry = runImageCatalog && freshInputResult && runImageCatalog.runId === freshInputResult.runId && measurementImageSource
    ? runImageEntryForPort(runImageCatalog, measurementImageSource.id, measurementImageInputEdge?.sourceHandle)
    : undefined;
  const measurementCurrentImageAvailable = Boolean(measurementInputImageEntry);
  const connectedRoiInputSnapshot = connectedRoiInput
    ? inputSnapshotValue(freshInputResult?.nodeReports.filter((report) => report.nodeId === node.id).slice(-1)[0]?.inputs, 'roi')
    : undefined;
  const connectedRoiPreviewGeometry = connectedRoiGeometry(connectedRoiInputSnapshot);
  const showConnectedRoiPreview = Boolean((isImageCrop || measurementNodeTypes.includes(catalogItem.type))
    && connectedRoiPreviewGeometry && roiSourceImageFrameNodeIds.length > 0 && !connectedRoiImageFrameMismatch
    && imageSource === 'input' && previewNodeId === node.id && selectedInputAvailable);
  if (isImageCrop && !cropRoiInputConnected && !resultStale && imageSource === 'input'
    && previewNodeId === node.id && selectedInputAvailable) {
    const imageExtentIssue = cropRoiOutsideImageIssue(cropRoiForImage, imageViewerSource.width ?? 0, imageViewerSource.height ?? 0);
    if (imageExtentIssue) issues.push(imageExtentIssue);
  }
  if (measurementNodeTypes.includes(catalogItem.type) && !connectedRoiInput && !resultStale && imageSource === 'input'
    && previewNodeId === node.id && selectedInputAvailable) {
    const imageExtentIssue = measurementRoiOutsideImageIssue(draft.roi, imageViewerSource.width ?? 0, imageViewerSource.height ?? 0);
    if (imageExtentIssue) issues.push(imageExtentIssue);
  }
  const measurementGeometryType = isCaliperCircle ? 'circle'
    : ['measure.caliper', 'measure.caliperWidth', 'measure.caliperLine', 'measure.caliperArray', 'feature.edgeSubpixel'].includes(catalogItem.type) ? 'line' : undefined;
  const measurementGeometryFrameKind = !transformInputEdge ? 'current'
    : savedMeasurementReference ? 'saved-reference'
      : cropMeasurementReferenceNodeId ? 'transform-input' : undefined;
  const circleInputSnapshot = circleInputEdge
    ? inputSnapshotValue(freshInputResult?.nodeReports.filter((report) => report.nodeId === node.id).slice(-1)[0]?.inputs, 'circle')
    : undefined;
  const connectedCircle = circleInputEdge ? connectedCircleGeometry(circleInputSnapshot) : undefined;
  const circleSourceReport = circleInputEdge
    ? freshInputResult?.nodeReports.filter((report) => report.nodeId === circleInputEdge.source).slice(-1)[0]
    : undefined;
  const circleSourceParameters = (circleSourceNode?.data.parameters ?? {}) as NodeParameters;
  const circleSourceRoiType = circleSourceReport?.summary.roiType
    ?? (circleSourceParameters.roi as VisionRoi | undefined)?.type;
  const circleInputIsBoundingApproximation = isCaliperCircle && circleSourceNode?.data.typeKey === 'roi.transform'
    && typeof circleSourceRoiType === 'string' && circleSourceRoiType !== 'Circle';
  const searchLineInputSnapshot = searchLineInputEdge
    ? inputSnapshotValue(freshInputResult?.nodeReports.filter((report) => report.nodeId === node.id).slice(-1)[0]?.inputs, 'searchLine')
    : undefined;
  const connectedSearchLine = searchLineInputEdge ? connectedSearchLineGeometry(searchLineInputSnapshot) : undefined;
  const canDisplayConnectedSearchLine = Boolean(measurementGeometryType === 'line' && connectedSearchLine && measurementGeometryFrameKind);
  const canEditMeasurementGeometry = Boolean(measurementGeometryType && !searchLineInputEdge
    && (!circleInputEdge || connectedCircle && !correctedCircleTransformConflict) && measurementGeometryFrameKind);
  useEffect(() => {
    if (!open || !canEditMeasurementGeometry && !canDisplayConnectedSearchLine) return;
    if (measurementGeometryFrameKind === 'saved-reference' || measurementGeometryFrameKind === 'current') {
      setPreviewNodeId(node.id);
      setImageSource(measurementGeometryFrameKind === 'current' && !selectedInputAvailable && selectedOutputAvailable ? 'output' : 'input');
    } else if (cropMeasurementReferenceNodeId) {
      const currentImage = measurementImageView === 'current';
      setPreviewNodeId(currentImage ? node.id : cropMeasurementReferenceNodeId);
      setImageSource(currentImage ? 'input' : 'output');
      if (!currentImage) setImageOutputPortName(cropMeasurementReferencePortName);
    }
  }, [canEditMeasurementGeometry, canDisplayConnectedSearchLine, cropMeasurementReferenceNodeId, cropMeasurementReferencePortName,
    displayedResult?.runId, measurementGeometryFrameKind, node.id, open, measurementCurrentImageAvailable, measurementImageView,
    selectedInputAvailable, selectedOutputAvailable, setImageSource, setImageOutputPortName]);
  useEffect(() => {
    if (open) setMeasurementImageView('reference');
  }, [node.id, open]);
  const measurementInputImageDisplayed = previewNodeId === node.id && imageSource === 'input'
    && imageSourceMatchesPort(imageViewerSource, measurementImageInputEdge?.source, measurementImageInputEdge?.sourceHandle);
  const measurementImageFrameReady = measurementGeometryFrameKind === 'saved-reference'
    ? previewNodeId === node.id && Boolean(savedMeasurementReference && savedMeasurementReference.width > 0 && savedMeasurementReference.height > 0)
    : measurementGeometryFrameKind === 'transform-input'
      ? measurementImageView === 'current'
        ? measurementInputImageDisplayed && measurementCurrentImageAvailable
        : previewNodeId === cropMeasurementReferenceNodeId && imageSource === 'output'
          && imageSourceMatchesPort(imageViewerSource, cropMeasurementReferenceNodeId, cropMeasurementReferencePortName)
      : measurementInputImageDisplayed || previewNodeId === node.id && imageSource === 'output'
        && imageSourceMatchesPort(imageViewerSource, node.id, 'image');
  const measurementImageFrameUnavailable = Boolean((canEditMeasurementGeometry || canDisplayConnectedSearchLine)
    && !measurementImageFrameReady && !imageCatalogLoading && imageViewerSource.url);
  const geometryNumber = (key: string, fallback: number) => parameterNumber(draft, key, fallback);
  // 几何构建/回写/适配三件事集中在 nodeConfigurationMeasurementGeometryModel：读写字段对称性
  // 在那里一眼可查，此处只做编排。
  const measurementGeometry = buildMeasurementGeometry({
    draft,
    catalogType: catalogItem.type,
    measurementType: measurementGeometryType,
    editable: canEditMeasurementGeometry,
    displayableConnectedSearchLine: canDisplayConnectedSearchLine,
    imageFrameReady: measurementImageFrameReady,
    connectedSearchLine,
    connectedCircle,
    circleInputConnected: Boolean(circleInputEdge)
  });
  const measurementInputReport = transformInputEdge
    ? freshInputResult?.nodeReports.filter((report) => report.nodeId === node.id).slice(-1)[0]
    : undefined;
  const measurementTransformValue = transformInputEdge
    ? inputSnapshotValue(measurementInputReport?.inputs, 'transform')
    : undefined;
  const measurementTransform = transformInputEdge
    ? rigidPixelTransform(measurementTransformValue)
    : undefined;
  const measurementTransformInvalid = Boolean(transformInputEdge && measurementInputReport
    && measurementTransformValue !== undefined && !measurementTransform);
  const potentiallyIncompatibleTransformSource = transformLineageNodes.find((source) => {
    const type = String(source.data.typeKey ?? '').toLowerCase();
    const parameters = (source.data.parameters ?? {}) as NodeParameters;
    if (type === 'calibration.planar') return true;
    if (type === 'coordinate.transformconstant') return String(parameters.unit ?? 'mm') !== 'px';
    if (type === 'calibration.handeye2d')
      return String(parameters.sourceUnit ?? 'mm') !== 'px' || String(parameters.unit ?? 'mm') !== 'px';
    return false;
  });
  const measurementTransformSourceNotice = transformInputEdge && !measurementTransform && !measurementTransformInvalid
    && potentiallyIncompatibleTransformSource
    ? String(potentiallyIncompatibleTransformSource.data.typeKey ?? '').toLowerCase() === 'calibration.planar'
      ? `“${String(potentiallyIncompatibleTransformSource.data.label ?? potentiallyIncompatibleTransformSource.id)}”输出像素到物理坐标的平面标定矩阵，通常包含单位换算或透视；卡尺只接受 px→px 刚性变换。请在像素图像上直接测量，或改用纯平移/旋转的像素变换。`
      : `“${String(potentiallyIncompatibleTransformSource.data.label ?? potentiallyIncompatibleTransformSource.id)}”当前配置的单位不是 px；卡尺只接受 px→px 刚性变换。请将变换设为像素单位，或让测量几何直接使用当前图像坐标。`
    : undefined;
  const mappedMeasurementGeometry = measurementGeometry && (transformInputEdge
    ? measurementTransform ? mapMeasurementGeometry(measurementGeometry, measurementTransform) : undefined
    : measurementGeometry);
  const showCurrentMeasurementImage = (measurementGeometryFrameKind === 'saved-reference' || measurementGeometryFrameKind === 'transform-input')
    && measurementImageView === 'current';
  const viewerMeasurementGeometry = showCurrentMeasurementImage ? mappedMeasurementGeometry : measurementGeometry;
  const currentMeasurementGeometryUnavailable = showCurrentMeasurementImage && Boolean(measurementGeometry)
    && !mappedMeasurementGeometry && !measurementTransformInvalid;
  const measurementBoundsWidth = transformInputEdge ? measurementInputImageEntry?.width : imageViewerSource.width;
  const measurementBoundsHeight = transformInputEdge ? measurementInputImageEntry?.height : imageViewerSource.height;
  const transformedBoundsPreflightUnavailable = Boolean(transformInputEdge && measurementGeometry && measurementImageFrameReady
    && !measurementTransformInvalid
    && (!mappedMeasurementGeometry || !measurementInputImageEntry || measurementInputImageEntry.width <= 0 || measurementInputImageEntry.height <= 0));
  const measurementBoundsContext = transformInputEdge
    ? '已按本次有效运行的刚性 transform 映射到测量输入图像后估算；'
    : '';
  const {
    samplingNotice: circleSamplingNotice, fitConditionNotice: circleFitConditionNotice
  } = circleSamplingModel(draft, isCaliperCircle, geometryNumber);
  const {
    circleBoundsNotice: caliperCircleBoundsNotice, lineBoundsNotice: caliperLineBoundsNotice,
    searchLineBoundsNotice: caliperSearchLineBoundsNotice
  } = measurementBoundsNotices({
    catalogType: catalogItem.type, geometry: mappedMeasurementGeometry, imageFrameReady: measurementImageFrameReady,
    width: measurementBoundsWidth, height: measurementBoundsHeight, boundsContext: measurementBoundsContext, geometryNumber });
  // 回写：几何 → 参数。与 buildMeasurementGeometry 互为逆过程，实现在同一模块里对照。
  const measurementGeometryParameters = (current: NodeParameters, geometry: MeasurementGeometry,
    options?: { preserveValues?: boolean }): NodeParameters =>
    buildGeometryParameters(current, geometry, {
      catalogType: catalogItem.type,
      circleInputConnected: Boolean(circleInputEdge),
      preserveValues: options?.preserveValues
    });
  const updateMeasurementGeometry = (geometry: MeasurementGeometry, options?: { preserveValues?: boolean }) => {
    if (locked || !canEditMeasurementGeometry || !measurementImageFrameReady || showCurrentMeasurementImage) return;
    setDraft(current => measurementGeometryParameters(current, geometry, options));
  };
  const fitMeasurementGeometryToImage = () => {
    if (locked || !measurementGeometry || !measurementImageFrameReady || showCurrentMeasurementImage) return;
    const width = measurementGeometryFrameKind === 'saved-reference' ? savedMeasurementReference?.width : imageViewerSource.width;
    const height = measurementGeometryFrameKind === 'saved-reference' ? savedMeasurementReference?.height : imageViewerSource.height;
    if (!width || !height || !Number.isFinite(width) || !Number.isFinite(height)) return;
    const next = measurementGeometryParameters(draft, fitGeometryToBounds(measurementGeometry, width, height));
    if (parameterFingerprint(next) === draftFingerprint) return;
    setMeasurementFitUndo({ previous: cloneParameters(draft), afterFingerprint: parameterFingerprint(next) });
    setDraft(next);
  };
  const undoMeasurementGeometryFit = () => {
    if (!measurementFitUndoAvailable || !measurementFitUndo) return;
    setDraft(cloneParameters(measurementFitUndo.previous));
    setMeasurementFitUndo(undefined);
  };
  const imageInputSourceId = inputEdge(edges, node.id, 'image')?.source;
  const roiSourceNode = roiInputEdge ? nodes.find((item) => item.id === roiInputEdge.source) : undefined;
  const roiSourceDebugImageId = roiInputEdge
    ? inputEdge(edges, roiInputEdge.source, 'debugImage')?.source
    : undefined;
  // RoiTransform overlays are in the debugImage coordinate frame. Only copy them onto an input
  // image when that exact frame is connected, so a different preprocessed image is never misled.
  const roiOverlayFrameMatchesInput = roiSourceNode?.data.typeKey === 'roi.transform'
    && Boolean(imageInputSourceId && (imageFrameDescendsFrom(imageInputSourceId, roiInputEdge?.source, nodes, edges, cropMaskFramePreservingNodeTypes)
      || imageFrameDescendsFrom(imageInputSourceId, roiSourceDebugImageId, nodes, edges, cropMaskFramePreservingNodeTypes)
      || imageFramesSharePixelPreservingAncestor(imageInputSourceId, roiSourceDebugImageId, nodes, edges, cropMaskFramePreservingNodeTypes)));
  const connectedRoiInputOverlays = freshInputResult && roiInputEdge && imageSource === 'input'
    && previewNodeId === node.id && selectedInputAvailable && roiOverlayFrameMatchesInput
    ? (freshInputResult.overlays ?? []).filter((overlay) => overlay.nodeId === roiInputEdge.source)
    : [];
  // Measurement nodes pass the input image through unchanged, so their own result overlays
  // remain aligned when users switch to the input image to edit measurement geometry.
  const sameFrameMeasurementOverlays = !stale && measurementGeometryType && measurementInputImageDisplayed
    ? (displayedResult?.overlays ?? []).filter((overlay) => overlay.nodeId === node.id)
    : [];
  const resultOverlaysForViewer = stale || measurementGeometryType && imageSource === 'output'
    && !imageSourceMatchesPort(imageViewerSource, node.id, 'image')
    ? viewerOverlays.filter((overlay) => overlay.nodeId !== node.id)
    : viewerOverlays;
  useEffect(() => {
    if (open && isRoiTransform && roiImageView !== 'reference') setImageSource(roiImageView);
  }, [isRoiTransform, open, roiImageView, imageSource, setImageSource]);
  useEffect(() => {
    if (!open || !(isImageCrop || isPerspective)) return;
    setPreviewNodeId(node.id);
    setImageSource('input');
  }, [isImageCrop, isPerspective, node.id, open, setImageSource]);
  const trainingContours = Array.isArray(draft.trainedContours) ? draft.trainedContours as { x: number; y: number }[][]
    : Array.isArray(draft.trainedContour) ? [draft.trainedContour as { x: number; y: number }[]] : [];
  const showCapturedTrainingImage = isShapeMatch && shapeWorkspaceTab === 'training' && !showSavedTrainingImage && capturedTrainingImage;
  const shapeImageMatchesTab = shapeImageKind === shapeWorkspaceTab || (shapeWorkspaceTab === 'training' && (showTrainedTemplatePreview || Boolean(showCapturedTrainingImage)));
  const showMeasurementReferenceImage = Boolean((canEditMeasurementGeometry || canDisplayConnectedSearchLine)
    && measurementGeometryFrameKind === 'saved-reference' && measurementImageFrameReady && measurementImageView === 'reference');
  const diagnosticSnapshot = isCaliperEdge ? edgeDiagnostics.data : caliperDiagnostics.manifest;
  const diagnosticImageEntry = showCaliperDiagnostics && diagnosticSnapshot
    ? runImageEntryForPort(runImageCatalog, node.id, 'image') : undefined;
  const diagnosticImageMatches = Boolean(diagnosticImageEntry && diagnosticSnapshot
    && diagnosticImageEntry.width === diagnosticSnapshot.imageWidth && diagnosticImageEntry.height === diagnosticSnapshot.imageHeight);
  const edgeDiagnosticGeometry: MeasurementGeometry | undefined = showCaliperDiagnostics && isCaliperEdge && diagnosticImageMatches && edgeDiagnostics.data
    ? { type: 'line', start: edgeDiagnostics.data.start, end: edgeDiagnostics.data.end,
      profileWidth: edgeDiagnostics.data.sampleWidth, direction: edgeDiagnostics.data.direction } : undefined;
  const diagnosticOverlays = showCaliperDiagnostics && diagnosticImageMatches
    ? (displayedResult?.overlays ?? []).filter(overlay => overlay.nodeId === node.id
      && (!isCaliperEdge || !['caliper-edge', 'caliper-search', 'edge-search', 'edge-subpixel'].includes(overlay.id))) : [];
  const diagnosticMarkers: VisionOverlay[] = [];
  if (isCaliperEdge && showCaliperDiagnostics && diagnosticImageMatches && edgeDiagnostics.data) {
    const data = edgeDiagnostics.data;
    // The profile uses the same clipped and transformed frame as the captured node image.
    for (const [index, candidate] of data.candidates.entries()) {
      if (index === data.selectedCandidateIndex) continue;
      const point = caliperProfilePoint(data, candidate.position);
      if (point) diagnosticMarkers.push({ id: `caliper-candidate-${index}`, type: 'Point', nodeId: node.id,
        x: point.x, y: point.y, stroke: '#a78bfa', strokeWidth: 1.5 });
    }
    const inspected = edgeInspectionPosition === undefined ? undefined : caliperProfilePoint(data, edgeInspectionPosition);
    if (inspected) diagnosticMarkers.push({ id: 'caliper-profile-cursor', type: 'Point', nodeId: node.id,
      x: inspected.x, y: inspected.y, stroke: '#ffd666', strokeWidth: 3 });
    if (data.edgePoint) diagnosticMarkers.push({ id: 'caliper-diagnostic-chosen', type: 'Point', nodeId: node.id,
      x: data.edgePoint.x, y: data.edgePoint.y, stroke: '#52c41a', strokeWidth: 2.5 });
  }
  const displayImageSource = showCaliperDiagnostics
    ? { url: diagnosticImageMatches && displayedResult
      ? runs.nodeImage(displayedResult.runId, node.id, 'image') : '',
      width: diagnosticImageEntry?.width, height: diagnosticImageEntry?.height,
      label: `${isCaliperEdge ? '边缘剖面诊断' : '单卡尺诊断'} · 本次实际测量图像（运行快照）` }
    : isRoiTransform && roiImageView === 'reference'
    ? { url: referenceImage ? `data:image/png;base64,${referenceImage}` : '', width: Number(draft.referenceWidth ?? 0),
      height: Number(draft.referenceHeight ?? 0), label: referenceImage ? '已保存基准图像' : '尚未建立基准' }
    : showMeasurementReferenceImage && savedMeasurementReference
      ? { url: `data:image/png;base64,${savedMeasurementReference.data}`, width: savedMeasurementReference.width,
        height: savedMeasurementReference.height, label: '已保存基准图像 · 测量几何坐标' }
    : showCapturedTrainingImage ? { url: `data:image/png;base64,${capturedTrainingImage.data}`, width: capturedTrainingImage.width,
      height: capturedTrainingImage.height, label: '已锁定训练图像 · 训练使用当前显示的图像' }
    : showTrainedTemplatePreview ? { ...imageViewerSource,
    url: `data:${trainedSourceImage ? 'image/png' : 'image/jpeg'};base64,${trainedSourceImage || trainedPreviewData}`, width: Number(draft.trainedSourceWidth ?? imageViewerSource.width),
    height: Number(draft.trainedSourceHeight ?? imageViewerSource.height), label: trainedSourceImage ? '已保存无损训练图像' : '旧模型压缩预览 · 重新训练前请重新获取原图' }
    : isShapeMatch && !shapeImageMatchesTab ? { ...imageViewerSource, url: '', label: shapeWorkspaceTab === 'training' ? '训练图像' : '搜索场景图像' }
      : imageViewerSource;
  const overlaysWithTrainedContour: VisionOverlay[] = trainingView && isShapeMatch && trainedModelData &&
    (showTrainedTemplatePreview || capturedTrainingImage?.trained)
    ? [...trainingContours.map((points, index) => ({ id: `saved-shape-training-contour-${index}`, type: 'Polyline' as const, points, label: '已保存模型轮廓片段', stroke: '#13c2c2', strokeWidth: 2 })),
      { id: 'saved-shape-training-origin', type: 'Point', x: Number(draft.trainedOriginImageX ?? 0), y: Number(draft.trainedOriginImageY ?? 0), label: '训练原点', stroke: '#fa541c', strokeWidth: 3 }]
    : (isRoiTransform && roiImageView === 'reference') || showCapturedTrainingImage || showMeasurementReferenceImage ? []
      : [...new Map([...resultOverlaysForViewer, ...connectedRoiInputOverlays, ...sameFrameMeasurementOverlays]
        .map(overlay => [`${overlay.nodeId ?? ''}:${overlay.id}`, overlay])).values()];
  const imagePorts = [...catalogItem.inputs, ...catalogItem.outputs].some((p) => p.dataType === 'Image' || p.dataType === 'DomainImage');
  const showImage = imagePorts || initialTab === 'image';
  const supportsRoi = catalogItem.capabilities?.supportsRoi === true;
  const canEditRoi = supportsRoi && (isRoiTransform
    ? !roiInputConnected && !referenceMismatch && roiImageView === 'reference' && Boolean(referenceImage)
    : isShapeMatch
      ? regionEditMode !== 'search'
        ? shapeWorkspaceTab === 'training' && shapeImageMatchesTab && (Boolean(showCapturedTrainingImage) || showTrainedTemplatePreview || imageSource === 'output' && (previewNodeId === templateSourceNodeId || previewNodeId === node.id))
        : shapeWorkspaceTab === 'search' && shapeImageMatchesTab && imageSource === 'output' && previewNodeId === node.id && Boolean(displayImageSource.url)
      : isImageCrop
        ? !cropRoiInputConnected && previewNodeId === node.id && imageSource === 'input' && selectedInputAvailable
      : previewNodeId === node.id && imageSource === 'input' && selectedInputAvailable && !roiMaskInputConnected && !showMeasurementReferenceImage);
  const canLocateRoiInImage = showImage && supportsRoi && !cropRoiInputConnected && !roiMaskInputConnected
    && !roiInputConnected && !referenceMismatch && !showMeasurementReferenceImage
    && (isShapeMatch || (isRoiTransform ? Boolean(referenceImage) : selectedInputAvailable));
  return {
    node, catalogItem, catalogItems, nodes, edges, open, disabled, onPreview, previewBlockedReason,
    initialTab, result, resultReceivedAt, resultStale, circleSamplingNotice, circleFitConditionNotice,
    caliperCircleBoundsNotice, caliperLineBoundsNotice, caliperSearchLineBoundsNotice,
    baseline, imageSource, setImageSource, imageViewerSource, viewerOverlays, selectedOutputAvailable,
    selectedInputAvailable, runImageCatalog, imageCatalogLoading, imageCatalogError, retryImageCatalog,
    imageOutputPorts, imageOutputPortName, setImageOutputPortName, shapeWorkspaceTab, setShapeWorkspaceTab,
    changeShapeWorkspaceTab, shapeImageKind, setShapeImageKind, trainingView, setTrainingView,
    showSavedTrainingImage, setShowSavedTrainingImage, trainingSamples, setTrainingSamples,
    searchWorkspacePreview, setSearchWorkspacePreview, trainingBusy, trainingImageBusy, searchImageBusy,
    trainedModelData, trainedSourceImage, trainedPreviewData, legacyWorkflow, trainedSettings, trainingStale,
    shapeSearchBlocked, capturedTrainingImage, hasLosslessTrainingImage, trainingSampleData,
    effectiveTrainingSampleCount, changedTrainingLabels, showTrainedTemplatePreview, fetchTrainingImage,
    addCurrentTrainingSample, fetchSearchImage, trainShapeModel, locateSavedTrainingImage,
    cropMaskFramePreservingNodeTypes, catalogMaturity, hasOverlayPort, activeTab, setActiveTab, maximized,
    setMaximized, imageShare, setImageShare, workspaceWidth, setWorkspaceWidth, workspaceRef,
    columns, previewShare, commitShare, bindWorkspace, resultRef, focusResult,
    nodeDuration, setNodeDuration, baselineOverride, setBaselineOverride, effectiveBaseline, draft,
    setDraft, assetBusy, setAssetBusy, assetMessage, setAssetMessage, cropClearUndo,
    setCropClearUndo, measurementFitUndo, setMeasurementFitUndo, previewNodeId, setPreviewNodeId, previewBusy,
    setPreviewBusy, caliperDiagnosticMode, setCaliperDiagnosticMode, edgeInspection, setEdgeInspection, regionEditMode,
    setRegionEditMode, roiImageView, setRoiImageView, measurementImageView, setMeasurementImageView, previewError,
    setPreviewError, preview, setPreview, searchPreview, setSearchPreview, focusTick,
    setFocusTick, draftFingerprint, dirty, changedCount, discardOpen, setDiscardOpen,
    externalChange, locked, roiInputEdge, measurementNodeTypes, measurementImageInputEdge, measurementImageSource,
    cropMaskSources, directCropMaskSource, croppedRoiConnected, croppedImageNeedsRoiWarning, connectedInputPorts, connectedRoiInput,
    isShapeMatch, modelAssetId, modelAssetVersion, hydratedAssetRef, isImageCrop, isPerspective,
    isRoiTransform, isCaliperCircle, isCaliperEdge, isMetrology, roiImageInputPort, roiImageInputEdge,
    roiSourceImageFrameNodeIds, roiImageInputSourceNode, connectedRoiImageFrameMismatch, connectedRoiImageFrameMismatchMessage, templateSourceNodeId, trainingSourceKey,
    roiInputConnected, roiMaskInputConnected, cropRoiEdge, cropRoiSourceId, cropRoiInputConnected, legacyCropBoundsPresent,
    cropClearUndoAvailable, measurementFitUndoAvailable, searchLineInputEdge, circleInputEdge, circleSourceNode, transformInputEdge,
    transformLineageNodes, connectedGeometryEdge, connectedGeometryImageFrameNodeIds, transformSourceImageFrames, transformMappedImageFrames, connectedGeometryAlreadyInMappedFrame,
    connectedGeometryTransformSourceFrameMismatch, connectedGeometryImageFrameMismatch, directCorrectedCircleTransformConflict, correctedCircleTransformConflict, correctedSearchLineTransformConflict, connectedCircleImageFrameMismatch,
    connectedSearchLineImageFrameMismatch, connectedCircleTransformSourceMismatch, connectedSearchLineTransformSourceMismatch, measurementGeometryTransformConflict, measurementGeometryFrameMismatchConflict, measurementGeometryConflict,
    correctedCircleTransformMessage, correctedSearchLineTransformMessage, circleImageFrameMismatchMessage, searchLineImageFrameMismatchMessage, circleTransformSourceMismatchMessage, searchLineTransformSourceMismatchMessage,
    measurementGeometryConflictMessage, referenceTransformNode, referenceTransformParameters, savedMeasurementReference, cropTransformNode, cropMeasurementReferenceEdge,
    cropMeasurementReferenceNodeId, cropMeasurementReferencePortName, debugImageSourceId, debugImageConnected, poseEdge, poseSource,
    poseSourceParameters, referenceBindings, referenceImage, referenceMismatch, displayedResult, resultFailed,
    resultExpanded, setResultExpanded, receivedAt, stale, showCaliperDiagnostics, caliperDiagnosticReport,
    caliperDiagnostics, edgeDiagnostics, edgeDiagnosticKey, edgeInspectionPosition, inspectEdgePosition, freshInputResult,
    matchReport, matchStale, matchNumber, nodeLabels, cropRoiForImage, issues,
    scriptPreviewReport, scriptPreviewBlockedReason, measurementInputImageEntry, measurementCurrentImageAvailable, connectedRoiInputSnapshot, connectedRoiPreviewGeometry,
    showConnectedRoiPreview, measurementGeometryType, measurementGeometryFrameKind, circleInputSnapshot, connectedCircle, circleSourceReport,
    circleSourceParameters, circleSourceRoiType, circleInputIsBoundingApproximation, searchLineInputSnapshot, connectedSearchLine, canDisplayConnectedSearchLine,
    canEditMeasurementGeometry, measurementInputImageDisplayed, measurementImageFrameReady, measurementImageFrameUnavailable, geometryNumber, measurementGeometry,
    measurementInputReport, measurementTransformValue, measurementTransform, measurementTransformInvalid, potentiallyIncompatibleTransformSource, measurementTransformSourceNotice,
    mappedMeasurementGeometry, showCurrentMeasurementImage, viewerMeasurementGeometry, currentMeasurementGeometryUnavailable, measurementBoundsWidth, measurementBoundsHeight,
    transformedBoundsPreflightUnavailable, measurementBoundsContext, measurementGeometryParameters, updateMeasurementGeometry, fitMeasurementGeometryToImage, undoMeasurementGeometryFit,
    imageInputSourceId, roiSourceNode, roiSourceDebugImageId, roiOverlayFrameMatchesInput, connectedRoiInputOverlays, sameFrameMeasurementOverlays,
    resultOverlaysForViewer, trainingContours, showCapturedTrainingImage, shapeImageMatchesTab, showMeasurementReferenceImage, diagnosticSnapshot,
    diagnosticImageEntry, diagnosticImageMatches, edgeDiagnosticGeometry, diagnosticOverlays, diagnosticMarkers, displayImageSource,
    overlaysWithTrainedContour, imagePorts, showImage, supportsRoi, canEditRoi, canLocateRoiInImage,
  };
}
