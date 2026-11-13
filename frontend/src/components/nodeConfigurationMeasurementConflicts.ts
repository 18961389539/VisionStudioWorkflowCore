/**
 * NodeConfigurationDialog 的测量节点连接关系与坐标系一致性判定（纯函数）。
 *
 * 从 `useNodeConfigurationDialogModel` 拆出（2026-11-13）。这块是整个配置页最容易出错的部分：
 * 上游 ROI / 搜索线 / 标称圆 / transform 各自可能处在不同的像素坐标系，错配不会报错，
 * 只会让测量结果静默偏移。判定条件因此写成显式派生而不是内联三元，便于单独测试与复查。
 *
 * 不依赖 React；输入是图（nodes/edges）+ 目录 + 草稿参数，输出是一组标志位与提示文案。
 */
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem } from '../types';
import type { NodeParameters } from '../nodeConfigurationState';
import {
  connectedCircleGeometry, connectedSearchLineGeometry, geometrySourceImageFrameNodeIds,
  roiSourceFrameNodeIdsForEdge, transformEndpointImageFrameNodeIds, transformMappedImageFrameNodeIds,
  transformSourceLineage
} from './nodeConfigurationGeometry';
import { imageFramesSharePixelPreservingAncestor, inputEdge } from './nodeConfigurationUtils';

/** 会走卡尺/测量几何编辑的节点类型。集中一处，避免各处 `includes` 数组漂移。 */
export const MEASUREMENT_NODE_TYPES = [
  'feature.edgeSubpixel', 'measure.caliper', 'measure.caliperWidth',
  'measure.caliperLine', 'measure.caliperArray', 'measure.caliperCircle'
] as const;

/** 带「搜索线」输入端口的节点（不含标称圆节点，它走 `circle` 端口）。 */
export const SEARCH_LINE_NODE_TYPES = [
  'measure.caliper', 'measure.caliperWidth', 'measure.caliperLine',
  'measure.caliperArray', 'feature.edgeSubpixel'
] as const;

/** 带 `transform` 输入端口的节点。 */
export const TRANSFORM_PORT_NODE_TYPES = [...MEASUREMENT_NODE_TYPES] as const;

export type MeasurementNodeKind = {
  isShapeMatch: boolean;
  isImageCrop: boolean;
  isPerspective: boolean;
  isRoiTransform: boolean;
  isCaliperCircle: boolean;
  isCaliperEdge: boolean;
  isMetrology: boolean;
  /** 该节点是否参与卡尺/测量几何编辑（决定是否解析搜索线、圆、transform 端口）。 */
  isMeasurement: boolean;
};

/**
 * 按节点类型分流。配置页里几乎所有能力位都由类型决定，散在 20 多处 `catalogItem.type === 'x'`
 * 很难核对，集中成一处后加节点类型只需改这里。
 */
export function classifyMeasurementNode(type: string): MeasurementNodeKind {
  const isMeasurement = (MEASUREMENT_NODE_TYPES as readonly string[]).includes(type);
  return {
    isShapeMatch: type === 'match.shape',
    isImageCrop: type === 'image.crop',
    isPerspective: type === 'image.perspective',
    isRoiTransform: type === 'roi.transform',
    isCaliperCircle: type === 'measure.caliperCircle',
    isCaliperEdge: type === 'measure.caliper' || type === 'feature.edgeSubpixel',
    isMetrology: type === 'measure.metrology',
    isMeasurement
  };
}

/** ROI 图编辑所依据的图像端口：ROI 变换看 debugImage，裁剪/透视/区域/测量类看 image。 */
export function roiImageInputPortFor(type: string, kind: MeasurementNodeKind): string | undefined {
  if (kind.isRoiTransform) return 'debugImage';
  if (type === 'image.crop' || type === 'image.perspective' || type === 'region.fromRoi' || kind.isMeasurement) return 'image';
  return undefined;
}

export type MeasurementConnectionInput = {
  nodeId: string;
  nodes: Node[];
  edges: Edge[];
  catalogItems: NodeCatalogItem[];
  nodeType: string;
  kind: MeasurementNodeKind;
  /** 保持像素坐标系的节点类型集合（裁剪/透视等），用于帧血缘判定。 */
  framePreservingNodeTypes: ReadonlySet<string>;
  roiInputEdge?: Edge;
  measurementImageInputEdge?: Edge;
};

export type MeasurementConnections = {
  roiImageInputPort?: string;
  roiImageInputEdge?: Edge;
  roiSourceImageFrameNodeIds: string[];
  roiImageInputSourceNode?: Node;
  connectedRoiImageFrameMismatch: boolean;
  connectedRoiImageFrameMismatchMessage?: string;

  searchLineInputEdge?: Edge;
  circleInputEdge?: Edge;
  circleSourceNode?: Node;
  transformInputEdge?: Edge;
  transformLineageNodes: Node[];

  connectedGeometryEdge?: Edge;
  connectedGeometryImageFrameNodeIds: string[];
  transformSourceImageFrames: string[];
  transformMappedImageFrames: string[];

  connectedGeometryAlreadyInMappedFrame: boolean;
  connectedGeometryTransformSourceFrameMismatch: boolean;
  connectedGeometryImageFrameMismatch: boolean;

  directCorrectedCircleTransformConflict: boolean;
  correctedCircleTransformConflict: boolean;
  correctedSearchLineTransformConflict: boolean;
  connectedCircleImageFrameMismatch: boolean;
  connectedSearchLineImageFrameMismatch: boolean;
  connectedCircleTransformSourceMismatch: boolean;
  connectedSearchLineTransformSourceMismatch: boolean;

  measurementGeometryTransformConflict: boolean;
  measurementGeometryFrameMismatchConflict: boolean;
  measurementGeometryConflict: boolean;
  /** 按优先级选出的唯一一条提示（渲染层主提示用）。 */
  measurementGeometryConflictMessage?: string;
  /** 六条细分提示：渲染层在冲突卡片里逐条展开时用（每条只在对应冲突成立时非空）。 */
  correctedCircleTransformMessage?: string;
  correctedSearchLineTransformMessage?: string;
  circleImageFrameMismatchMessage?: string;
  searchLineImageFrameMismatchMessage?: string;
  circleTransformSourceMismatchMessage?: string;
  searchLineTransformSourceMismatchMessage?: string;

  referenceTransformNode?: Node;
  referenceTransformParameters: NodeParameters;
  savedMeasurementReference?: { data: string; width: number; height: number };
  cropTransformNode?: Node;
  cropMeasurementReferenceEdge?: Edge;
  cropMeasurementReferenceNodeId?: string;
  cropMeasurementReferencePortName: string;
};

/**
 * 解析测量节点的全部连接与坐标系一致性。
 *
 * 判定顺序即提示优先级：先报「重复映射」（成因明确、修法唯一），再报「源坐标系不匹配」，
 * 最后报「与当前图像不同坐标系」（最宽泛）。同一个问题只给一条提示，避免面板堆五行红字。
 */
export function resolveMeasurementConnections(input: MeasurementConnectionInput): MeasurementConnections {
  const {
    nodeId, nodes, edges, catalogItems, nodeType, kind, framePreservingNodeTypes,
    roiInputEdge, measurementImageInputEdge
  } = input;
  const sharesFrame = (a: string, b: string) => imageFramesSharePixelPreservingAncestor(a, b, nodes, edges, framePreservingNodeTypes);

  const roiImageInputPort = roiImageInputPortFor(nodeType, kind);
  const roiImageInputEdge = roiImageInputPort ? inputEdge(edges, nodeId, roiImageInputPort) : undefined;
  const roiSourceImageFrameNodeIds = roiSourceFrameNodeIdsForEdge(roiInputEdge, nodes, edges, catalogItems);
  const roiImageInputSourceNode = nodes.find((item) => item.id === roiImageInputEdge?.source);
  const connectedRoiImageFrameMismatch = Boolean(roiSourceImageFrameNodeIds.length && roiImageInputEdge
    && roiSourceImageFrameNodeIds.some((roiFrameNodeId) => !sharesFrame(roiFrameNodeId, roiImageInputEdge.source)));
  const connectedRoiImageFrameMismatchMessage = connectedRoiImageFrameMismatch
    ? `上游 ROI 与目标图像“${String(roiImageInputSourceNode?.data.label ?? roiImageInputEdge!.source)}”不在同一像素坐标系。请将 ROI 与图像接到同一裁剪/变换分支；若目标图像来自裁剪节点，请连接该裁剪节点的 croppedRoi 输出。当前已阻止测试运行。`
    : undefined;

  const searchLineInputEdge = (SEARCH_LINE_NODE_TYPES as readonly string[]).includes(nodeType)
    ? inputEdge(edges, nodeId, 'searchLine') : undefined;
  const circleInputEdge = kind.isCaliperCircle ? inputEdge(edges, nodeId, 'circle') : undefined;
  const circleSourceNode = circleInputEdge ? nodes.find((item) => item.id === circleInputEdge.source) : undefined;
  const transformInputEdge = (TRANSFORM_PORT_NODE_TYPES as readonly string[]).includes(nodeType)
    ? inputEdge(edges, nodeId, 'transform') : undefined;
  const transformLineageNodes = transformSourceLineage(nodes, edges, transformInputEdge?.source);

  const connectedGeometryEdge = circleInputEdge ?? searchLineInputEdge;
  const connectedGeometryImageFrameNodeIds = geometrySourceImageFrameNodeIds(connectedGeometryEdge, nodes, edges, catalogItems);
  const transformSourceImageFrames = transformInputEdge
    ? transformEndpointImageFrameNodeIds(transformInputEdge.source, 'source', nodes, edges) : [];
  const transformMappedImageFrames = transformMappedImageFrameNodeIds(transformLineageNodes, edges);

  const connectedGeometryAlreadyInMappedFrame = Boolean(connectedGeometryImageFrameNodeIds.length && transformInputEdge
    && connectedGeometryImageFrameNodeIds.some((geometryFrameNodeId) => transformMappedImageFrames.some((mappedFrameNodeId) =>
      sharesFrame(geometryFrameNodeId, mappedFrameNodeId))));
  const connectedGeometryTransformSourceFrameMismatch = Boolean(connectedGeometryImageFrameNodeIds.length && transformSourceImageFrames.length
    && connectedGeometryImageFrameNodeIds.some((geometryFrameNodeId) => transformSourceImageFrames.every((sourceFrameNodeId) =>
      !sharesFrame(geometryFrameNodeId, sourceFrameNodeId))));
  const connectedGeometryImageFrameMismatch = Boolean(connectedGeometryImageFrameNodeIds.length && !transformInputEdge && measurementImageInputEdge
    && connectedGeometryImageFrameNodeIds.some((geometryFrameNodeId) => !sharesFrame(geometryFrameNodeId, measurementImageInputEdge.source)));

  const directCorrectedCircleTransformConflict = kind.isCaliperCircle && Boolean(circleInputEdge && transformInputEdge
    && circleSourceNode?.data.typeKey === 'roi.transform'
    && transformLineageNodes.some((source) => source.id === circleInputEdge.source));
  const correctedCircleTransformConflict = directCorrectedCircleTransformConflict
    || kind.isCaliperCircle && connectedGeometryAlreadyInMappedFrame;
  const correctedSearchLineTransformConflict = Boolean(searchLineInputEdge && transformInputEdge && connectedGeometryAlreadyInMappedFrame);
  const connectedCircleImageFrameMismatch = Boolean(circleInputEdge && connectedGeometryImageFrameMismatch);
  const connectedSearchLineImageFrameMismatch = Boolean(searchLineInputEdge && connectedGeometryImageFrameMismatch);
  const connectedCircleTransformSourceMismatch = Boolean(circleInputEdge && connectedGeometryTransformSourceFrameMismatch);
  const connectedSearchLineTransformSourceMismatch = Boolean(searchLineInputEdge && connectedGeometryTransformSourceFrameMismatch);
  const measurementGeometryTransformConflict = correctedCircleTransformConflict || correctedSearchLineTransformConflict;
  const measurementGeometryFrameMismatchConflict = connectedCircleImageFrameMismatch || connectedSearchLineImageFrameMismatch
    || connectedCircleTransformSourceMismatch || connectedSearchLineTransformSourceMismatch;

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
  const cropMeasurementReferenceEdge = cropTransformNode ? inputEdge(edges, cropTransformNode.id, 'image') : undefined;

  return {
    roiImageInputPort, roiImageInputEdge, roiSourceImageFrameNodeIds, roiImageInputSourceNode,
    connectedRoiImageFrameMismatch, connectedRoiImageFrameMismatchMessage,
    searchLineInputEdge, circleInputEdge, circleSourceNode, transformInputEdge, transformLineageNodes,
    connectedGeometryEdge, connectedGeometryImageFrameNodeIds, transformSourceImageFrames, transformMappedImageFrames,
    connectedGeometryAlreadyInMappedFrame, connectedGeometryTransformSourceFrameMismatch, connectedGeometryImageFrameMismatch,
    directCorrectedCircleTransformConflict, correctedCircleTransformConflict, correctedSearchLineTransformConflict,
    connectedCircleImageFrameMismatch, connectedSearchLineImageFrameMismatch,
    connectedCircleTransformSourceMismatch, connectedSearchLineTransformSourceMismatch,
    measurementGeometryTransformConflict, measurementGeometryFrameMismatchConflict,
    measurementGeometryConflict: measurementGeometryTransformConflict || measurementGeometryFrameMismatchConflict,
    measurementGeometryConflictMessage,
    correctedCircleTransformMessage, correctedSearchLineTransformMessage,
    circleImageFrameMismatchMessage, searchLineImageFrameMismatchMessage,
    circleTransformSourceMismatchMessage, searchLineTransformSourceMismatchMessage,
    referenceTransformNode, referenceTransformParameters, savedMeasurementReference,
    cropTransformNode, cropMeasurementReferenceEdge,
    cropMeasurementReferenceNodeId: cropMeasurementReferenceEdge?.source,
    cropMeasurementReferencePortName: cropMeasurementReferenceEdge?.sourceHandle ?? 'image'
  };
}

/**
 * 测量几何所处的坐标系种类。
 *  - `current`：直接在当前测量图像坐标；
 *  - `saved-reference`：基准图像上保存的几何（需要中间视图切换）；
 *  - `transform-input`：几何在 transform 的输入图像坐标，显示时按本次运行的刚性变换映射。
 */
export type MeasurementFrameKind = 'current' | 'saved-reference' | 'transform-input' | undefined;

export function resolveMeasurementFrameKind(
  transformInputEdge: Edge | undefined,
  savedMeasurementReference: MeasurementConnections['savedMeasurementReference'],
  cropMeasurementReferenceNodeId: string | undefined
): MeasurementFrameKind {
  if (!transformInputEdge) return 'current';
  if (savedMeasurementReference) return 'saved-reference';
  if (cropMeasurementReferenceNodeId) return 'transform-input';
  return undefined;
}

/** 从上游快照里取出已连接的圆/搜索线几何；端口未连接时返回 undefined。 */
export function resolveConnectedGeometry(
  connections: Pick<MeasurementConnections, 'circleInputEdge' | 'searchLineInputEdge'>,
  readInputSnapshot: (port: string) => unknown
) {
  const circle = connections.circleInputEdge
    ? connectedCircleGeometry(readInputSnapshot('circle') as Parameters<typeof connectedCircleGeometry>[0])
    : undefined;
  const searchLine = connections.searchLineInputEdge
    ? connectedSearchLineGeometry(readInputSnapshot('searchLine') as Parameters<typeof connectedSearchLineGeometry>[0])
    : undefined;
  return { connectedCircle: circle, connectedSearchLine: searchLine };
}
