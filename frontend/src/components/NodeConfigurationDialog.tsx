import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import type { Edge, Node } from '@xyflow/react';
import { Alert, Button, Collapse, Modal, Select, Segmented, Space, Tabs, Tag, Tooltip } from 'antd';
import type { NodeCatalogItem, ParameterDescriptor, PortDescriptor, RunResult, VisionOverlay, VisionRoi } from '../types';
import NodeCategoryIcon from './NodeCategoryIcon';
import PropertyPanel from './PropertyPanel';
import ImageViewer, { type MeasurementGeometry } from './ImageViewer';
import NodeExecutionResult from './NodeExecutionResult';
import SplitHandle from './SplitHandle';
import { useRunImages } from '../useRunImages';
import { cloneParameters, configurationIssues, configurationParameters, parameterFingerprint, withParameter, type NodeParameters } from '../nodeConfigurationState';
import { capturedReference, imageFrameFingerprint } from '../roiReference';

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
    return parameters.modelMode === 'legacy' || typeof parameters.trainedModelData === 'string' && parameters.trainedModelData ? 'search' : 'training';
  });
  const existingSearchReport = result?.nodeReports.filter(report => report.nodeId === node.id).slice(-1)[0];
  const hasExistingSearch = Boolean(existingSearchReport && !existingSearchReport.summary.trainingMode
    && !existingSearchReport.summary.trainingSucceeded && !existingSearchReport.summary.trainingPreviewReady && !existingSearchReport.summary.searchPreviewReady);
  const [shapeImageKind, setShapeImageKind] = useState<'training' | 'search' | undefined>(hasExistingSearch ? 'search' : undefined);
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
  const [regionEditMode, setRegionEditMode] = useState<'model' | 'search' | 'clutter'>('model');
  const [showSavedTrainingImage, setShowSavedTrainingImage] = useState(false);
  const [trainingImage, setTrainingImage] = useState<{ data: string; width: number; height: number; sourceKey: string; trained: boolean; receivedAt: string }>();
  const [trainingSamples, setTrainingSamples] = useState<{ data: string; width: number; height: number }[]>(() => {
    const parameters = (node.data.parameters ?? {}) as NodeParameters;
    const saved = parameters.trainedSourceImages;
    return Array.isArray(saved) && saved.length > 0
      ? saved.filter((item): item is { data: string; width: number; height: number } => Boolean(item && typeof item === 'object'
        && typeof (item as { data?: unknown }).data === 'string' && typeof (item as { width?: unknown }).width === 'number'
        && typeof (item as { height?: unknown }).height === 'number'))
      : [];
  });
  const [roiImageView, setRoiImageView] = useState<'reference' | 'input' | 'output'>('reference');
  const [previewError, setPreviewError] = useState<string>();
  const [preview, setPreview] = useState<{ result: RunResult; fingerprint: string; receivedAt: string; preserveTrainingWorkspace?: boolean }>();
  const [searchPreview, setSearchPreview] = useState<{ result: RunResult; fingerprint: string; receivedAt: string }>();
  const [searchWorkspacePreview, setSearchWorkspacePreview] = useState<{ result: RunResult; fingerprint: string; receivedAt: string; preserveTrainingWorkspace?: boolean }>();
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
  const isRoiTransform = catalogItem.type === 'roi.transform';
  const isCaliperCircle = catalogItem.type === 'measure.caliperCircle';
  const trainedModelData = typeof draft.trainedModelData === 'string' ? draft.trainedModelData : '';
  const trainedSourceImage = typeof draft.trainedSourceImage === 'string' ? draft.trainedSourceImage : '';
  const legacyWorkflow = isShapeMatch && draft.modelMode === 'legacy';
  const trainedSettings = draft.trainedModelSettings && typeof draft.trainedModelSettings === 'object'
    ? draft.trainedModelSettings as Record<string, unknown> : undefined;
  const currentTrainingSettings = {
    binarize: draft.binarize ?? 'auto', threshold: draft.threshold ?? 128, invert: draft.invert ?? false,
    trainingAngleRangeDeg: draft.trainingAngleRangeDeg ?? draft.angleRangeDeg ?? 180, angleStepDeg: draft.angleStepDeg ?? 1,
    pyramidLevels: draft.pyramidLevels ?? 0,
    scaleTolerancePercent: draft.scaleTolerancePercent ?? 0, scaleToleranceXPercent: draft.scaleToleranceXPercent ?? 0,
    scaleToleranceYPercent: draft.scaleToleranceYPercent ?? 0,
    scaleStepPercent: Number(draft.scaleTolerancePercent ?? 0) > 0 || Number(draft.scaleToleranceXPercent ?? 0) > 0 || Number(draft.scaleToleranceYPercent ?? 0) > 0 ? draft.scaleStepPercent ?? 1 : 0,
    trainingMinSampleFraction: draft.trainingMinSampleFraction ?? 0.75, trainingFeatureTolerancePx: draft.trainingFeatureTolerancePx ?? 2,
    ignorePolarity: draft.ignorePolarity ?? false, minContrast: draft.minContrast ?? 5,
    clutterContrast: draft.clutterRoi ? draft.clutterContrast ?? draft.minContrast ?? 5 : 0,
    trainingOriginMode: draft.trainingOriginMode ?? 'centroid', trainingOriginX: draft.trainingOriginX ?? 0,
    trainingOriginY: draft.trainingOriginY ?? 0, roi: draft.roi ?? null, clutterRoi: draft.clutterRoi ?? null,
    templatePath: draft.templatePath ?? ''
  };
  const trainingSettingsWithDefaults = trainedSettings && (() => {
    const normalized: Record<string, unknown> = { scaleTolerancePercent: 0, scaleToleranceXPercent: 0, scaleToleranceYPercent: 0, scaleStepPercent: 1,
      trainingMinSampleFraction: 0.75, trainingFeatureTolerancePx: 2, ignorePolarity: false, minContrast: 5, clutterContrast: 0,
      clutterRoi: null, pyramidLevels: 0, ...trainedSettings };
    if (normalized.trainingAngleRangeDeg === undefined) normalized.trainingAngleRangeDeg = normalized.angleRangeDeg ?? 180;
    delete normalized.angleRangeDeg;
    if (Number(normalized.scaleTolerancePercent) === 0 && Number(normalized.scaleToleranceXPercent) === 0 && Number(normalized.scaleToleranceYPercent) === 0) normalized.scaleStepPercent = 0;
    return normalized;
  })();
  const trainingStale = Boolean(trainedModelData && trainingSettingsWithDefaults && parameterFingerprint(trainingSettingsWithDefaults) !== parameterFingerprint(currentTrainingSettings));
  const shapeSearchBlocked = isShapeMatch && (!trainedModelData && !legacyWorkflow || trainingStale);
  const trainingBusy = trainBusy;
  const templateSourceNodeId = edges.find((edge) => edge.target === node.id && (edge.targetHandle === 'template' || edge.targetHandle?.endsWith('template')))?.source;
  const trainingSourceKey = templateSourceNodeId ?? String(draft.templatePath ?? '');
  const capturedTrainingImage = trainingImage?.sourceKey === trainingSourceKey ? trainingImage : undefined;
  const hasLosslessTrainingImage = Boolean(capturedTrainingImage?.data || trainedSourceImage || trainingSamples.length);
  const trainingSampleData = new Set(trainingSamples.map(sample => sample.data));
  const currentTrainingSampleData = capturedTrainingImage?.data || trainedSourceImage;
  const effectiveTrainingSampleCount = trainingSamples.length + (currentTrainingSampleData && !trainingSampleData.has(currentTrainingSampleData) ? 1 : 0);
  const changedTrainingLabels = trainingSettingsWithDefaults ? Object.keys(currentTrainingSettings)
    .filter(key => parameterFingerprint(trainingSettingsWithDefaults[key as keyof typeof trainingSettingsWithDefaults]) !== parameterFingerprint(currentTrainingSettings[key as keyof typeof currentTrainingSettings]))
    .map(key => catalogItem.parameters.find(parameter => parameter.name === key)?.label ?? (key === 'roi' ? '模型区域' : key)).join('、') : '';
  const roiInputEdge = edges.find((edge) => edge.target === node.id && edge.targetHandle === 'roi');
  const roiInputConnected = isRoiTransform && Boolean(roiInputEdge);
  const roiMaskInputConnected = !isRoiTransform && !isImageCrop && Boolean(roiInputEdge);
  const cropRoiEdge = isImageCrop ? roiInputEdge : undefined;
  const cropRoiSourceId = cropRoiEdge?.source;
  const cropRoiInputConnected = Boolean(cropRoiEdge);
  const searchLineInputEdge = ['measure.caliper', 'measure.caliperLine', 'feature.edgeSubpixel'].includes(catalogItem.type)
    ? edges.find((edge) => edge.target === node.id && edge.targetHandle === 'searchLine')
    : undefined;
  const circleInputEdge = isCaliperCircle
    ? edges.find((edge) => edge.target === node.id && edge.targetHandle === 'circle')
    : undefined;
  const transformInputEdge = ['measure.caliper', 'measure.caliperLine', 'measure.caliperCircle', 'feature.edgeSubpixel'].includes(catalogItem.type)
    ? edges.find((edge) => edge.target === node.id && edge.targetHandle === 'transform')
    : undefined;
  const transformSourceNode = transformInputEdge ? nodes.find((item) => item.id === transformInputEdge.source) : undefined;
  const transformSourceParameters = (transformSourceNode?.data.parameters ?? {}) as NodeParameters;
  const savedMeasurementReference = transformSourceNode?.data.typeKey === 'roi.transform'
    && typeof transformSourceParameters.referenceImage === 'string' && transformSourceParameters.referenceImage
    ? { data: transformSourceParameters.referenceImage, width: Number(transformSourceParameters.referenceWidth ?? 0),
      height: Number(transformSourceParameters.referenceHeight ?? 0) }
    : undefined;
  const cropMeasurementReferenceNodeId = transformSourceNode?.data.typeKey === 'image.crop'
    ? edges.find((edge) => edge.target === transformSourceNode.id && edge.targetHandle === 'image')?.source
    : undefined;
  const debugImageSourceId = isRoiTransform
    ? edges.find(edge => edge.target === node.id && edge.targetHandle === 'debugImage')?.source
    : undefined;
  const debugImageConnected = isRoiTransform && edges.some((edge) => edge.target === node.id && edge.targetHandle === 'debugImage');
  const poseEdge = edges.find(edge => edge.target === node.id && edge.targetHandle === 'currentPose');
  const poseSource = nodes.find(item => item.id === poseEdge?.source);
  const poseSourceParameters = (poseSource?.data.parameters ?? {}) as NodeParameters;
  const referenceBindings = parameterFingerprint({
    inputs: edges.filter(edge => edge.target === node.id && ['currentPose', 'debugImage'].includes(edge.targetHandle ?? ''))
      .map(edge => ({ port: edge.targetHandle, source: edge.source, sourcePort: edge.sourceHandle })).sort((a, b) => String(a.port).localeCompare(String(b.port))),
    imageFrame: imageFrameFingerprint(debugImageSourceId, nodes, edges),
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
    if (!preview || preview.preserveTrainingWorkspace) return;
    setResultExpanded(true);
    const frame = requestAnimationFrame(() => resultRef.current?.scrollIntoView?.({ block: 'nearest' }));
    return () => cancelAnimationFrame(frame);
  }, [preview]);
  const receivedAt = preview?.receivedAt ?? resultReceivedAt;
  const stale = Boolean(previewError) || (preview ? preview.fingerprint !== draftFingerprint : resultStale || dirty);
  const matchReport = (searchPreview?.result ?? result)?.nodeReports.filter(report => report.nodeId === node.id).slice(-1)[0];
  const matchStale = Boolean(previewError) || (searchPreview ? searchPreview.fingerprint !== draftFingerprint : resultStale || dirty);
  const matchNumber = (port: string, summaryKey = port, digits = 2) => {
    const value = matchReport?.outputs?.[port]?.value ?? matchReport?.summary[summaryKey];
    return typeof value === 'number' && Number.isFinite(value) ? value.toFixed(digits) : '—';
  };
  const nodeLabels = Object.fromEntries(nodes.map((item) => [item.id, String(item.data.label ?? item.id)]));
  const { imageSource, setImageSource, imageViewerSource, viewerOverlays, selectedOutputAvailable, selectedInputAvailable,
    imageCatalogLoading, imageCatalogError, retryImageCatalog } =
    useRunImages({ result: displayedResult, selectedId: previewNodeId, nodes, edges, nodeLabels });
  const measurementGeometryType = isCaliperCircle ? 'circle'
    : ['measure.caliper', 'measure.caliperLine', 'feature.edgeSubpixel'].includes(catalogItem.type) ? 'line' : undefined;
  const measurementGeometryFrameKind = !transformInputEdge ? 'current'
    : savedMeasurementReference ? 'saved-reference'
      : cropMeasurementReferenceNodeId ? 'transform-input' : undefined;
  const canEditMeasurementGeometry = Boolean(measurementGeometryType && !searchLineInputEdge && !circleInputEdge && measurementGeometryFrameKind);
  useEffect(() => {
    if (!open || !canEditMeasurementGeometry) return;
    if (measurementGeometryFrameKind === 'saved-reference' || measurementGeometryFrameKind === 'current') {
      setPreviewNodeId(node.id);
      setImageSource('input');
    } else if (cropMeasurementReferenceNodeId) {
      setPreviewNodeId(cropMeasurementReferenceNodeId);
      setImageSource('output');
    }
  }, [canEditMeasurementGeometry, cropMeasurementReferenceNodeId, measurementGeometryFrameKind, node.id, open, setImageSource]);
  const measurementImageFrameReady = measurementGeometryFrameKind === 'saved-reference'
    ? previewNodeId === node.id && Boolean(savedMeasurementReference && savedMeasurementReference.width > 0 && savedMeasurementReference.height > 0)
    : measurementGeometryFrameKind === 'transform-input'
      ? previewNodeId === cropMeasurementReferenceNodeId && imageSource === 'output' && selectedOutputAvailable
      : previewNodeId === node.id
        && (imageSource === 'input' && selectedInputAvailable || imageSource === 'output' && selectedOutputAvailable);
  const geometryNumber = (key: string, fallback: number) => Number.isFinite(Number(draft[key])) ? Number(draft[key]) : fallback;
  const measurementGeometry: MeasurementGeometry | undefined = canEditMeasurementGeometry && measurementImageFrameReady
    ? measurementGeometryType === 'line'
      ? { type: 'line', start: { x: geometryNumber('startX', 120), y: geometryNumber('startY', 240) },
        end: { x: geometryNumber('endX', 520), y: geometryNumber('endY', 240) },
        ...(catalogItem.type === 'measure.caliperLine' ? { profileSpan: Math.max(0, geometryNumber('edgeLength', 0)),
          profileCount: geometryNumber('caliperCount', 9) } : {}) }
      : { type: 'circle', center: { x: geometryNumber('centerX', 320), y: geometryNumber('centerY', 240) },
        radius: Math.max(0, geometryNumber('radius', 100)), band: Math.max(0, geometryNumber('radialBand', 8)),
        angleStartDeg: geometryNumber('angleStartDeg', -180), angleEndDeg: geometryNumber('angleEndDeg', 180),
        caliperCount: geometryNumber('caliperCount', 72), samplingMode: String(draft.samplingMode ?? 'count'),
        angleStepDeg: geometryNumber('angleStepDeg', 5) }
    : undefined;
  const updateMeasurementGeometry = (geometry: MeasurementGeometry) => {
    if (geometry.type === 'line') {
      setDraft(current => ({ ...current, startX: geometry.start.x, startY: geometry.start.y, endX: geometry.end.x, endY: geometry.end.y }));
    } else {
      setDraft(current => ({ ...current, centerX: geometry.center.x, centerY: geometry.center.y, radius: geometry.radius }));
    }
  };
  const roiInputImageSourceId = edges.find((edge) => edge.target === node.id && edge.targetHandle === 'image')?.source;
  const roiSourceNode = roiInputEdge ? nodes.find((item) => item.id === roiInputEdge.source) : undefined;
  const roiSourceDebugImageId = roiInputEdge
    ? edges.find((edge) => edge.target === roiInputEdge.source && edge.targetHandle === 'debugImage')?.source
    : undefined;
  // RoiTransform overlays are in the debugImage coordinate frame. Only copy them onto an input
  // image when that exact frame is connected, so a different preprocessed image is never misled.
  const roiOverlayFrameMatchesInput = roiSourceNode?.data.typeKey === 'roi.transform'
    && Boolean(roiInputImageSourceId && (roiInputImageSourceId === roiInputEdge?.source || roiInputImageSourceId === roiSourceDebugImageId));
  const connectedRoiInputOverlays = roiInputEdge && imageSource === 'input'
    && previewNodeId === node.id && selectedInputAvailable && roiOverlayFrameMatchesInput
    ? (displayedResult?.overlays ?? []).filter((overlay) => overlay.nodeId === roiInputEdge.source)
    : [];
  useEffect(() => {
    if (open && isRoiTransform && roiImageView !== 'reference') setImageSource(roiImageView);
  }, [isRoiTransform, open, roiImageView, imageSource, setImageSource]);
  useEffect(() => {
    if (!open || !isImageCrop) return;
    setPreviewNodeId(node.id);
    setImageSource('input');
  }, [isImageCrop, node.id, open, setImageSource]);
  const trainingContours = Array.isArray(draft.trainedContours) ? draft.trainedContours as { x: number; y: number }[][]
    : Array.isArray(draft.trainedContour) ? [draft.trainedContour as { x: number; y: number }[]] : [];
  const trainedPreviewData = typeof draft.trainedPreviewImage === 'string' ? draft.trainedPreviewImage : '';
  const showTrainedTemplatePreview = Boolean(showSavedTrainingImage && trainingView && isShapeMatch && (trainedSourceImage || trainedPreviewData));
  const showCapturedTrainingImage = isShapeMatch && shapeWorkspaceTab === 'training' && !showSavedTrainingImage && capturedTrainingImage;
  const shapeImageMatchesTab = shapeImageKind === shapeWorkspaceTab || (shapeWorkspaceTab === 'training' && (showTrainedTemplatePreview || Boolean(showCapturedTrainingImage)));
  const showMeasurementReferenceImage = Boolean(canEditMeasurementGeometry && measurementGeometryFrameKind === 'saved-reference' && measurementImageFrameReady);
  const displayImageSource = isRoiTransform && roiImageView === 'reference'
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
      : [...new Map([...viewerOverlays, ...connectedRoiInputOverlays].map(overlay => [`${overlay.nodeId ?? ''}:${overlay.id}`, overlay])).values()];
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
    if (locked || issues.length || externalChange || previewBlockedReason || shapeSearchBlocked || referenceMismatch) return;
    if (isShapeMatch) { setShapeWorkspaceTab('search'); setShapeImageKind(undefined); setRegionEditMode('search'); }
    setTrainingView(false);
    setShowSavedTrainingImage(false);
    const submitted = cloneParameters(draft);
    if (isRoiTransform && debugImageConnected && !roiInputConnected && !draft.roi)
      submitted.debugImagePreviewOnly = true;
    else if (isRoiTransform)
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
      if (isImageCrop) { setPreviewNodeId(node.id); setImageSource('input'); }
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '节点测试失败。');
      setResultExpanded(true);
    } finally { setPreviewBusy(false); }
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
      if (typeof report.summary.trainingImageData === 'string') setTrainingImage({ data: report.summary.trainingImageData,
        width: Number(report.summary.trainingSourceWidth), height: Number(report.summary.trainingSourceHeight), sourceKey: trainingSourceKey, trained: false, receivedAt: new Date().toISOString() });
      const safeResult = { ...next, nodeReports: next.nodeReports.map(item => ({ ...item,
        summary: Object.fromEntries(Object.entries(item.summary).filter(([key]) => key !== 'trainingImageData')) })) };
      setPreview({ result: safeResult, fingerprint: draftFingerprint, receivedAt: new Date().toISOString(), preserveTrainingWorkspace: true });
      setShapeImageKind('training');
      setPreviewNodeId(node.id); setImageSource('output');
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '训练图像获取失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); setTrainingImageBusy(false); }
  };
  const addCurrentTrainingSample = () => {
    if (!capturedTrainingImage?.data || locked) return;
    const current = { data: capturedTrainingImage.data, width: capturedTrainingImage.width, height: capturedTrainingImage.height };
    const samples = trainingSamples.filter(sample => sample.data !== current.data);
    const reference = samples[0];
    if (reference && (reference.width !== current.width || reference.height !== current.height)) {
      setPreviewError('多样本训练要求图像尺寸一致。请采集与现有样本相同尺寸的图像。');
      return;
    }
    if (samples.length >= 4) {
      setPreviewError('最多可联合训练 4 张图像。');
      return;
    }
    if (samples.reduce((total, sample) => total + sample.data.length, current.data.length) > 32 * 1024 * 1024) {
      setPreviewError('样本图像总大小超过限制，请裁剪图像或减少样本数量。');
      return;
    }
    setTrainingSamples([...samples, current]);
    setPreviewError(undefined);
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
      const scenePreview = { result: next, fingerprint: draftFingerprint, receivedAt: new Date().toISOString(), preserveTrainingWorkspace: true };
      setPreview(scenePreview); setSearchWorkspacePreview(scenePreview);
      setShapeImageKind('search');
      setPreviewNodeId(node.id); setImageSource('output');
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '场景图像获取失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); setSearchImageBusy(false); }
  };
  const trainShapeModel = async () => {
    if (locked || issues.length || externalChange || previewBlockedReason) return;
    if (!hasLosslessTrainingImage) {
      setPreviewError('请先获取训练图像，再训练模型。这样保存的模型才能关联原始无损图像。');
      return;
    }
    const currentImage = capturedTrainingImage?.data ? { data: capturedTrainingImage.data, width: capturedTrainingImage.width, height: capturedTrainingImage.height }
      : trainedSourceImage ? { data: trainedSourceImage, width: Number(draft.trainedSourceWidth ?? 0), height: Number(draft.trainedSourceHeight ?? 0) } : undefined;
    const sampleSet = [...trainingSamples];
    if (currentImage && !sampleSet.some(sample => sample.data === currentImage.data)) sampleSet.push(currentImage);
    if (!sampleSet.length) {
      setPreviewError('请先获取训练图像，再训练模型。这样保存的模型才能关联原始无损图像。');
      return;
    }
    if (sampleSet.length > 4 || sampleSet.some(sample => sample.width !== sampleSet[0].width || sample.height !== sampleSet[0].height)) {
      setPreviewError('多样本训练最多支持 4 张同尺寸图像。请清除不兼容样本后重试。');
      return;
    }
    const totalSourceLength = sampleSet.reduce((total, sample) => total + sample.data.length, 0);
    if (sampleSet.length > 1 && totalSourceLength > 32 * 1024 * 1024) {
      setPreviewError('样本图像总大小超过限制，请清除样本或裁剪训练图像。');
      return;
    }
    setShapeWorkspaceTab('training'); setShapeImageKind(undefined); setTrainingView(true);
    setShowSavedTrainingImage(false);
    setPreviewBusy(true); setTrainBusy(true); setPreviewError(undefined);
    try {
      const trainingRuns: { result: RunResult; summary: Record<string, unknown> }[] = [];
      for (let sampleIndex = 0; sampleIndex < sampleSet.length; sampleIndex++) {
        const sample = sampleSet[sampleIndex];
        const firstSample = trainingRuns[0]?.summary;
        const submitted = cloneParameters({ ...draft, modelMode: 'trained', trainingMode: true, trainingImageData: sample.data,
          ...(sampleIndex > 0 && firstSample ? {
            trainingReferenceModelData: String(firstSample.trainingModelData ?? ''),
            trainingReferenceOriginImageX: Number(firstSample.trainingOriginImageX ?? 0),
            trainingReferenceOriginImageY: Number(firstSample.trainingOriginImageY ?? 0)
          } : {}) });
        const next = await onPreview(submitted);
        const report = next.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
        const summary = report?.summary ?? {};
        if (!next.success || !report?.success || summary.trainingSucceeded !== true || typeof summary.trainingModelData !== 'string' || !summary.trainingModelData)
          throw new Error(report?.error || next.error || `第 ${trainingRuns.length + 1} 张样本训练失败，请检查图像和模型区域。`);
        trainingRuns.push({ result: next, summary });
      }
      const modelVariants = trainingRuns.map(run => ({ data: String(run.summary.trainingModelData), width: Number(run.summary.trainingWidth),
        height: Number(run.summary.trainingHeight), originX: Number(run.summary.trainingOriginX), originY: Number(run.summary.trainingOriginY),
        sourceOriginX: Number(run.summary.trainingOriginImageX), sourceOriginY: Number(run.summary.trainingOriginImageY) }));
      const combinedModelBytes = modelVariants.reduce((total, variant) => total + Math.floor(variant.data.length * 3 / 4), 0);
      if (combinedModelBytes > 16 * 1024 * 1024)
        throw new Error('多样本模型输入超过 16 MB 限制，请减少样本数量或裁剪模型区域。');
      let next = trainingRuns[trainingRuns.length - 1].result;
      let summary = trainingRuns[0].summary;
      let combinedModelData = modelVariants[0].data;
      if (modelVariants.length > 1) {
        const modelSetData = `shape-model-set-v1:${JSON.stringify({ version: 1, models: modelVariants })}`;
        if (new TextEncoder().encode(modelSetData).length > 24 * 1024 * 1024)
          throw new Error('多样本模型输入超过 24 MB 工作流请求限制，请减少样本数量或裁剪图像。');
        const combined = await onPreview(cloneParameters({ ...draft, modelMode: 'trained', trainingMode: true,
          trainingImageData: sampleSet[0].data, trainingCombineModelData: modelSetData,
          trainingCombineSourceImages: JSON.stringify(sampleSet.map(sample => sample.data)) }));
        const combinedReport = combined.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
        const combinedSummary = combinedReport?.summary ?? {};
        if (!combined.success || !combinedReport?.success || combinedSummary.trainingSucceeded !== true
          || typeof combinedSummary.trainingModelData !== 'string' || !combinedSummary.trainingModelData)
          throw new Error(combinedReport?.error || combined.error || '复合模型训练失败，请检查样本对齐、模型区域和特征出现比例。');
        next = combined;
        summary = combinedSummary;
        combinedModelData = String(combinedSummary.trainingModelData);
      }
      if (new TextEncoder().encode(combinedModelData).length > 24 * 1024 * 1024)
        throw new Error('训练模型超过工作流资产大小限制，请减少样本数量或裁剪图像。');
      const snapshot = {
        binarize: draft.binarize ?? 'auto', threshold: draft.threshold ?? 128, invert: draft.invert ?? false,
        trainingAngleRangeDeg: draft.trainingAngleRangeDeg ?? draft.angleRangeDeg ?? 180, angleStepDeg: draft.angleStepDeg ?? 1,
        pyramidLevels: draft.pyramidLevels ?? 0,
        scaleTolerancePercent: draft.scaleTolerancePercent ?? 0, scaleToleranceXPercent: draft.scaleToleranceXPercent ?? 0,
        scaleToleranceYPercent: draft.scaleToleranceYPercent ?? 0, scaleStepPercent: draft.scaleStepPercent ?? 1,
        trainingMinSampleFraction: draft.trainingMinSampleFraction ?? 0.75, trainingFeatureTolerancePx: draft.trainingFeatureTolerancePx ?? 2,
        ignorePolarity: draft.ignorePolarity ?? false, minContrast: draft.minContrast ?? 5,
        clutterContrast: draft.clutterRoi ? draft.clutterContrast ?? draft.minContrast ?? 5 : 0,
        trainingOriginMode: draft.trainingOriginMode ?? 'centroid', trainingOriginX: draft.trainingOriginX ?? 0,
        trainingOriginY: draft.trainingOriginY ?? 0, roi: draft.roi ?? null, clutterRoi: draft.clutterRoi ?? null,
        templatePath: draft.templatePath ?? ''
      };
      const completed = { ...draft, modelMode: 'trained', trainingMode: false, trainedModelData: combinedModelData,
        trainedModelHash: String(summary.trainingModelHash ?? trainingRuns.map(run => String(run.summary.trainingModelHash ?? '')).filter(Boolean).join(':')),
        trainedModelConfig: String(summary.trainingConfig ?? ''), trainedModelSettings: snapshot,
        trainedSourceImage: sampleSet[0].data, trainedSourceImages: sampleSet.length > 1 ? sampleSet : undefined,
        trainedContour: Array.isArray(summary.trainingContour) ? summary.trainingContour : [],
        trainedContours: Array.isArray(summary.trainingContours) ? summary.trainingContours : [],
        trainedModelWarnings: [...(Array.isArray(summary.trainingWarnings) ? summary.trainingWarnings as string[] : []),
          ...trainingRuns.flatMap((run, index) => Array.isArray(run.summary.trainingWarnings)
            ? (run.summary.trainingWarnings as string[]).map(warning => `样本 ${index + 1}：${warning}`) : [])],
        trainedSampleCount: sampleSet.length,
        trainedFeaturePointCount: Number(summary.trainingFeaturePointCount ?? 0),
        trainedContourCount: Number(summary.trainingContourCount ?? 0),
        trainedSymmetryPeriodDeg: summary.trainingSymmetryPeriodDeg ?? null,
        trainedPreviewImage: typeof summary.trainingPreviewImage === 'string' ? summary.trainingPreviewImage : '',
        trainedSourceWidth: Number(summary.trainingSourceWidth ?? 0), trainedSourceHeight: Number(summary.trainingSourceHeight ?? 0),
        trainedWidth: Number(summary.trainingWidth ?? 0), trainedHeight: Number(summary.trainingHeight ?? 0),
        trainedOriginX: Number(summary.trainingOriginX ?? 0), trainedOriginY: Number(summary.trainingOriginY ?? 0),
        trainedOriginImageX: Number(summary.trainingOriginImageX ?? 0), trainedOriginImageY: Number(summary.trainingOriginImageY ?? 0),
        trainedPoseOffsetX: Number(summary.trainingPoseOffsetX ?? 0), trainedPoseOffsetY: Number(summary.trainingPoseOffsetY ?? 0) };
      setDraft(completed);
      setTrainingSamples(sampleSet.length > 1 ? sampleSet : []);
      if (capturedTrainingImage && !showTrainedTemplatePreview) setTrainingImage({ ...capturedTrainingImage, trained: true });
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
    if (locked || issues.length || externalChange || referenceMismatch || !dirty || isShapeMatch && !legacyWorkflow && trainingStale) return;
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
  const locateSavedTrainingImage = () => {
    setShapeWorkspaceTab('training'); setShapeImageKind('training'); setRegionEditMode('model');
    setTrainingView(true); setShowSavedTrainingImage(true); setPreviewNodeId(templateSourceNodeId ?? node.id); setImageSource('output'); setFocusTick((value) => value + 1);
  };
  const roiTransformEditHint = roiInputConnected
    ? 'roi 输入端口已连接，上游 ROI 优先于本节点参数。请在上游 ROI 节点编辑区域。'
    : referenceMismatch ? '图像或定位来源、模型原点已变化，请重新设为基准后再编辑、运行或应用。'
      : !referenceImage ? '先获取图像并设为基准，系统会同时保存该次图像与实际位姿，再在基准图上绘制 ROI。旧流程的手动基准参数继续兼容。'
        : roiImageView === 'reference' ? '在已保存基准图上编辑 ROI。重新设为基准会同步迁移已有区域；当前图与修正结果只读。'
          : '当前图像只读。切回已保存基准图像编辑区域，避免对当前图上的选区重复施加位移。';
  const changeShapeWorkspaceTab = (key: string) => {
    if (locked) return;
    const next = key === 'search' ? 'search' : 'training';
    setShapeWorkspaceTab(next);
    setShapeImageKind(undefined);
    if (next === 'training') {
      setTrainingView(true); setRegionEditMode('model');
      if (capturedTrainingImage) { setShowSavedTrainingImage(false); setShapeImageKind('training'); }
      else if (trainedSourceImage || trainedPreviewData) locateSavedTrainingImage();
      else void fetchTrainingImage();
    } else {
      setTrainingView(false); setRegionEditMode('search'); setShowSavedTrainingImage(false);
      if (searchWorkspacePreview || hasExistingSearch) { setPreview(searchWorkspacePreview); setShapeImageKind('search'); setPreviewNodeId(node.id); setImageSource('output'); }
      else void fetchSearchImage();
    }
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
        if (parameter.name === 'angleRangeDeg' && !legacyWorkflow) return false;
        if (parameter.name === 'templatePath' && templateSourceNodeId) return false;
        if (parameter.name === 'scoreUsingClutter' && !draft.clutterRoi) return false;
        if (['trainingOriginX', 'trainingOriginY'].includes(parameter.name) && draft.trainingOriginMode !== 'manual') return false;
        if (parameter.name === 'scaleStepPercent' && Number(draft.scaleTolerancePercent ?? 0) === 0
          && Number(draft.scaleToleranceXPercent ?? 0) === 0 && Number(draft.scaleToleranceYPercent ?? 0) === 0) return false;
        if (parameter.name === 'minArea') return !trainingMode;
        return trainingMode
          ? ['Training', 'Origin Estimation', 'General'].includes(group)
          : ['Matching', 'Multi-target', 'Search'].includes(group);
      }).map((parameter) => ({
        ...parameter,
        group: ({
          Training: '模型训练', 'Origin Estimation': '原点估算', Candidates: '特征筛选', General: '训练图像来源',
          Matching: '匹配条件', 'Multi-target': '多目标定位', Search: '搜索范围'
        } as Record<string, string>)[['trainingAngleRangeDeg', 'scaleTolerancePercent', 'scaleToleranceXPercent', 'scaleToleranceYPercent'].includes(parameter.name)
          ? 'Training' : parameter.group ?? 'General'] ?? parameter.group
      }))
    } : searchLineInputEdge ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
      !['startX', 'startY', 'endX', 'endY'].includes(parameter.name))
    } : circleInputEdge ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
      !['centerX', 'centerY', 'radius'].includes(parameter.name))
    } : isRoiTransform ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
      !(referenceImage && ['baseX', 'baseY', 'baseThetaDeg'].includes(parameter.name))
      && !(poseEdge && ['currentX', 'currentY', 'currentThetaDeg'].includes(parameter.name))) } : catalogItem;
    const roiFields = shapeMode ? trainingMode
      ? [
        { key: 'roi', label: '模型区域', emptyText: '未设置 · 使用整幅训练图像', showEmpty: true },
        { key: 'clutterRoi', label: '杂乱区域', emptyText: '未设置 · 不启用杂乱度评分', showEmpty: true }
      ]
      : [{ key: 'searchRoi', label: '搜索区域', emptyText: '未设置 · 使用整幅场景图像', showEmpty: true }]
      : isImageCrop ? cropRoiInputConnected ? []
        : [{ key: 'roi', label: '裁剪区域', emptyText: '未绘制 · 使用旧版坐标或整幅图像', showEmpty: true }]
      : isRoiTransform ? roiInputConnected ? [] : [{ key: 'roi', label: '基准 ROI', emptyText: '设定基准后在基准图上绘制', showEmpty: true }]
        : roiMaskInputConnected || showMeasurementReferenceImage ? [] : undefined;
    return <div ref={bindWorkspace} tabIndex={-1} className={'node-configuration-workspace' + (showImage ? '' : ' without-image')}
      style={{ '--node-columns': columns(imageShare) } as CSSProperties}>
      {showImage && <section className={'node-configuration-image' + (isRoiTransform ? ' has-roi-reference' : '')} aria-label={shapeMode === 'training' ? '模型训练图像' : shapeMode === 'search' ? '搜索推理图像' : '图像预览'}>
        <div className="node-configuration-image-source">
          {shapeMode ? <>
            <span>{trainingMode ? '训练图像' : '搜索场景图像'}</span>
            <Button size="small" onClick={() => void (trainingMode ? fetchTrainingImage() : fetchSearchImage())}
              loading={trainingMode ? trainingImageBusy : searchImageBusy}
              disabled={locked || externalChange || Boolean(previewBlockedReason)}
              title={previewBlockedReason || (trainingMode ? '读取训练源图像并编辑模型区域' : '读取运行场景图像并编辑搜索区域')}>
              {trainingMode ? '获取训练图像' : '获取场景图像'}
            </Button>
            {trainingMode && trainedModelData && (trainedSourceImage || trainedPreviewData) &&
              <Button size="small" disabled={locked} onClick={locateSavedTrainingImage}>查看已保存训练图像</Button>}
          </> : isImageCrop ? <>
            <span>裁剪输入图像</span>
            <Segmented size="small" value={imageSource === 'output' ? 'output' : 'input'} onChange={(value) => setImageSource(value as 'input' | 'output')}
              options={[{ label: '输入图像', value: 'input', disabled: !selectedInputAvailable }, { label: '裁剪输出', value: 'output', disabled: !selectedOutputAvailable }]} />
          </> : isRoiTransform ? <>
            <Button size="small" type={referenceImage ? 'default' : 'primary'} loading={previewBusy}
              aria-label={referenceImage ? '重新设为基准' : '获取图像并设为基准'}
              disabled={locked || externalChange || Boolean(previewBlockedReason) || !debugImageConnected}
              title={!debugImageConnected ? '请先将场景图像连接到调试图像端口' : previewBlockedReason || '同时保存本次图像与实际定位位姿，并迁移已有 ROI'}
              onClick={() => void captureRoiReference()}>{referenceImage ? '重新设为基准' : '获取图像并设为基准'}</Button>
            <Segmented size="small" value={roiImageView} onChange={value => setRoiImageView(value as typeof roiImageView)}
              options={[{ label: '已保存基准', value: 'reference', disabled: !referenceImage },
                { label: '当前输入', value: 'input', disabled: !selectedInputAvailable },
                { label: '修正结果', value: 'output', disabled: !selectedOutputAvailable }]} />
          </> : canEditMeasurementGeometry && showMeasurementReferenceImage ? <span>已保存基准图像 · 测量几何坐标</span>
            : canEditMeasurementGeometry && measurementGeometryFrameKind === 'transform-input' && cropMeasurementReferenceNodeId
              ? <span>几何基准图像 · {nodeLabels[cropMeasurementReferenceNodeId] ?? cropMeasurementReferenceNodeId}</span>
              : <>
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
          {isRoiTransform && roiImageView === 'reference' && draft.referenceCapturedAt
            ? <span>基准采集于 {new Date(String(draft.referenceCapturedAt)).toLocaleString()}</span>
            : showCapturedTrainingImage ? <span>采集于 {new Date(capturedTrainingImage.receivedAt).toLocaleString()}</span>
              : !showTrainedTemplatePreview && receivedAt && <span>更新于 {new Date(receivedAt).toLocaleString()}</span>}
          {stale && displayedResult && !(isRoiTransform && roiImageView === 'reference') && <Tag color="gold">上次运行图像 · 当前参数待验证</Tag>}
        </div>
        {isRoiTransform && <Alert showIcon type={roiInputConnected || referenceMismatch || !referenceImage ? 'warning' : canEditRoi ? 'success' : 'info'}
          title={roiInputConnected ? '当前 ROI 来自上游节点' : canEditRoi ? '正在编辑基准 ROI' : '基准 ROI 编辑说明'}
          description={<Space wrap>
            <span>{roiTransformEditHint}</span>
            {!debugImageConnected && <span>获取基准前，请将场景图像连接到“调试图像”端口。</span>}
            {referenceImage && roiImageView !== 'reference' && <Button size="small" disabled={locked}
              onClick={() => setRoiImageView('reference')}>切换到已保存基准</Button>}
          </Space>} />}
        {roiMaskInputConnected && <Alert showIcon type="info" title={`ROI 掩膜来自上游：${nodeLabels[roiInputEdge!.source] ?? roiInputEdge!.source}`}
          description="此节点的 roi 输入优先于本地选区，且按当前图像坐标解释。请在上游编辑区域；当前节点不会再次移动 ROI。" />}
        {searchLineInputEdge && <Alert showIcon type="info"
          title={`搜索线来自上游：${nodeLabels[searchLineInputEdge.source] ?? searchLineInputEdge.source}`}
          description={transformInputEdge
            ? `上游搜索线会再通过“${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}”的变换映射到当前图像；请确认上游线处于基准坐标，已是当前坐标的线不要重复变换。`
            : '上游 searchLine 会覆盖本节点的起点和终点参数；请在提供搜索线的上游节点中调整几何。'} />}
        {circleInputEdge && <Alert showIcon type={transformInputEdge ? 'warning' : 'info'}
          title={`标称圆来自上游：${nodeLabels[circleInputEdge.source] ?? circleInputEdge.source}`}
          description={transformInputEdge
            ? `该圆还会经过“${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}”的变换；若上游已输出当前图像坐标（例如定位修正 ROI），请断开 transform，避免圆心被重复移动。`
            : '上游 circle 会覆盖本节点的圆心和标称半径参数；请在上游节点中调整圆几何。'} />}
        {!searchLineInputEdge && !circleInputEdge && transformInputEdge && <Alert showIcon type="info"
          title={`几何将按变换跟随：${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}`}
          description={canEditMeasurementGeometry
            ? `本节点几何属于基准图像坐标；当前显示的${showMeasurementReferenceImage ? '已保存基准图像' : '变换输入图像'}可直接拖动或重绘，transform 会在运行时映射到测量图像。ROI 掩膜仍使用当前图像坐标。`
            : '本节点几何属于基准图像坐标；transform 会在运行时映射到测量图像。可从支持的定位修正 ROI 或图像裁剪变换自动打开对应基准图像。ROI 掩膜仍使用当前图像坐标。'} />}
        <div className="node-configuration-viewer"><ImageViewer previewUrl={displayImageSource.url} imageWidth={displayImageSource.width} imageHeight={displayImageSource.height}
          overlays={overlaysWithTrainedContour} roi={canEditRoi ? (shapeMode === 'search' ? draft.searchRoi as VisionRoi | undefined
            : regionEditMode === 'clutter' ? draft.clutterRoi as VisionRoi | undefined : draft.roi as VisionRoi | undefined) : undefined} showRoiTools={canEditRoi || isRoiTransform}
          measurementGeometry={measurementGeometry} measurementGeometryEditable={Boolean(measurementGeometry)}
          onMeasurementGeometryChange={updateMeasurementGeometry}
          roiEnabled={canEditRoi && !locked && Boolean(displayImageSource.url)} roiTargetLabel={isImageCrop ? '裁剪输入图像' : isRoiTransform ? '基准 ROI' : shapeMode === 'search' ? '搜索区域' : regionEditMode === 'clutter' ? '杂乱区域' : shapeMode === 'training' ? '模型区域' : roiInputImageSourceId ? `输入图像 · ${nodeLabels[roiInputImageSourceId] ?? roiInputImageSourceId}` : String(node.data.label ?? node.id)}
          loading={previewBusy} onRoiChange={(roi) => isImageCrop ? changeCropRoi(roi) : changeParameter(shapeMode === 'search' ? 'searchRoi' : regionEditMode === 'clutter' ? 'clutterRoi' : 'roi', roi)} nodeLabels={nodeLabels} focusTick={focusTick}
          emptyHint={isRoiTransform ? '获取图像并设为基准，然后在已保存的图像上绘制 ROI' : isImageCrop ? '运行节点以加载输入图像，然后绘制裁剪区域' : shapeMode === 'training' ? regionEditMode === 'clutter' ? '在训练图像上绘制预期为空的杂乱区域' : '获取训练图像后可绘制模型区域' : shapeMode === 'search' ? '获取场景图像后可绘制搜索区域' : '运行此节点以显示图像与矢量覆盖层'}
          emptyAction={{
            label: isRoiTransform ? '获取图像并设为基准' : isImageCrop ? '运行以获取输入图像' : shapeMode === 'training' ? '获取训练图像' : shapeMode === 'search' ? '获取场景图像' : '运行此节点',
            onClick: () => void (isRoiTransform ? captureRoiReference() : shapeMode === 'training' ? fetchTrainingImage() : shapeMode === 'search' ? fetchSearchImage() : runPreview()),
            disabled: locked || (!shapeMode && !isRoiTransform && issues.length > 0) || externalChange || Boolean(previewBlockedReason) || (isRoiTransform && !debugImageConnected),
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
        {isRoiTransform && <div className="shape-training-panel">
          <strong>{referenceImage ? '已保存基准位姿' : '建立基准'}</strong>
          {referenceImage && <p>X {Number(draft.baseX).toFixed(2)} px · Y {Number(draft.baseY).toFixed(2)} px · 角度 {Number(draft.baseThetaDeg).toFixed(2)}°</p>}
          <p>{poseEdge ? `当前位姿来自 ${String(poseSource?.data.label ?? poseEdge.source)}；以每次运行的实际定位结果为准。` : '当前位姿使用下方手动参数。'}</p>
          <p>下游测量：将“变换”输出连接到测量节点的 transform，搜索线、方向和采样布局即可跟随。ROI 输出已经是当前图像坐标，不要重复变换。</p>
        </div>}
        {shapeMode === 'training' && <div className="shape-training-panel">
          <strong>建立形状模型</strong>
          <p>获取图像 → 绘制模型区域 → 可选绘制模型周围“正常时不应有边缘”的杂乱区域 → 可重复采集并加入最多 4 张同尺寸样本 → 融合稳定轮廓训练复合模型 → 测试搜索。每张图像都会锁定；后续样本先按首张模型定位，再自动变换模型区域、杂乱区域和姿态。</p>
          {draft.clutterRoi && <Button size="small" onClick={() => changeParameter('clutterRoi', undefined)} disabled={locked}>清除杂乱区域（关闭杂乱度评分）</Button>}
          <p>训练来源：{templateSourceNodeId ? `模板输入 ← ${nodeLabels[templateSourceNodeId] ?? templateSourceNodeId}（优先使用此输入）` : String(draft.templatePath || '请填写下方模板图像路径或连接模板输入')}</p>
          {(capturedTrainingImage || trainingSamples.length > 0) && <div className="shape-training-panel">
            <p>样本训练：{effectiveTrainingSampleCount}/4 张。系统会保留达到出现比例的共同轮廓，过滤单张样本中的偶发特征；当前图像会自动参与训练。</p>
            <Space wrap>
              <Button size="small" disabled={!capturedTrainingImage || locked || effectiveTrainingSampleCount >= 4}
                onClick={addCurrentTrainingSample}>加入当前图像为样本</Button>
              {trainingSamples.length > 0 && <Button size="small" disabled={locked} onClick={() => setTrainingSamples([])}>清空额外样本</Button>}
            </Space>
          </div>}
          {!hasLosslessTrainingImage && <Alert showIcon type="info" title="先获取训练图像" description={trainedModelData && trainedPreviewData ? '此模型只有旧版压缩预览。请重新获取原图后再训练，避免继续使用有损预览。' : '获取图像会锁定当前像素，训练和以后重新训练都会使用这份无损图像。'} />}
          {showCapturedTrainingImage && !capturedTrainingImage.trained && <p>当前图像尚未用于训练；测试搜索仍使用原有模型。点击“{trainedModelData ? '重新训练模型' : '训练模型'}”后使用当前图像。</p>}
          {legacyWorkflow
            ? <Alert showIcon type="info" title="旧模板图像兼容模式" description="本次运行使用 template 输入或模板路径；已保存的训练模型仍保留，训练后可切换到训练模型模式。" />
            : trainedModelData
              ? <Alert showIcon type={trainingStale ? 'warning' : 'success'} title={trainingStale ? '训练参数已变化，需要重新训练' : draft.trainedModelData !== baseline.trainedModelData ? '模型已训练，待应用到工作流' : '模型已保存在此工作流'} description={trainingStale ? `已修改：${changedTrainingLabels}。重新训练后即可测试搜索。` : `样本 ${draft.trainedSampleCount ?? 1} 张 · 模型尺寸 ${draft.trainedWidth ?? '—'} × ${draft.trainedHeight ?? '—'} px · ${Number(draft.trainedFeaturePointCount ?? 0).toLocaleString()} 个边缘特征点 · ${draft.trainedContourCount ?? 0} 段轮廓 · 可直接测试搜索，满意后点击“应用”。`} />
              : <Alert showIcon type="info" title="尚未训练" description="先获取训练图像并设置模型区域，再训练形状模型。" />}
          {Array.isArray(draft.trainedModelWarnings) && draft.trainedModelWarnings.length > 0 && <Alert showIcon type="warning" title="训练质量提示" description={(draft.trainedModelWarnings as string[]).join(' ')} />}
          {trainedModelData && !trainingStale && !legacyWorkflow && <Button onClick={() => void runPreview()}
            disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason)}>下一步：测试搜索</Button>}
        </div>}
        {shapeMode === 'search' && <div className="shape-training-panel shape-search-panel">
          <strong>搜索与定位</strong>
          <p>设置角度/尺度窗口、最低分数、局部形变、特征对比度、杂乱评分、目标数量和搜索区域，然后运行搜索。角度与尺度窗口会限制在模型训练覆盖内，调整它们无需重训；局部形变默认关闭，增大后可容忍边缘小幅移动，但会降低定位约束。运行时对比度阈值可剔除弱边缘，设为 0 时关闭；相对阈值需要无损训练图。启用“杂乱度参与评分”后，分数按匹配分 ×（1 − 杂乱分）调整，同时保留原生分数供核对。区域留空时搜索整张场景图。</p>
          {matchReport?.success && typeof matchReport.summary.found === 'boolean' && <Alert showIcon
            type={matchStale ? 'info' : matchReport.summary.found ? 'success' : 'warning'}
            title={`${matchStale ? '上次搜索 · ' : ''}${matchReport.summary.found ? '已找到目标' : '未找到目标'}${matchStale ? ' · 当前参数待验证' : ''}`}
            description={matchReport.summary.found
              ? `数量 ${matchNumber('count', 'count', 0)} · 最佳分数 ${matchNumber('score', 'bestScore', 3)}${typeof matchReport.summary.matchedNativeScore === 'number' ? `（原生 ${matchNumber('nativeScore', 'matchedNativeScore', 3)}）` : ''}${typeof matchReport.summary.matchedContrast === 'number' ? ` · 特征对比度 ${matchNumber('contrast', 'matchedContrast', 1)}` : ''}${typeof matchReport.summary.matchedContrastFraction === 'number' ? ` · 相对对比度 ${matchNumber('contrastFraction', 'matchedContrastFraction', 3)}` : ''} · X ${matchNumber('x')} / Y ${matchNumber('y')} px · 角度 ${matchNumber('angle', 'matchedAngle')}°`
              : '请检查目标是否在搜索区域内、场景与模型是否一致，以及最小匹配分数设置。'} />}
          {!legacyWorkflow && <p>模型覆盖：角度 ±{String(trainedSettings?.trainingAngleRangeDeg ?? trainedSettings?.angleRangeDeg ?? draft.trainingAngleRangeDeg ?? 180)}° · 尺度 {Number(trainedSettings?.scaleToleranceXPercent ?? draft.scaleToleranceXPercent ?? 0) > 0 || Number(trainedSettings?.scaleToleranceYPercent ?? draft.scaleToleranceYPercent ?? 0) > 0
            ? `X ${100 - Number(trainedSettings?.scaleToleranceXPercent ?? draft.scaleToleranceXPercent ?? 0)}%～${100 + Number(trainedSettings?.scaleToleranceXPercent ?? draft.scaleToleranceXPercent ?? 0)}% · Y ${100 - Number(trainedSettings?.scaleToleranceYPercent ?? draft.scaleToleranceYPercent ?? 0)}%～${100 + Number(trainedSettings?.scaleToleranceYPercent ?? draft.scaleToleranceYPercent ?? 0)}%`
            : `${100 - Number(trainedSettings?.scaleTolerancePercent ?? draft.scaleTolerancePercent ?? 0)}%～${100 + Number(trainedSettings?.scaleTolerancePercent ?? draft.scaleTolerancePercent ?? 0)}%`}
            <Button size="small" type="link" disabled={locked} onClick={() => changeShapeWorkspaceTab('training')}>调整模型范围</Button></p>}
          {draft.searchRoi && <Button size="small" onClick={() => changeParameter('searchRoi', undefined)} disabled={locked}>清除搜索区域（恢复全图搜索）</Button>}
          {!trainedModelData && !legacyWorkflow && <Alert showIcon type="warning" title="尚未训练模型" description="先建立模型，即可在当前窗口运行搜索；无需先应用或关闭窗口。" />}
          {trainedModelData && trainingStale && <Alert showIcon type="warning" title="训练参数已变化，需要重新训练" description={`已修改：${changedTrainingLabels}。`} />}
          {shapeSearchBlocked && <Button disabled={locked} onClick={() => changeShapeWorkspaceTab('training')}>前往模型训练</Button>}
        </div>}
        {isImageCrop && <div className="shape-training-panel crop-settings-panel">
          <strong>选择裁剪区域</strong>
          <p>在输入图像上绘制矩形、圆形、多边形或笔刷选区；笔刷支持添加和擦除，输出图像按选区紧包围框裁剪，选区外清零；ROI 蒙版端口输出二值选区。裁剪后原点移到新图左上角，可将“变换”端口连接到下游测量节点以换算原图坐标。</p>
          {cropRoiInputConnected && <Alert showIcon type="info" title={`裁剪区域来自上游：${nodeLabels[cropRoiSourceId!] ?? cropRoiSourceId}`}
            description="已连接的 roi 输入会覆盖本节点的本地选区。请在上游 ROI 节点编辑；本地选区不会参与本次裁剪。"
            style={{ marginBlock: 10 }} />}
          {!cropRoiInputConnected && draft.roi && <Button size="small" disabled={locked} onClick={() => changeCropRoi(undefined)}>清除选区（使用整图）</Button>}
          <Button type="primary" size="small" onClick={() => void runPreview()} loading={previewBusy}
            disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason)}
            title={previewBlockedReason || '按当前 ROI 预览裁剪图像与 ROI 蒙版'}>预览裁剪结果</Button>
        </div>}
        {issues.length > 0 && <Alert type="error" title="请修正参数后再运行或应用" description={issues.join('；')} />}
        <PropertyPanel node={draftNode} catalogItem={shapePanelCatalogItem} onChange={changeParameter} disabled={locked || referenceMismatch}
          roiFields={roiFields} onLocateRoi={canLocateRoiInImage ? locateRoi : undefined} />
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
          <Tooltip title={previewBlockedReason || (isShapeMatch && shapeWorkspaceTab === 'training' ? '使用当前显示的训练图像建立模型（Ctrl+Enter）' : shapeSearchBlocked ? '请先训练或重新训练模型' : '使用暂存参数测试此节点，并预热上游输入；不写回流程参数。（Ctrl+Enter）')}>
            <Button aria-label={isShapeMatch ? shapeWorkspaceTab === 'training' ? legacyWorkflow ? '训练并切换到训练模式' : trainedModelData ? '重新训练模型' : '训练模型' : '运行搜索' : '运行此节点'}
              onClick={() => void (isShapeMatch && shapeWorkspaceTab === 'training' ? trainShapeModel() : runPreview())} loading={isShapeMatch && shapeWorkspaceTab === 'training' ? trainingBusy : previewBusy}
              disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || (shapeWorkspaceTab !== 'training' && shapeSearchBlocked) || (isShapeMatch && shapeWorkspaceTab === 'training' && !hasLosslessTrainingImage) || referenceMismatch}>
              {isShapeMatch ? shapeWorkspaceTab === 'training' ? legacyWorkflow ? '训练并切换到训练模式' : trainedModelData ? '重新训练模型' : '训练模型' : '运行搜索' : '运行此节点'}
            </Button>
          </Tooltip>
          <Button onClick={onClose}>取消</Button><Button type="primary" onClick={apply} disabled={locked || !dirty || issues.length > 0 || externalChange || referenceMismatch || isShapeMatch && !legacyWorkflow && trainingStale} title="应用暂存参数到流程（Ctrl+S）">应用</Button>
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
            <NodeExecutionResult nodeId={node.id} catalog={catalogItem} result={isShapeMatch ? searchPreview?.result ?? result : displayedResult}
              receivedAt={isShapeMatch ? searchPreview?.receivedAt ?? resultReceivedAt : receivedAt}
              stale={isShapeMatch ? matchStale : stale} draftResult={isShapeMatch ? Boolean(searchPreview) : Boolean(preview)}
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
