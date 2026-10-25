import MetrologyObjectEditor from './MetrologyObjectEditor';
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import type { Edge, Node } from '@xyflow/react';
import { Alert, Button, Collapse, Modal, Select, Segmented, Space, Tabs, Tag, Tooltip } from 'antd';
import type { NodeCatalogItem, NodeFrameRule, PortDescriptor, RunResult, VisionOverlay, VisionRoi, VisionValueSnapshot } from '../types';
import NodeCategoryIcon from './NodeCategoryIcon';
import NodeDescription from './NodeDescription';
import PropertyPanel from './PropertyPanel';
import MatchingAutoTunePanel from './MatchingAutoTunePanel';
import ImageViewer, { buildCaliperCircleAngles, type MeasurementGeometry } from './ImageViewer';
import NodeExecutionResult from './NodeExecutionResult';
import CaliperCircleQuickSettings, { caliperCircleQuickParameterNames } from './CaliperCircleQuickSettings';
import CaliperCircleDiagnosticsPanel from './CaliperCircleDiagnosticsPanel';
import { useCaliperCircleDiagnostics } from '../caliperCircleDiagnostics';
import CaliperEdgeQuickSettings, { caliperEdgeQuickParameterNames } from './CaliperEdgeQuickSettings';
import CaliperEdgeDiagnosticsPanel from './CaliperEdgeDiagnosticsPanel';
import { caliperProfilePoint, useCaliperEdgeDiagnostics } from '../caliperEdgeDiagnostics';
import SplitHandle from './SplitHandle';
import SwitchCaseEditor from './SwitchCaseEditor';
import { runImageEntryForPort, runImagePortLabel, useRunImages } from '../useRunImages';
import { cloneParameters, configurationIssues, configurationParameters, cropRoiOutsideImageIssue, measurementRoiOutsideImageIssue, parameterFingerprint, withParameter, type NodeParameters } from '../nodeConfigurationState';
import { capturedReference, imageFrameFingerprint, poseSourceFingerprint } from '../roiReference';
import { withSwitchPorts } from '../switchPorts';
import { legacyNodeReplacement } from '../legacyNodes';
import { canonicalParameterGroup, portDisplayName } from '../i18n/catalog';
import { fallbackCatalogRaw } from '../catalog';
import { effectiveSampleWidth, normalizeCaliperCircleArc } from '../caliperCircleSampling';
import { catalogItemChangesImageFrame, declaredFrameChange, legacyFrameChangingNodeTypes } from '../frameSemantics';
import { useShapeMatchWorkspace } from './useShapeMatchWorkspace';
import {
  connectedCircleGeometry, connectedSearchLineGeometry, geometrySourceImageFrameNodeIds,
  mapMeasurementGeometry, parameterMeta, rigidPixelTransform, roiSourceFrameNodeIdsForEdge,
  transformEndpointImageFrameNodeIds, transformMappedImageFrameNodeIds, transformSourceLineage,
  type RigidPixelTransform
} from './nodeConfigurationGeometry';

export type NodeConfigurationTab = 'parameters' | 'image' | 'details' | 'result';

import { runs } from '../endpoints';
import {
  clipLineToImage,
  connectedRoiGeometry,
  hasInputEdge,
  imageFrameChanges,
  imageFrameDescendsFrom,
  imageFramePreservingNodeTypes,
  imageFramesSharePixelPreservingAncestor,
  imageSourceMatchesPort,
  inputEdge,
  inputSnapshotValue,
  legacyCropRectangle,
  outputPortMatches
} from './nodeConfigurationUtils';

type Props = {
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

function PortTable({ ports, input }: { ports: PortDescriptor[]; input?: boolean }) {
  if (!ports.length) return <p className="node-info-muted">无{input ? '输入' : '输出'}端口</p>;
  return <div className="node-info-table-wrap"><table className="node-info-table">
    <thead><tr><th>端口</th><th>数据类型</th>{input && <th>连接要求</th>}</tr></thead>
    <tbody>{ports.map((port) => <tr key={port.name}>
      <td title={port.label && port.label !== port.name ? port.name : undefined}>{portDisplayName(port.name, port.label)}</td>
      <td>{port.dataType}</td>
      {input && <td>{port.required ? '必需' : port.requiredWhen ? '所选操作需要时必需' : '可选'}</td>}
    </tr>)}</tbody>
  </table></div>;
}

// 几何/图纯函数（帧血缘、几何解析、刚体像素变换映射）见 ./nodeConfigurationGeometry（巨型组件拆分，2026-10-23）。

export default function NodeConfigurationDialog({ node, catalogItem, catalogItems = fallbackCatalogRaw, nodes, edges, open, disabled, onClose, onApply, onPreview, previewBlockedReason, initialTab = 'parameters', result, resultReceivedAt, resultStale = false }: Props) {
  const cropMaskFramePreservingNodeTypes = useMemo(() => imageFramePreservingNodeTypes(catalogItems), [catalogItems]);
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
  const [baseline] = useState(() => configurationParameters((node.data.parameters ?? {}) as NodeParameters, catalogItem));
  const [draft, setDraft] = useState(() => cloneParameters(baseline));
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
  const dirty = draftFingerprint !== parameterFingerprint(baseline);
  // 与打开弹窗时的值不同的参数项数：用于参数区“已改 N 项”与关闭确认
  const changedCount = [...new Set([...Object.keys(baseline), ...Object.keys(draft)])]
    .filter((key) => parameterFingerprint(draft[key]) !== parameterFingerprint(baseline[key])).length;
  const [discardOpen, setDiscardOpen] = useState(false);
  const externalChange = parameterFingerprint(configurationParameters((node.data.parameters ?? {}) as NodeParameters, catalogItem)) !== parameterFingerprint(baseline);
  const locked = disabled || previewBusy;
  const roiInputEdge = inputEdge(edges, node.id, 'roi');
  const measurementNodeTypes = ['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperWidth', 'measure.caliperLine', 'measure.caliperArray', 'measure.caliperCircle'];
  const measurementImageInputEdge = measurementNodeTypes.includes(catalogItem.type)
    ? inputEdge(edges, node.id, 'image')
    : undefined;
  const measurementImageSource = nodes.find((item) => item.id === measurementImageInputEdge?.source);
  const roiSourceMayMaskCropBounds = (sourceId: string, visited = new Set<string>()): boolean => {
    if (visited.has(sourceId)) return true;
    visited.add(sourceId);
    const source = nodes.find((item) => item.id === sourceId);
    if (source?.data.typeKey === 'roi.transform') return true;
    if (source?.data.typeKey !== 'image.crop') return true;
    const sourceRoiEdge = inputEdge(edges, sourceId, 'roi');
    if (sourceRoiEdge) return roiSourceMayMaskCropBounds(sourceRoiEdge.source, visited);
    const sourceRoi = (source.data.parameters as NodeParameters | undefined)?.roi as VisionRoi | undefined;
    return sourceRoi ? sourceRoi.type !== 'Rectangle' : false;
  };
  const cropMaskSources = (() => {
    const sources: { node: Node; maskIsInCurrentFrame: boolean }[] = [];
    const visited = new Set<string>();
    let current = measurementImageSource;
    let cropsAfterCurrent = 0;
    let hasCoordinateChange = false;
    while (current && !visited.has(current.id)) {
      const currentNode = current;
      visited.add(currentNode.id);
      const type = String(currentNode.data.typeKey ?? '').toLowerCase();
      if (type === 'image.crop') {
        const cropRoiEdge = inputEdge(edges, currentNode.id, 'roi');
        const cropRoi = (currentNode.data.parameters as NodeParameters | undefined)?.roi as VisionRoi | undefined;
        const masksOutsideSelection = cropRoiEdge
          ? roiSourceMayMaskCropBounds(cropRoiEdge.source)
          : Boolean(cropRoi && cropRoi.type !== 'Rectangle');
        if (masksOutsideSelection)
          sources.push({ node: currentNode, maskIsInCurrentFrame: cropsAfterCurrent === 0 && !hasCoordinateChange });
        cropsAfterCurrent++;
      } else if (imageFrameChanges(currentNode)) hasCoordinateChange = true;
      else if (type !== 'roi.transform' && (!cropMaskFramePreservingNodeTypes.has(type) || imageFrameChanges(currentNode))) break;
      const imageInputPort = type === 'roi.transform' ? 'debugImage' : 'image';
      const imageInput = inputEdge(edges, currentNode.id, imageInputPort);
      current = imageInput ? nodes.find((item) => item.id === imageInput.source) : undefined;
    }
    return sources;
  })();
  const directCropMaskSource = cropMaskSources.length === 1 && cropMaskSources[0].maskIsInCurrentFrame
    ? cropMaskSources[0].node : undefined;
  const croppedRoiConnected = Boolean(directCropMaskSource
    && outputPortMatches(roiInputEdge, directCropMaskSource.id, 'croppedRoi'));
  const croppedImageNeedsRoiWarning = Boolean(measurementNodeTypes.includes(catalogItem.type)
    && cropMaskSources.length > 0 && !croppedRoiConnected);
  const connectedInputPorts = catalogItem.inputs.filter((input) => hasInputEdge(edges, node.id, input.name)).map((input) => input.name);
  const connectedRoiInput = Boolean(roiInputEdge && connectedInputPorts.some((port) => port.toLowerCase() === 'roi'));
  const isShapeMatch = catalogItem.type === 'match.shape';
  const isImageCrop = catalogItem.type === 'image.crop';
  const isPerspective = catalogItem.type === 'image.perspective';
  const isRoiTransform = catalogItem.type === 'roi.transform';
  const isCaliperCircle = catalogItem.type === 'measure.caliperCircle';
  const isCaliperEdge = ['measure.caliper', 'feature.edgeSubpixel'].includes(catalogItem.type);
  const isMetrology = catalogItem.type === 'measure.metrology';
  const legacyReplacementType = legacyNodeReplacement(catalogItem.type);
  const legacyReplacement = legacyReplacementType ? catalogItems.find((item) => item.type === legacyReplacementType) : undefined;
  const roiImageInputPort = isRoiTransform ? 'debugImage'
    : ['image.crop', 'image.perspective', 'region.fromRoi', ...measurementNodeTypes].includes(catalogItem.type) ? 'image' : undefined;
  const roiImageInputEdge = roiImageInputPort ? inputEdge(edges, node.id, roiImageInputPort) : undefined;
  const roiSourceImageFrameNodeIds = roiSourceFrameNodeIdsForEdge(roiInputEdge, nodes, edges, catalogItems);
  const roiImageInputSourceNode = nodes.find((item) => item.id === roiImageInputEdge?.source);
  const connectedRoiImageFrameMismatch = Boolean(roiSourceImageFrameNodeIds.length && roiImageInputEdge
    && roiSourceImageFrameNodeIds.some((roiFrameNodeId) =>
      !imageFramesSharePixelPreservingAncestor(roiFrameNodeId, roiImageInputEdge.source, nodes, edges, cropMaskFramePreservingNodeTypes)));
  const connectedRoiImageFrameMismatchMessage = connectedRoiImageFrameMismatch
    ? `上游 ROI 与目标图像“${String(roiImageInputSourceNode?.data.label ?? roiImageInputEdge!.source)}”不在同一像素坐标系。请将 ROI 与图像接到同一裁剪/变换分支；若目标图像来自裁剪节点，请连接该裁剪节点的 croppedRoi 输出。当前已阻止测试运行。`
    : undefined;
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
  const searchLineInputEdge = ['measure.caliper', 'measure.caliperWidth', 'measure.caliperLine', 'measure.caliperArray', 'feature.edgeSubpixel'].includes(catalogItem.type)
    ? inputEdge(edges, node.id, 'searchLine')
    : undefined;
  const circleInputEdge = isCaliperCircle
    ? inputEdge(edges, node.id, 'circle')
    : undefined;
  const circleSourceNode = circleInputEdge ? nodes.find((item) => item.id === circleInputEdge.source) : undefined;
  const transformInputEdge = ['measure.caliper', 'measure.caliperWidth', 'measure.caliperLine', 'measure.caliperArray', 'measure.caliperCircle', 'feature.edgeSubpixel'].includes(catalogItem.type)
    ? inputEdge(edges, node.id, 'transform')
    : undefined;
  const transformLineageNodes = transformSourceLineage(nodes, edges, transformInputEdge?.source);
  const connectedGeometryEdge = circleInputEdge ?? searchLineInputEdge;
  const connectedGeometryImageFrameNodeIds = geometrySourceImageFrameNodeIds(connectedGeometryEdge, nodes, edges, catalogItems);
  const transformSourceImageFrames = transformInputEdge
    ? transformEndpointImageFrameNodeIds(transformInputEdge.source, 'source', nodes, edges) : [];
  const transformMappedImageFrames = transformMappedImageFrameNodeIds(transformLineageNodes, edges);
  const connectedGeometryAlreadyInMappedFrame = Boolean(connectedGeometryImageFrameNodeIds.length && transformInputEdge
    && connectedGeometryImageFrameNodeIds.some((geometryFrameNodeId) => transformMappedImageFrames.some((mappedFrameNodeId) =>
      imageFramesSharePixelPreservingAncestor(geometryFrameNodeId, mappedFrameNodeId, nodes, edges, cropMaskFramePreservingNodeTypes))));
  const connectedGeometryTransformSourceFrameMismatch = Boolean(connectedGeometryImageFrameNodeIds.length && transformSourceImageFrames.length
    && connectedGeometryImageFrameNodeIds.some((geometryFrameNodeId) => transformSourceImageFrames.every((sourceFrameNodeId) =>
      !imageFramesSharePixelPreservingAncestor(geometryFrameNodeId, sourceFrameNodeId, nodes, edges, cropMaskFramePreservingNodeTypes))));
  const connectedGeometryImageFrameMismatch = Boolean(connectedGeometryImageFrameNodeIds.length && !transformInputEdge && measurementImageInputEdge
    && connectedGeometryImageFrameNodeIds.some((geometryFrameNodeId) =>
      !imageFramesSharePixelPreservingAncestor(geometryFrameNodeId, measurementImageInputEdge.source, nodes, edges, cropMaskFramePreservingNodeTypes)));
  const directCorrectedCircleTransformConflict = isCaliperCircle && Boolean(circleInputEdge && transformInputEdge
    && circleSourceNode?.data.typeKey === 'roi.transform'
    && transformLineageNodes.some((source) => source.id === circleInputEdge.source));
  const correctedCircleTransformConflict = directCorrectedCircleTransformConflict
    || isCaliperCircle && connectedGeometryAlreadyInMappedFrame;
  const correctedSearchLineTransformConflict = Boolean(searchLineInputEdge && transformInputEdge && connectedGeometryAlreadyInMappedFrame);
  const connectedCircleImageFrameMismatch = Boolean(circleInputEdge && connectedGeometryImageFrameMismatch);
  const connectedSearchLineImageFrameMismatch = Boolean(searchLineInputEdge && connectedGeometryImageFrameMismatch);
  const connectedCircleTransformSourceMismatch = Boolean(circleInputEdge && connectedGeometryTransformSourceFrameMismatch);
  const connectedSearchLineTransformSourceMismatch = Boolean(searchLineInputEdge && connectedGeometryTransformSourceFrameMismatch);
  const measurementGeometryTransformConflict = correctedCircleTransformConflict || correctedSearchLineTransformConflict;
  const measurementGeometryFrameMismatchConflict = connectedCircleImageFrameMismatch || connectedSearchLineImageFrameMismatch
    || connectedCircleTransformSourceMismatch || connectedSearchLineTransformSourceMismatch;
  const measurementGeometryConflict = measurementGeometryTransformConflict || measurementGeometryFrameMismatchConflict;
  const correctedCircleTransformMessage = correctedCircleTransformConflict
    ? directCorrectedCircleTransformConflict
      ? '标称圆和 transform 都来自同一个定位修正 ROI。圆已经是当前图像坐标，请断开这个 transform 输入，避免重复位移；当前已阻止测试运行。'
      : '上游圆已经处于 transform 的目标图像坐标系，再传入 transform 会重复映射。请断开 transform，或改接基准图像坐标的圆；当前已阻止测试运行。'
    : undefined;
  const correctedSearchLineTransformMessage = correctedSearchLineTransformConflict
    ? '上游搜索线已经处于 transform 的目标图像坐标系，再传入 transform 会重复映射。请断开 transform，或改接基准图像坐标的搜索线；当前已阻止测试运行。'
    : undefined;
  const circleImageFrameMismatchMessage = connectedCircleImageFrameMismatch
    ? '上游圆与当前测量图像不在同一像素坐标系。请将圆和图像接到同一裁剪/变换分支，或为基准图像圆连接匹配的 transform；当前已阻止测试运行。'
    : undefined;
  const searchLineImageFrameMismatchMessage = connectedSearchLineImageFrameMismatch
    ? '上游搜索线与当前测量图像不在同一像素坐标系。请将线和图像接到同一裁剪/变换分支，或为基准图像线连接匹配的 transform；当前已阻止测试运行。'
    : undefined;
  const circleTransformSourceMismatchMessage = connectedCircleTransformSourceMismatch
    ? '上游圆与 transform 的源图像坐标系不匹配。请连接来自 transform 源图像（或同坐标处理分支）的圆；当前已阻止测试运行。'
    : undefined;
  const searchLineTransformSourceMismatchMessage = connectedSearchLineTransformSourceMismatch
    ? '上游搜索线与 transform 的源图像坐标系不匹配。请连接来自 transform 源图像（或同坐标处理分支）的线；当前已阻止测试运行。'
    : undefined;
  const measurementGeometryConflictMessage = correctedCircleTransformMessage ?? correctedSearchLineTransformMessage
    ?? circleTransformSourceMismatchMessage ?? searchLineTransformSourceMismatchMessage
    ?? circleImageFrameMismatchMessage ?? searchLineImageFrameMismatchMessage;
  const referenceTransformNode = transformLineageNodes.find((source) => source.data.typeKey === 'roi.transform'
    && typeof (source.data.parameters as NodeParameters | undefined)?.referenceImage === 'string'
    && Boolean((source.data.parameters as NodeParameters).referenceImage));
  const referenceTransformParameters = (referenceTransformNode?.data.parameters ?? {}) as NodeParameters;
  const savedMeasurementReference = referenceTransformNode
    ? { data: String(referenceTransformParameters.referenceImage), width: Number(referenceTransformParameters.referenceWidth ?? 0),
      height: Number(referenceTransformParameters.referenceHeight ?? 0) }
    : undefined;
  const cropTransformNode = transformLineageNodes.find((source) => source.data.typeKey === 'image.crop');
  const cropMeasurementReferenceEdge = cropTransformNode
    ? inputEdge(edges, cropTransformNode.id, 'image')
    : undefined;
  const cropMeasurementReferenceNodeId = cropMeasurementReferenceEdge?.source;
  const cropMeasurementReferencePortName = cropMeasurementReferenceEdge?.sourceHandle ?? 'image';
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
    if (!preview || preview.preserveTrainingWorkspace) return;
    setResultExpanded(true);
    const frame = requestAnimationFrame(() => resultRef.current?.scrollIntoView?.({ block: 'nearest' }));
    return () => cancelAnimationFrame(frame);
  }, [preview]);
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
      ? legacyCropRectangle(draft, imageViewerSource.width, imageViewerSource.height) : undefined)
    : undefined;
  const issues = configurationIssues(catalogItem, draft, { connectedInputPorts });
  // match.shape 工作区（按节点类型分块）：训练/搜索页签状态、模型训练与图像获取处理器。
  const {
    shapeWorkspaceTab, setShapeWorkspaceTab, changeShapeWorkspaceTab,
    shapeImageKind, setShapeImageKind,
    trainingView, setTrainingView,
    showSavedTrainingImage, setShowSavedTrainingImage,
    trainingSamples, setTrainingSamples,
    searchWorkspacePreview, setSearchWorkspacePreview,
    trainingBusy, trainingImageBusy, searchImageBusy,
    trainedModelData, trainedSourceImage, trainedPreviewData, legacyWorkflow, trainedSettings,
    trainingStale, shapeSearchBlocked, capturedTrainingImage, hasLosslessTrainingImage,
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
    const imageExtentIssue = cropRoiOutsideImageIssue(cropRoiForImage, imageViewerSource.width, imageViewerSource.height);
    if (imageExtentIssue) issues.push(imageExtentIssue);
  }
  if (measurementNodeTypes.includes(catalogItem.type) && !connectedRoiInput && !resultStale && imageSource === 'input'
    && previewNodeId === node.id && selectedInputAvailable) {
    const imageExtentIssue = measurementRoiOutsideImageIssue(draft.roi, imageViewerSource.width, imageViewerSource.height);
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
  const geometryNumber = (key: string, fallback: number) => Number.isFinite(Number(draft[key])) ? Number(draft[key]) : fallback;
  const measurementGeometry: MeasurementGeometry | undefined = (canEditMeasurementGeometry || canDisplayConnectedSearchLine) && measurementImageFrameReady
    ? measurementGeometryType === 'line'
      ? { type: 'line', start: connectedSearchLine?.start ?? { x: geometryNumber('startX', 120), y: geometryNumber('startY', 240) },
        end: connectedSearchLine?.end ?? { x: geometryNumber('endX', 520), y: geometryNumber('endY', 240) },
        profileWidth: effectiveSampleWidth(geometryNumber('sampleWidth', 5)), direction: String(draft.direction ?? 'strongest'),
        ...(catalogItem.type === 'measure.caliperLine' || catalogItem.type === 'measure.caliperArray' ? { profileSpan: Math.max(0, geometryNumber('edgeLength', 0)),
          profileCount: geometryNumber('caliperCount', 9) } : {}) }
      : { type: 'circle', center: connectedCircle ? { x: connectedCircle.x, y: connectedCircle.y }
          : { x: geometryNumber('centerX', 320), y: geometryNumber('centerY', 240) },
        radius: connectedCircle?.radius ?? Math.max(0, geometryNumber('radius', 100)), band: Math.max(0, geometryNumber('radialBand', 8)),
        angleStartDeg: geometryNumber('angleStartDeg', -180), angleEndDeg: geometryNumber('angleEndDeg', 180),
        caliperCount: geometryNumber('caliperCount', 72), samplingMode: String(draft.samplingMode ?? 'count'),
        angleStepDeg: geometryNumber('angleStepDeg', 5), profileWidth: effectiveSampleWidth(geometryNumber('sampleWidth', 5)),
        searchDirection: String(draft.searchDirection ?? 'strongest'),
        centerEditable: !circleInputEdge, radiusEditable: !circleInputEdge }
    : undefined;
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
  const circleSamplingMode = String(draft.samplingMode ?? (draft.angleStepDeg !== undefined ? 'step' : 'count')).toLowerCase();
  const circleSamplingGeometry: Extract<MeasurementGeometry, { type: 'circle' }> | undefined = isCaliperCircle ? {
    type: 'circle', center: { x: geometryNumber('centerX', 320), y: geometryNumber('centerY', 240) },
    radius: geometryNumber('radius', 100),
    angleStartDeg: geometryNumber('angleStartDeg', -180), angleEndDeg: geometryNumber('angleEndDeg', 180),
    samplingMode: circleSamplingMode,
    caliperCount: geometryNumber('caliperCount', 72), angleStepDeg: geometryNumber('angleStepDeg', 5)
  } : undefined;
  const circleSamplingAngles = circleSamplingGeometry ? buildCaliperCircleAngles(circleSamplingGeometry) : [];
  const circleSamplingNotice = (() => {
    if (!circleSamplingGeometry || circleSamplingMode !== 'step') return undefined;
    const requestedStep = Math.max(0.5, Math.min(45, geometryNumber('angleStepDeg', 5)));
    const angles = circleSamplingAngles;
    if (angles.length < 2) return undefined;
    const effectiveStep = angles[1] - angles[0];
    if (effectiveStep <= requestedStep + 1e-9) return undefined;
    const stepText = Number(effectiveStep.toFixed(3)).toString();
    return `此圆弧按 ${requestedStep}° 步长会超过卡尺数量上限；预览和执行将使用 ${angles.length} 条卡尺，实际步长约 ${stepText}°。如需精确控制数量，可切换到计数模式。`;
  })();
  const circleFitConditionNotice = (() => {
    const angles = circleSamplingAngles;
    if (!circleSamplingGeometry || angles.length < 3) return undefined;
    const points = angles.map(angle => angle * Math.PI / 180);
    const centerX = points.reduce((sum, angle) => sum + Math.cos(angle), 0) / points.length;
    const centerY = points.reduce((sum, angle) => sum + Math.sin(angle), 0) / points.length;
    let xx = 0; let yy = 0; let xy = 0;
    for (const angle of points) {
      const dx = Math.cos(angle) - centerX;
      const dy = Math.sin(angle) - centerY;
      xx += dx * dx; yy += dy * dy; xy += dx * dy;
    }
    const spread = xx + yy;
    const normalizedDeterminant = spread > 0 ? (xx * yy - xy * xy) / (spread * spread) : 0;
    // Keep the authoring preview aligned with the backend's minimum normalized circle-fit spread.
    if (normalizedDeterminant > 2e-3) return undefined;
    return '当前圆弧的理论测点分布接近直线，可能触发退化圆拟合。请扩大角度覆盖；单纯增加卡尺数量不能替代更宽的圆弧。';
  })();
  const caliperCircleBoundsNotice = (() => {
    const geometry = mappedMeasurementGeometry;
    if (catalogItem.type !== 'measure.caliperCircle' || geometry?.type !== 'circle'
      || !measurementImageFrameReady || measurementBoundsWidth === undefined || measurementBoundsHeight === undefined
      || measurementBoundsWidth <= 0 || measurementBoundsHeight <= 0
      || !Number.isFinite(geometry.center.x) || !Number.isFinite(geometry.center.y)
      || Math.abs(geometry.center.x) > 10_000_000 || Math.abs(geometry.center.y) > 10_000_000
      || !Number.isFinite(geometry.radius) || geometry.radius <= 0 || geometry.radius > 10_000_000) return undefined;
    const width = measurementBoundsWidth;
    const height = measurementBoundsHeight;
    const band = Math.max(1, Math.min(500, geometryNumber('radialBand', 8)));
    const innerRadius = Math.max(0, geometry.radius - band);
    const outerRadius = geometry.radius + band;
    const angles = buildCaliperCircleAngles(geometry);
    let outside = 0;
    let tooShortAfterClip = 0;
    for (const angle of angles) {
      const radians = angle * Math.PI / 180;
      const direction = { x: Math.cos(radians), y: Math.sin(radians) };
      const start = { x: geometry.center.x + direction.x * innerRadius,
        y: geometry.center.y + direction.y * innerRadius };
      const end = { x: geometry.center.x + direction.x * outerRadius,
        y: geometry.center.y + direction.y * outerRadius };
      const clipped = clipLineToImage(start, end, width, height);
      if (!clipped) { outside++; continue; }
      if (Math.hypot(clipped.end.x - clipped.start.x, clipped.end.y - clipped.start.y) < 2) tooShortAfterClip++;
    }
    if (outside === 0 && tooShortAfterClip === 0) return undefined;
    const minimum = Math.max(3, Math.min(361, Math.floor(geometryNumber('minValidCalipers', 3) + 0.5)));
    const potentiallyAvailable = angles.length - outside - tooShortAfterClip;
    const detail = [
      outside > 0 ? `${outside}/${angles.length} 条径向剖面完全位于图像外` : '',
      tooShortAfterClip > 0 ? `${tooShortAfterClip} 条径向剖面裁切后短于 2 px` : ''
    ].filter(Boolean).join('，');
    return {
      type: potentiallyAvailable < minimum ? 'warning' as const : 'info' as const,
      title: potentiallyAvailable < minimum ? '图像边界可能导致圆卡尺不足' : '部分圆卡尺位于图像边界外',
      description: `${measurementBoundsContext}${detail}；仅按图像边界估算，最多剩余 ${potentiallyAvailable} 条可采样，当前拟合至少需要 ${minimum} 条有效卡尺。ROI 掩膜、对比度阈值和边缘方向还可能继续减少有效点。请调整标称圆位置、半径或搜索带。`
    };
  })();
  const caliperLineBoundsNotice = (() => {
    const geometry = mappedMeasurementGeometry;
    if (!['measure.caliperLine', 'measure.caliperArray'].includes(catalogItem.type) || geometry?.type !== 'line'
      || !measurementImageFrameReady || measurementBoundsWidth === undefined || measurementBoundsHeight === undefined
      || measurementBoundsWidth <= 0 || measurementBoundsHeight <= 0) return undefined;
    const dx = geometry.end.x - geometry.start.x;
    const dy = geometry.end.y - geometry.start.y;
    const length = Math.hypot(dx, dy);
    if (length < 2) return undefined;
    const minimumCount = catalogItem.type === 'measure.caliperArray' ? 1 : 3;
    const count = Math.max(minimumCount, Math.min(101, Math.floor(geometryNumber('caliperCount', 9) + 0.5)));
    const requestedSpan = geometryNumber('edgeLength', 0);
    const span = requestedSpan > 0 ? Math.max(0, Math.min(10000, requestedSpan)) : length;
    const tangent = { x: -dy / length, y: dx / length };
    let outside = 0;
    let tooShortAfterClip = 0;
    for (let index = 0; index < count; index++) {
      const offset = (index / (count - 1) - 0.5) * span;
      const delta = { x: tangent.x * offset, y: tangent.y * offset };
      const clipped = clipLineToImage(
        { x: geometry.start.x + delta.x, y: geometry.start.y + delta.y },
        { x: geometry.end.x + delta.x, y: geometry.end.y + delta.y },
        measurementBoundsWidth, measurementBoundsHeight
      );
      if (!clipped) { outside++; continue; }
      if (Math.hypot(clipped.end.x - clipped.start.x, clipped.end.y - clipped.start.y) < 2) tooShortAfterClip++;
    }
    if (outside === 0 && tooShortAfterClip === 0) return undefined;
    const minimum = Math.max(3, Math.min(101, Math.floor(geometryNumber('minValidCalipers', 3) + 0.5)));
    const potentiallyAvailable = count - outside - tooShortAfterClip;
    const detail = [
      outside > 0 ? `${outside}/${count} 条剖面完全位于图像外` : '',
      tooShortAfterClip > 0 ? `${tooShortAfterClip} 条剖面裁切后短于 2 px` : ''
    ].filter(Boolean).join('，');
    return {
      type: potentiallyAvailable < minimum ? 'warning' as const : 'info' as const,
      title: potentiallyAvailable < minimum ? '图像边界可能导致卡尺数量不足' : '部分卡尺位于图像边界外',
      description: `${measurementBoundsContext}${detail}；仅按图像边界估算，最多剩余 ${potentiallyAvailable} 条可采样，当前拟合至少需要 ${minimum} 条有效卡尺。ROI 掩膜、对比度阈值和边缘方向还可能继续减少有效点。请缩短跨度或调整搜索线位置。`
    };
  })();
  const caliperSearchLineBoundsNotice = (() => {
    const geometry = mappedMeasurementGeometry;
    if (!['measure.caliper', 'measure.caliperWidth'].includes(catalogItem.type) || geometry?.type !== 'line'
      || !measurementImageFrameReady || measurementBoundsWidth === undefined || measurementBoundsHeight === undefined
      || measurementBoundsWidth <= 0 || measurementBoundsHeight <= 0) return undefined;
    const length = Math.hypot(geometry.end.x - geometry.start.x, geometry.end.y - geometry.start.y);
    if (length < 2) return undefined;
    const clipped = clipLineToImage(geometry.start, geometry.end, measurementBoundsWidth, measurementBoundsHeight);
    if (!clipped) return {
      type: 'warning' as const,
      title: '搜索线完全位于图像外',
      description: `${measurementBoundsContext}当前卡尺不会采到图像像素，运行会返回“未找到”而不是检测边缘。请将搜索线移入图像，或检查上游坐标变换。`
    };
    const clippedLength = Math.hypot(clipped.end.x - clipped.start.x, clipped.end.y - clipped.start.y);
    if (clippedLength < 2) return {
      type: 'warning' as const,
      title: '搜索线裁切后过短',
      description: `${measurementBoundsContext}搜索线与图像仅相交 ${clippedLength.toFixed(1)} px，短于卡尺要求的 2 px。请调整搜索线位置或方向。`
    };
    if (clippedLength >= length - 1e-6) return undefined;
    return {
      type: 'info' as const,
      title: '搜索线将按图像边界裁切',
      description: `${measurementBoundsContext}搜索线约有 ${(length - clippedLength).toFixed(1)} px 位于图像外；运行时只会在图像内的 ${clippedLength.toFixed(1)} px 段上采样。`
    };
  })();
  const measurementGeometryParameters = (current: NodeParameters, geometry: MeasurementGeometry,
    options?: { preserveValues?: boolean }): NodeParameters => {
    if (geometry.type === 'line') return { ...current, startX: geometry.start.x, startY: geometry.start.y,
      endX: geometry.end.x, endY: geometry.end.y,
      ...(geometry.profileWidth !== undefined ? { sampleWidth: geometry.profileWidth } : {}),
      ...((catalogItem.type === 'measure.caliperLine' || catalogItem.type === 'measure.caliperArray') && geometry.profileSpan !== undefined ? { edgeLength: geometry.profileSpan } : {}) };
    const startAngle = geometry.angleStartDeg ?? -180;
    const endAngle = geometry.angleEndDeg ?? 180;
    const normalizedArc = normalizeCaliperCircleArc(startAngle, endAngle);
    return { ...current, ...(circleInputEdge ? {} : { centerX: geometry.center.x, centerY: geometry.center.y, radius: geometry.radius }),
      ...(geometry.profileWidth !== undefined ? { sampleWidth: geometry.profileWidth } : {}),
      angleStartDeg: options?.preserveValues ? startAngle : normalizedArc.start,
      angleEndDeg: options?.preserveValues ? endAngle : normalizedArc.end,
      radialBand: options?.preserveValues ? geometry.band ?? 0 : Math.max(1, Math.min(500, geometry.band ?? 1)) };
  };
  const updateMeasurementGeometry = (geometry: MeasurementGeometry, options?: { preserveValues?: boolean }) => {
    if (locked || !canEditMeasurementGeometry || !measurementImageFrameReady || showCurrentMeasurementImage) return;
    setDraft(current => measurementGeometryParameters(current, geometry, options));
  };
  const fitMeasurementGeometryToImage = () => {
    if (locked || !measurementGeometry || !measurementImageFrameReady || showCurrentMeasurementImage) return;
    const width = measurementGeometryFrameKind === 'saved-reference' ? savedMeasurementReference?.width : imageViewerSource.width;
    const height = measurementGeometryFrameKind === 'saved-reference' ? savedMeasurementReference?.height : imageViewerSource.height;
    if (!width || !height || !Number.isFinite(width) || !Number.isFinite(height)) return;
    if (measurementGeometry.type === 'line') {
      const dx = measurementGeometry.end.x - measurementGeometry.start.x;
      const dy = measurementGeometry.end.y - measurementGeometry.start.y;
      const currentLength = Math.hypot(dx, dy);
      const direction = currentLength > 1e-6 ? { x: dx / currentLength, y: dy / currentLength } : { x: 1, y: 0 };
      const availableLength = Math.min(width / Math.max(Math.abs(direction.x), 1e-6),
        height / Math.max(Math.abs(direction.y), 1e-6));
      const length = Math.max(2, availableLength * 0.625);
      const center = { x: width / 2, y: height / 2 };
      const tangent = { x: -direction.y, y: direction.x };
      const availableSpan = Math.min(width / Math.max(Math.abs(tangent.x), 1e-6),
        height / Math.max(Math.abs(tangent.y), 1e-6)) * 0.625;
      const profileSpan = measurementGeometry.profileSpan !== undefined && measurementGeometry.profileSpan > 0
        ? Math.min(measurementGeometry.profileSpan, availableSpan) : measurementGeometry.profileSpan;
      const fittedGeometry: MeasurementGeometry = { ...measurementGeometry,
        start: { x: center.x - direction.x * length / 2, y: center.y - direction.y * length / 2 },
        end: { x: center.x + direction.x * length / 2, y: center.y + direction.y * length / 2 },
        ...(profileSpan === undefined ? {} : { profileSpan }) };
      const next = measurementGeometryParameters(draft, fittedGeometry);
      if (parameterFingerprint(next) === draftFingerprint) return;
      setMeasurementFitUndo({ previous: cloneParameters(draft), afterFingerprint: parameterFingerprint(next) });
      setDraft(next);
      return;
    }
    const minDimension = Math.min(width, height);
    const band = Math.max(1, Math.min(measurementGeometry.band ?? 8, minDimension * 0.15));
    const maxRadius = Math.max(1, minDimension / 2 - band - 1);
    const radius = Math.max(1, Math.min(minDimension / 4.8, maxRadius));
    const fittedGeometry: MeasurementGeometry = { ...measurementGeometry, center: { x: width / 2, y: height / 2 }, radius, band };
    const next = measurementGeometryParameters(draft, fittedGeometry);
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
  const draftNode: Node = { ...node, data: { ...node.data, parameters: draft } };
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
  const perspectiveRoi = (() => {
    const defaults = [0, 0, 100, 0, 100, 100, 0, 100];
    const values = ['x1', 'y1', 'x2', 'y2', 'x3', 'y3', 'x4', 'y4']
      .map((key, index) => Number(draft[key] ?? defaults[index]));
    if (values.some((value) => !Number.isFinite(value))) return undefined;
    return {
      type: 'Polygon' as const,
      points: [0, 2, 4, 6].map((index) => ({ x: values[index], y: values[index + 1] }))
    };
  })();
  const draftNumber = (key: string, fallback = 0) => {
    const value = draft[key];
    const number = typeof value === 'number' ? value : Number(value ?? fallback);
    return Number.isFinite(number) ? number : fallback;
  };
  /** 数值条件参数：仅在相关功能启用时显示，避免默认表单出现无效控件。 */
  const numericHiddenParameterNames = [
    catalogItem.type === 'match.template' && draftNumber('scaleMinPercent', 100) === 100
      && draftNumber('scaleMaxPercent', 100) === 100 ? 'scaleStepPercent' : undefined,
    catalogItem.type === 'measure.regionIntensity' && draftNumber('meanNominal') === 0 ? 'tolerance' : undefined,
    catalogItem.type === 'measure.wallThickness' && draftNumber('nominalThickness') === 0 ? 'tolerance' : undefined,
    catalogItem.type === 'measure.lineSpacing' && draftNumber('nominalDistance') === 0 ? 'tolerance' : undefined,
    catalogItem.type === 'defect.referenceCompare' && draftNumber('variationK') === 0 ? 'variationWindow' : undefined,
    catalogItem.type === 'output.httpRequest' && draftNumber('retries') === 0 ? 'retryDelayMs' : undefined,
    catalogItem.type === 'flow.try' && draftNumber('maxRetries') === 0 ? 'retryDelayMs' : undefined,
    catalogItem.type === 'robot.executeTarget' && draftNumber('maxRetries') === 0 ? 'retryDelayMs' : undefined,
    catalogItem.type === 'match.template' && (draftNumber('searchAngleStartDeg') !== 0 || draftNumber('searchAngleEndDeg') !== 0)
      ? 'angleRangeDeg' : undefined,
    catalogItem.type === 'measure.caliperWidth' && String(draft.direction ?? 'strongest') !== 'closestwidth'
      ? 'nominalWidth' : undefined,
    catalogItem.type === 'flow.if' && !connectedInputPorts.includes('previous') ? 'hysteresis' : undefined
  ].filter((name): name is string => Boolean(name));
  const editorHiddenParameterNames = catalogItem.type === 'flow.switch' ? ['casesJson'] : [];
  /** 这些推理节点的输出坐标无法经过居中裁剪重映射，运行时会拒绝 crop=true。 */
  const unsupportedCropParameterNames = ['inference.detection', 'inference.segmentation', 'inference.obb', 'inference.instanceSegmentation', 'inference.pose', 'inference.anomaly'].includes(catalogItem.type)
    ? ['crop'] : [];
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
  const roiTransformEditHint = roiInputConnected
    ? 'roi 输入端口已连接，上游 ROI 优先于本节点参数。请在上游 ROI 节点编辑区域。'
    : referenceMismatch ? '图像或定位来源、模型原点已变化，请重新设为基准后再编辑、运行或应用。'
      : !referenceImage ? '先获取图像并设为基准，系统会同时保存该次图像与实际位姿，再在基准图上绘制 ROI。旧流程的手动基准参数继续兼容。'
        : roiImageView === 'reference' ? '在已保存基准图上编辑 ROI。重新设为基准会同步迁移已有区域；当前图与修正结果只读。'
          : '当前图像只读。切回已保存基准图像编辑区域，避免对当前图上的选区重复施加位移。';
  const details = <div className="node-info">
    <NodeDescription text={catalogItem.description} />
    <h3>输入端口</h3><PortTable ports={catalogItem.inputs} input />
    <h3>输出端口</h3><PortTable ports={withSwitchPorts(catalogItem, draft).outputs} />
    {catalogItem.parameters.some((parameter) => !parameter.internal) && <>
      <h3>参数说明</h3>
      <dl className="node-info-parameters">{catalogItem.parameters.filter((parameter) => !parameter.internal).map((parameter) => {
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
        const group = canonicalParameterGroup(parameter.group);
        if (parameter.name === 'angleRangeDeg' && !legacyWorkflow) return false;
        if (parameter.name === 'templatePath' && templateSourceNodeId) return false;
        if (parameter.name === 'scoreUsingClutter' && !draft.clutterRoi) return false;
        if (['trainingOriginX', 'trainingOriginY'].includes(parameter.name) && draft.trainingOriginMode !== 'manual') return false;
        if (parameter.name === 'scaleStepPercent' && Number(draft.scaleTolerancePercent ?? 0) === 0
          && Number(draft.scaleToleranceXPercent ?? 0) === 0 && Number(draft.scaleToleranceYPercent ?? 0) === 0) return false;
        if (parameter.name === 'minArea') return !trainingMode;
        return trainingMode
          ? ['Training', 'Origin Estimation', 'Candidates', 'Auto Tune', 'Model', 'General'].includes(group)
          : ['Matching', 'Multi-target', 'Search', 'Managed Chamfer', 'Diagnostics', 'General'].includes(group);
      }).map((parameter) => ({
        ...parameter,
        group: ({
          Training: '模型训练', 'Origin Estimation': '原点估算', Candidates: '特征筛选', General: '训练图像来源',
          Matching: '匹配条件', 'Multi-target': '多目标定位', Search: '搜索范围'
        } as Record<string, string>)[['trainingAngleRangeDeg', 'scaleTolerancePercent', 'scaleToleranceXPercent', 'scaleToleranceYPercent'].includes(parameter.name)
          ? 'Training' : canonicalParameterGroup(parameter.group)] ?? parameter.group
      }))
    } : searchLineInputEdge ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
      !['startX', 'startY', 'endX', 'endY'].includes(parameter.name))
    } : circleInputEdge ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
      !['centerX', 'centerY', 'radius'].includes(parameter.name))
    } : isRoiTransform ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
      !(referenceImage && ['baseX', 'baseY', 'baseThetaDeg'].includes(parameter.name))
      && !(poseEdge && ['currentX', 'currentY', 'currentThetaDeg'].includes(parameter.name))) } : catalogItem;
    const roiFields = isPerspective ? [] : shapeMode ? trainingMode
      ? [
        { key: 'roi', label: '模型区域', emptyText: '未设置 · 使用整幅训练图像', showEmpty: true },
        { key: 'clutterRoi', label: '杂乱区域', emptyText: '未设置 · 不启用杂乱度评分', showEmpty: true }
      ]
      : [{ key: 'searchRoi', label: '搜索区域', emptyText: '未设置 · 使用整幅场景图像', showEmpty: true }]
      : isImageCrop ? cropRoiInputConnected ? []
        : [{ key: 'roi', label: '裁剪区域', emptyText: legacyCropBoundsPresent && !draft.roi
          ? '正在使用旧版裁剪坐标；载入输入图像后可在画布核对或调整' : '未绘制 · 使用旧版坐标或整幅图像', showEmpty: true }]
      : isRoiTransform ? roiInputConnected ? [] : [{ key: 'roi', label: '基准 ROI', emptyText: '设定基准后在基准图上绘制', showEmpty: true }]
        : roiMaskInputConnected || showMeasurementReferenceImage ? [] : undefined;
    return <div ref={bindWorkspace} tabIndex={-1} className={'node-configuration-workspace' + (showImage ? '' : ' without-image')}
      style={{ '--node-columns': columns(imageShare) } as CSSProperties}>
      {showImage && <section className={'node-configuration-image' + (isRoiTransform ? ' has-roi-reference' : '')} aria-label={shapeMode === 'training' ? '模型训练图像' : shapeMode === 'search' ? '搜索推理图像' : '图像预览'}>
        <div className="node-configuration-image-source">
          {showCaliperDiagnostics ? <span>诊断图像 · 已应用本次 ROI 与坐标变换</span> : shapeMode ? <>
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
          </> : (canEditMeasurementGeometry || canDisplayConnectedSearchLine)
            && (measurementGeometryFrameKind === 'transform-input'
              || measurementGeometryFrameKind === 'saved-reference' && measurementImageFrameReady) ? <>
            <span>卡尺几何图像</span>
            <Segmented size="small" aria-label="卡尺几何图像坐标系" value={measurementImageView}
              onChange={(value) => {
                const next = value as 'reference' | 'current';
                setMeasurementImageView(next);
                if (next === 'current') { setPreviewNodeId(node.id); setImageSource('input'); }
                else if (measurementGeometryFrameKind === 'transform-input' && cropMeasurementReferenceNodeId) {
                  setPreviewNodeId(cropMeasurementReferenceNodeId); setImageSource('output');
                }
              }}
              options={[{ label: '基准图像', value: 'reference' },
                { label: '当前测量图像', value: 'current', disabled: !measurementCurrentImageAvailable }]} />
          </>
            : canEditMeasurementGeometry && measurementGeometryFrameKind === 'transform-input' && cropMeasurementReferenceNodeId
              ? <span>几何基准图像 · {nodeLabels[cropMeasurementReferenceNodeId] ?? cropMeasurementReferenceNodeId}</span>
              : <>
            <span>预览节点</span><Select size="small" aria-label="预览节点" showSearch optionFilterProp="label" value={previewNodeId}
              style={{ minWidth: 130, flex: '1 1 130px' }} options={nodes.map((item) => ({ value: item.id, label: nodeLabels[item.id] }))}
              onChange={(id) => { setPreviewNodeId(id); setImageSource('output'); }} />
            <Segmented size="small" value={imageSource} onChange={(value) => setImageSource(value as 'preview' | 'output' | 'input')}
              options={[{ label: '最终预览', value: 'preview' }, { label: '输出', value: 'output', disabled: !selectedOutputAvailable }, { label: '输入', value: 'input', disabled: !selectedInputAvailable }]} />
          </>}
          {!showCaliperDiagnostics && imageSource === 'output' && imageOutputPorts.length > 1 && <Select size="small" aria-label="输出图像端口"
            value={imageOutputPortName} style={{ minWidth: 110 }} options={imageOutputPorts.map((entry) => ({ value: entry.portName, label: runImagePortLabel(nodes, previewNodeId, entry.portName) }))}
            onChange={setImageOutputPortName} />}
          {!showCaliperDiagnostics && canEditMeasurementGeometry && measurementGeometry && measurementImageFrameReady && !showCurrentMeasurementImage
            && !(measurementGeometry.type === 'circle' && circleInputEdge) && <Button size="small" disabled={locked}
              onClick={fitMeasurementGeometryToImage}
              title="将搜索线或标称圆居中并缩放到当前几何图像内；必要时收窄沿边缘跨度或径向搜索带，保留阈值、极性、数量和圆弧设置。">
              适配几何到图像
            </Button>}
          {!showCaliperDiagnostics && measurementFitUndoAvailable && <Button size="small" disabled={locked} onClick={undoMeasurementGeometryFit}
            title="撤销最近一次适配几何到图像">撤销几何适配</Button>}
        </div>
        <div className="node-image-provenance"><span>{displayImageSource.label}</span>
          {imageCatalogLoading && <span>正在加载节点图像…</span>}
          {imageCatalogError && <span role="alert">节点图像加载失败：{imageCatalogError} <Button size="small" onClick={retryImageCatalog}>重试加载</Button></span>}
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
            {roiInputConnected && roiSourceNode?.data.typeKey === 'roi.transform' && <span>
              上游定位修正 ROI 已经过一次位姿变换；本节点仍会按自身的基准位姿与当前位姿再次变换。只有需要叠加第二次修正时才保留此连接；否则请将原始 ROI 接入本节点，或将上游 ROI 直接接到目标裁剪/测量节点。
            </span>}
            {!debugImageConnected && <span>获取基准前，请将场景图像连接到“调试图像”端口。</span>}
            {referenceImage && roiImageView !== 'reference' && <Button size="small" disabled={locked}
              onClick={() => setRoiImageView('reference')}>切换到已保存基准</Button>}
          </Space>} />}
        {roiMaskInputConnected && <Alert showIcon type="info" title={`ROI 掩膜来自上游：${nodeLabels[roiInputEdge!.source] ?? roiInputEdge!.source}`}
          description="此节点的 roi 输入优先于本地选区，且按当前图像坐标解释。请在上游编辑区域；当前节点不会再次移动 ROI。有效运行报告且坐标系匹配时，轮廓会在输入图像上只读显示。" />}
        {connectedRoiImageFrameMismatch && <Alert showIcon type="error" title="ROI 与目标图像坐标系不匹配"
          description={connectedRoiImageFrameMismatchMessage} />}
        {croppedImageNeedsRoiWarning && <Alert showIcon type="warning" title="上游裁剪可能形成伪边缘"
          description={directCropMaskSource
            ? <span>如果裁剪使用圆形、多边形或笔刷 ROI，选区外像素会清零，可能形成伪边缘。请将“{nodeLabels[directCropMaskSource.id] ?? directCropMaskSource.id}”的 croppedRoi 输出接到本节点 roi 输入，以屏蔽选区外采样；灰度转换等不改变坐标的处理可以放在两者之间，模糊等邻域滤波后请避开 ROI 边界测量。</span>
            : <span>图像处理链中存在非矩形裁剪，选区外像素会清零，可能形成伪边缘；多次裁剪或坐标变换后，单个 croppedRoi 可能无法直接覆盖当前图像。请在当前图像坐标系提供完整 ROI 掩膜，或调整流程避免将裁剪边界当作真实边缘。</span>} />}
        {circleSamplingNotice && <Alert showIcon type="info" title="圆卡尺实际步长已自动调整" description={circleSamplingNotice} />}
        {circleFitConditionNotice && <Alert showIcon type="warning" title="圆弧覆盖可能不足以稳定拟合" description={circleFitConditionNotice} />}
        {measurementTransformInvalid && <Alert showIcon type="warning" title="当前 transform 不符合卡尺测量要求"
          description="本提示依据最近一次运行报告中的实际输入。卡尺只接受有限、方向保持的 px→px 刚性变换（平移和旋转）；缩放、剪切、透视、镜像或非像素单位会导致测量失败。请改用刚性像素变换，或让测量几何直接使用当前图像坐标。" />}
        {!showCaliperDiagnostics && measurementImageFrameUnavailable && <Alert showIcon type="warning" title="当前图像不能用于测量几何编辑"
          description={measurementGeometryFrameKind === 'transform-input' && measurementImageView === 'reference'
            ? `基准图像必须来自裁剪输入实际连接的端口“${cropMeasurementReferencePortName}”。请切回该输出端口；若未保存该端口图像，请重新运行以获取基准。其它端口和最终预览不会替代基准图像。`
            : '只有实际连接的测量输入图像或节点的 image 透传输出可用于当前坐标系编辑。其它端口和最终预览仅供查看；请切换图像来源或重新运行以获取对应图像。'} />}
        {!showCaliperDiagnostics && showCurrentMeasurementImage && viewerMeasurementGeometry && <Alert showIcon type="info" title="映射几何只读"
          description={canEditMeasurementGeometry
            ? '当前视图用于核对本次运行 transform 后的实际位置；请切回基准图像拖动或重绘卡尺几何。'
            : '当前视图用于核对本次运行 transform 后的实际位置；上游几何保持只读，请在来源节点中调整。'} />}
        {!showCaliperDiagnostics && currentMeasurementGeometryUnavailable && <Alert showIcon type="info" title="尚无当前图像中的卡尺位置"
          description="运行此节点以读取本次实际使用的 transform；在此之前不会把基准图像坐标直接叠加到当前测量图像。" />}
        {measurementTransformSourceNotice && <Alert showIcon type="info" title="transform 来源可能不适用于卡尺"
          description={measurementTransformSourceNotice} />}
        {!showCaliperDiagnostics && transformedBoundsPreflightUnavailable && <Alert showIcon type="info" title="暂时无法预估变换后的图像边界"
          description="当前节点的有效运行报告或测量图尺寸尚不可用。预检不会用基准图坐标冒充当前图像坐标；先运行一次上游定位与变换，再检查变换后的卡尺范围。" />}
        {!showCaliperDiagnostics && caliperCircleBoundsNotice && <Alert showIcon type={caliperCircleBoundsNotice.type} title={caliperCircleBoundsNotice.title}
          description={caliperCircleBoundsNotice.description} />}
        {caliperLineBoundsNotice && <Alert showIcon type={caliperLineBoundsNotice.type} title={caliperLineBoundsNotice.title}
          description={caliperLineBoundsNotice.description} />}
        {!showCaliperDiagnostics && caliperSearchLineBoundsNotice && <Alert showIcon type={caliperSearchLineBoundsNotice.type} title={caliperSearchLineBoundsNotice.title}
          description={caliperSearchLineBoundsNotice.description} />}
        {searchLineInputEdge && (!showCaliperDiagnostics || measurementGeometryConflict) && <Alert showIcon type={correctedSearchLineTransformConflict || connectedSearchLineImageFrameMismatch || connectedSearchLineTransformSourceMismatch ? 'error' : 'info'}
          title={correctedSearchLineTransformConflict ? '上游几何已处于变换目标坐标系'
            : connectedSearchLineTransformSourceMismatch ? '上游搜索线与 transform 源坐标系不匹配'
              : connectedSearchLineImageFrameMismatch ? '上游搜索线与测量图像坐标不匹配'
                : `搜索线来自上游：${nodeLabels[searchLineInputEdge.source] ?? searchLineInputEdge.source}`}
          description={correctedSearchLineTransformMessage ?? searchLineTransformSourceMismatchMessage ?? searchLineImageFrameMismatchMessage ?? (transformInputEdge
            ? connectedSearchLine && measurementGeometryFrameKind
              ? `蓝色线显示运行报告中的上游基准搜索线；它会在执行时经过“${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}”变换到当前图像。几何仅在匹配的基准图像上只读显示。`
              : `上游搜索线会再通过“${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}”的变换映射到当前图像；请确认上游线处于基准坐标，已是当前坐标的线不要重复变换。`
            : connectedSearchLine
              ? '蓝色线显示本次运行实际使用的上游搜索线（只读）；要移动搜索线，请在上游节点中调整几何。'
              : '上游 searchLine 会覆盖本节点的起点和终点参数；运行节点以读取实际搜索线，然后可在图像上查看其位置。要移动搜索线，请在上游节点中调整几何。')} />}
        {circleInputEdge && (!showCaliperDiagnostics || measurementGeometryConflict) && <Alert showIcon type={correctedCircleTransformConflict || connectedCircleImageFrameMismatch || connectedCircleTransformSourceMismatch ? 'error' : transformInputEdge ? 'warning' : 'info'}
          title={correctedCircleTransformConflict ? directCorrectedCircleTransformConflict
            ? '定位修正圆不能再次应用 transform' : '上游几何已处于变换目标坐标系'
            : connectedCircleTransformSourceMismatch ? '上游圆与 transform 源坐标系不匹配'
              : connectedCircleImageFrameMismatch ? '上游圆与测量图像坐标不匹配'
                : `标称圆来自上游：${nodeLabels[circleInputEdge.source] ?? circleInputEdge.source}`}
          description={correctedCircleTransformMessage ?? circleTransformSourceMismatchMessage ?? circleImageFrameMismatchMessage ?? (transformInputEdge
            ? connectedCircle && measurementGeometryFrameKind
              ? `圆心和标称半径锁定为运行报告中的上游基准圆；图像预览显示对应基准图像，可拖动圆弧和搜索带手柄，变换会在执行时映射到当前图像。`
              : `该圆还会经过“${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}”的变换；请确认上游圆处于基准坐标，已是当前坐标的圆不能重复变换。`
            : connectedCircle
              ? '圆心和标称半径锁定为本次运行的上游输入；可在图像上拖动蓝/紫圆弧端点和青/绿搜索带手柄，调整本节点的搜索范围。'
              : '上游 circle 会覆盖本节点的圆心和标称半径参数；运行此节点以读取本次使用的圆，之后可在图像上调整圆弧和搜索带。')} />}
        {circleInputIsBoundingApproximation && <Alert showIcon type="warning" title="上游提供的是 ROI 包围圆"
          description={`定位修正节点的 ROI 类型为 ${circleSourceRoiType}；其 circle 端口输出包围圆，不是圆拟合结果。请核对圆心和半径，并按实际尺寸设置径向搜索带；若目标需要精确圆心/半径，请连接圆检测或圆拟合节点。`} />}
        {!showCaliperDiagnostics && !searchLineInputEdge && !circleInputEdge && transformInputEdge && <Alert showIcon type="info"
          title={`几何将按变换跟随：${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}`}
          description={canEditMeasurementGeometry
            ? `本节点几何属于基准图像坐标；当前显示的${showMeasurementReferenceImage ? '已保存基准图像' : '变换输入图像'}可直接拖动或重绘，transform 会在运行时映射到测量图像。ROI 掩膜仍使用当前图像坐标。`
            : '本节点几何属于基准图像坐标；transform 会在运行时映射到测量图像。可从支持的定位修正 ROI 或图像裁剪变换自动打开对应基准图像。ROI 掩膜仍使用当前图像坐标。'} />}
        <div className="node-configuration-viewer">{isMetrology ? <MetrologyObjectEditor value={draft.objects}
          onChange={value => changeParameter('objects', value)} imageSrc={displayImageSource.url}
          width={displayImageSource.width} height={displayImageSource.height} disabled={locked || previewBusy}
          aligned={String(draft.alignMode ?? 'none') !== 'none'} overlays={resultOverlaysForViewer.filter(overlay => overlay.nodeId === node.id)} onLoadImage={() => void loadMetrologyImage()} /> : <ImageViewer previewUrl={displayImageSource.url} imageWidth={displayImageSource.width} imageHeight={displayImageSource.height}
          overlays={showCaliperDiagnostics ? diagnosticOverlays : overlaysWithTrainedContour}
          roi={showCaliperDiagnostics ? undefined : canEditRoi ? (isImageCrop ? cropRoiForImage : isPerspective ? perspectiveRoi : shapeMode === 'search' ? draft.searchRoi as VisionRoi | undefined
            : regionEditMode === 'clutter' ? draft.clutterRoi as VisionRoi | undefined : draft.roi as VisionRoi | undefined)
            : showConnectedRoiPreview ? connectedRoiPreviewGeometry : undefined} showRoiTools={!showCaliperDiagnostics && (canEditRoi || isRoiTransform)}
          roiPointLimit={isPerspective ? 4 : undefined} roiClearable={!isPerspective}
          measurementGeometry={showCaliperDiagnostics ? edgeDiagnosticGeometry : viewerMeasurementGeometry}
          measurementMarkers={showCaliperDiagnostics ? diagnosticMarkers : undefined}
          measurementGeometryEditable={!showCaliperDiagnostics && Boolean(measurementGeometry) && canEditMeasurementGeometry && !locked && !showCurrentMeasurementImage}
          caliperDiagnosticSelection={isCaliperCircle && showCaliperDiagnostics && diagnosticImageMatches && caliperDiagnostics.manifest ? {
            center: caliperDiagnostics.manifest.center, radius: caliperDiagnostics.manifest.nominalRadius,
            spokes: caliperDiagnostics.manifest.spokes, index: caliperDiagnostics.index, onSelect: caliperDiagnostics.select
          } : undefined}
          onMeasurementGeometryChange={updateMeasurementGeometry}
          roiEnabled={!showCaliperDiagnostics && canEditRoi && !locked && Boolean(displayImageSource.url)} roiTargetLabel={isImageCrop ? '裁剪输入图像' : isPerspective ? '透视四边形（左上 → 右上 → 右下 → 左下）' : isRoiTransform ? '基准 ROI' : shapeMode === 'search' ? '搜索区域' : regionEditMode === 'clutter' ? '杂乱区域' : shapeMode === 'training' ? '模型区域' : imageInputSourceId ? `输入图像 · ${nodeLabels[imageInputSourceId] ?? imageInputSourceId}` : String(node.data.label ?? node.id)}
          loading={previewBusy} onRoiChange={(roi) => isImageCrop ? changeCropRoi(roi) : isPerspective ? changePerspectiveQuad(roi) : changeParameter(shapeMode === 'search' ? 'searchRoi' : regionEditMode === 'clutter' ? 'clutterRoi' : 'roi', roi)} nodeLabels={nodeLabels} focusTick={focusTick}
          emptyHint={showCaliperDiagnostics ? (isCaliperEdge ? edgeDiagnostics.loading : caliperDiagnostics.loading) ? '正在读取本次运行的诊断图像…' : '本次诊断图像不可用；请读取诊断或重新运行此节点。' : isRoiTransform ? '获取图像并设为基准，然后在已保存的图像上绘制 ROI' : isImageCrop ? '运行节点以加载输入图像，然后绘制裁剪区域' : isPerspective ? '运行此节点加载输入图像，然后依次点击左上、右上、右下、左下四个角' : shapeMode === 'training' ? regionEditMode === 'clutter' ? '在训练图像上绘制预期为空的杂乱区域' : '获取训练图像后可绘制模型区域' : shapeMode === 'search' ? '获取场景图像后可绘制搜索区域' : '运行此节点以显示图像与矢量覆盖层'}
          emptyAction={{
            label: isRoiTransform ? '获取图像并设为基准' : isImageCrop ? '运行以获取输入图像' : isPerspective ? '运行以获取输入图像' : shapeMode === 'training' ? '获取训练图像' : shapeMode === 'search' ? '获取场景图像' : '运行此节点',
            onClick: () => void (isRoiTransform ? captureRoiReference() : shapeMode === 'training' ? fetchTrainingImage() : shapeMode === 'search' ? fetchSearchImage() : runPreview()),
            disabled: locked || (!shapeMode && !isRoiTransform && issues.length > 0) || externalChange || Boolean(previewBlockedReason) || (isRoiTransform && !debugImageConnected),
            title: previewBlockedReason || (shapeMode ? '读取当前步骤的图像' : '使用暂存参数测试此节点（Ctrl+Enter）')
          }} />}</div>
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
        {legacyReplacementType && <Alert type="info" showIcon title="兼容节点" style={{ margin: '0 10px 8px' }}
          description={`此节点保留用于已保存流程；新流程请使用「${legacyReplacement?.displayName ?? legacyReplacementType}」。`} />}
        {(isCaliperCircle || isCaliperEdge) && <div className="caliper-workspace-mode"><Segmented size="small" aria-label={isCaliperEdge ? '单边缘卡尺工作模式' : '圆卡尺工作模式'}
          value={caliperDiagnosticMode ? 'diagnostics' : 'settings'}
          options={[{ label: isCaliperEdge ? '选线与调参' : '圈选与调参', value: 'settings' }, { label: isCaliperEdge ? '边缘剖面诊断' : '单卡尺诊断', value: 'diagnostics' }]}
          onChange={value => setCaliperDiagnosticMode(value === 'diagnostics')} /></div>}
        {isCaliperCircle && showCaliperDiagnostics && <CaliperCircleDiagnosticsPanel manifest={caliperDiagnostics.manifest} profile={caliperDiagnostics.profile}
          index={caliperDiagnostics.index} available={caliperDiagnostics.available} loading={caliperDiagnostics.loading} error={caliperDiagnostics.error}
          stale={stale} busy={previewBusy} onSelect={caliperDiagnostics.select} onRetry={caliperDiagnostics.retry} onRun={() => void runPreview()}
          runDisabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || measurementGeometryConflict || connectedRoiImageFrameMismatch} />}
        {isCaliperEdge && showCaliperDiagnostics && <CaliperEdgeDiagnosticsPanel key={edgeDiagnosticKey ?? 'no-edge-diagnostic'}
          data={edgeDiagnostics.data} available={edgeDiagnostics.available} loading={edgeDiagnostics.loading} error={edgeDiagnostics.error}
          stale={stale} busy={previewBusy} onRetry={edgeDiagnostics.retry} onRun={() => void runPreview()}
          inspectionPosition={edgeInspectionPosition} onInspect={inspectEdgePosition}
          runDisabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || measurementGeometryConflict || connectedRoiImageFrameMismatch} />}
        {isCaliperEdge && <CaliperEdgeQuickSettings parameters={draft} parameterDescriptors={catalogItem.parameters} disabled={locked}
          geometry={measurementGeometry?.type === 'line' ? measurementGeometry : undefined}
          geometryDisabled={showCaliperDiagnostics || !canEditMeasurementGeometry || !measurementImageFrameReady || showCurrentMeasurementImage || measurementGeometryConflict}
          geometryHint={searchLineInputEdge ? '搜索线来自上游，几何只读；可在此调节采样参数。'
            : showCaliperDiagnostics ? '当前图像显示运行快照；切回“选线与调参”调整搜索线。'
              : showCurrentMeasurementImage ? '当前映射几何只读；切回基准图像调整搜索线。'
                : !measurementGeometry ? '运行节点加载匹配的图像后，可拖动或重绘搜索线。' : undefined}
          onGeometryChange={updateMeasurementGeometry} onChange={patch => setDraft(current => ({ ...current, ...patch }))} />}
        {isCaliperCircle && <CaliperCircleQuickSettings parameters={draft} parameterDescriptors={catalogItem.parameters} disabled={locked}
          onChange={patch => setDraft(current => ({ ...current, ...patch }))} />}
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
          <p>在输入图像上绘制矩形、圆形、多边形或笔刷选区；越出图像边缘的部分会裁切，完全未覆盖图像像素时会报错。笔刷支持添加和擦除，输出图像按选区与图像交集的紧包围框裁剪，选区外清零；ROI 蒙版端口输出二值选区。裁剪后原点移到新图左上角，可将“变换”端口连接到下游测量节点以换算原图坐标。</p>
          {cropRoiInputConnected && <Alert showIcon type="info" title={`裁剪区域来自上游：${nodeLabels[cropRoiSourceId!] ?? cropRoiSourceId}`}
            description="已连接的 roi 输入会覆盖本节点的本地选区。请在上游 ROI 节点编辑；本地选区不会参与本次裁剪。有效运行报告且坐标系匹配时，轮廓会在输入图像上只读显示。"
            style={{ marginBlock: 10 }} />}
          {!cropRoiInputConnected && (draft.roi || legacyCropBoundsPresent) && <Button size="small" disabled={locked} onClick={clearCropSelection}>清除选区（使用整图）</Button>}
          {cropClearUndoAvailable && <Button size="small" disabled={locked} onClick={undoCropSelectionClear}>撤销清除选区</Button>}
          <Button type="primary" size="small" onClick={() => void runPreview()} loading={previewBusy}
            disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || connectedRoiImageFrameMismatch}
            title={connectedRoiImageFrameMismatchMessage || previewBlockedReason || '按当前 ROI 预览裁剪图像与 ROI 蒙版'}>预览裁剪结果</Button>
        </div>}
        {issues.length > 0 && <Alert type="error" title="请修正参数后再运行或应用" description={issues.join('；')} />}
        {(catalogItem.type === 'match.template' || (catalogItem.type === 'match.shape' && shapeMode !== 'search')) && <MatchingAutoTunePanel
          nodeType={catalogItem.type === 'match.template' ? 'match.template' : 'match.shape'}
          parameters={draft as Record<string, unknown>}
          disabled={locked || referenceMismatch}
          onApply={(values) => { for (const [key, value] of Object.entries(values)) changeParameter(key, value); }} />}
        {catalogItem.type === 'flow.switch' && <SwitchCaseEditor value={draft.casesJson} disabled={locked}
          onChange={(value) => changeParameter('casesJson', value)} />}
        {['coordinate.point', 'coordinate.point3D', 'coordinate.pose'].includes(catalogItem.type)
          && connectedInputPorts.some(port => ['x', 'y', 'z', 'thetaDeg'].includes(port))
          && <Alert type="info" showIcon title="已接线的坐标分量使用上游输入，未接线分量使用本地参数。" style={{ marginBottom: 10 }} />}
        <PropertyPanel node={draftNode} catalogItem={shapePanelCatalogItem} onChange={changeParameter} disabled={locked || referenceMismatch}
          hiddenParameterNames={[
            ...((['coordinate.point', 'coordinate.point3D', 'coordinate.pose'].includes(catalogItem.type) ? connectedInputPorts.filter(port => ['x', 'y', 'z', 'thetaDeg'].includes(port))
            : catalogItem.type === 'device.writeTag' && connectedInputPorts.includes('value') ? ['valueType', 'fallbackValue']
            : catalogItem.type === 'text.match' || catalogItem.type === 'text.validate' ? connectedInputPorts.filter(port => ['text', 'expected'].includes(port))
            : catalogItem.type === 'jlvision.matrixOp' ? connectedInputPorts.filter(port => ['matrixA', 'matrixB'].includes(port))
            : catalogItem.type === 'output.saveImage' ? connectedInputPorts.filter(port => ['path', 'partId'].includes(port))
            : catalogItem.type === 'output.httpRequest' ? [...connectedInputPorts.filter(port => ['url', 'body', 'idempotencyKey'].includes(port)),
              ...(connectedInputPorts.includes('value') ? ['body', 'contentType'] : [])]
            : catalogItem.type === 'jlvision.restoration' && ['psfMotion', 'psfDefocus'].includes(String(draft.operator)) && connectedInputPorts.includes('image') ? ['width', 'height']
            : isMetrology ? ['objects'] : isCaliperCircle ? caliperCircleQuickParameterNames : isCaliperEdge ? caliperEdgeQuickParameterNames : undefined) ?? []),
            ...numericHiddenParameterNames,
            ...editorHiddenParameterNames,
            ...unsupportedCropParameterNames
          ]}
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
