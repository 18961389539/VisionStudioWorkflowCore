import { useEffect, type Dispatch, type SetStateAction } from 'react';
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, RunResult, VisionRoi } from '../types';
import type { RunImageCatalog } from '../useRunImages';
import { runImageEntryForPort } from '../useRunImages';
import { cloneParameters, parameterFingerprint, type NodeParameters } from '../nodeConfigurationState';
import { connectedCircleGeometry, connectedSearchLineGeometry, mapMeasurementGeometry, rigidPixelTransform } from './nodeConfigurationGeometry';
import {
  buildMeasurementGeometry, fitGeometryToBounds,
  measurementGeometryParameters as buildGeometryParameters, parameterNumber
} from './nodeConfigurationMeasurementGeometryModel';
import { imageSourceMatchesPort, inputSnapshotValue } from './nodeConfigurationUtils';
import { circleSamplingModel, measurementBoundsNotices } from './nodeConfigurationMeasurementNotices';
import type { MeasurementGeometry } from './ImageViewer';

export function useNodeMeasurementGeometry(options: {
  open: boolean;
  locked: boolean;
  nodeId: string;
  catalogItem: NodeCatalogItem;
  isCaliperCircle: boolean;
  draft: NodeParameters;
  setDraft: Dispatch<SetStateAction<NodeParameters>>;
  draftFingerprint: string;
  freshInputResult?: RunResult;
  runImageCatalog?: RunImageCatalog;
  imageCatalogLoading: boolean;
  imageViewerSource: { url?: string; width?: number; height?: number; nodeId?: string; portName?: string };
  previewNodeId: string;
  setPreviewNodeId: (id: string) => void;
  imageSource: 'input' | 'output' | 'preview';
  setImageSource: (source: 'input' | 'output' | 'preview') => void;
  selectedInputAvailable: boolean;
  selectedOutputAvailable: boolean;
  setImageOutputPortName: (name: string) => void;
  measurementImageInputEdge?: Edge;
  measurementImageSource?: Node;
  searchLineInputEdge?: Edge;
  circleInputEdge?: Edge;
  circleSourceNode?: Node;
  transformInputEdge?: Edge;
  transformLineageNodes: Node[];
  correctedCircleTransformConflict: boolean;
  savedMeasurementReference?: { data: string; width: number; height: number };
  cropMeasurementReferenceNodeId?: string;
  cropMeasurementReferencePortName: string;
  measurementImageView: 'reference' | 'current';
  setMeasurementImageView: (view: 'reference' | 'current') => void;
  measurementFitUndo: { previous: NodeParameters; afterFingerprint: string } | undefined;
  setMeasurementFitUndo: Dispatch<SetStateAction<{ previous: NodeParameters; afterFingerprint: string } | undefined>>;
}) {
  const {
    open, locked, nodeId, catalogItem, isCaliperCircle,
    draft, setDraft, draftFingerprint, freshInputResult, runImageCatalog, imageCatalogLoading,
    imageViewerSource, previewNodeId, setPreviewNodeId, imageSource, setImageSource,
    selectedInputAvailable, selectedOutputAvailable, setImageOutputPortName,
    measurementImageInputEdge, measurementImageSource, searchLineInputEdge,
    circleInputEdge, circleSourceNode, transformInputEdge, transformLineageNodes,
    correctedCircleTransformConflict, savedMeasurementReference,
    cropMeasurementReferenceNodeId, cropMeasurementReferencePortName,
    measurementImageView, setMeasurementImageView, measurementFitUndo, setMeasurementFitUndo
  } = options;

  const measurementInputImageEntry = runImageCatalog && freshInputResult && runImageCatalog.runId === freshInputResult.runId && measurementImageSource
    ? runImageEntryForPort(runImageCatalog, measurementImageSource.id, measurementImageInputEdge?.sourceHandle)
    : undefined;
  const measurementCurrentImageAvailable = Boolean(measurementInputImageEntry);

  const measurementGeometryType = isCaliperCircle ? 'circle'
    : ['measure.caliper', 'measure.caliperWidth', 'measure.caliperLine', 'measure.caliperArray', 'feature.edgeSubpixel'].includes(catalogItem.type) ? 'line' : undefined;
  const measurementGeometryFrameKind = !transformInputEdge ? 'current'
    : savedMeasurementReference ? 'saved-reference'
      : cropMeasurementReferenceNodeId ? 'transform-input' : undefined;

  const circleInputSnapshot = circleInputEdge
    ? inputSnapshotValue(freshInputResult?.nodeReports.filter((report) => report.nodeId === nodeId).slice(-1)[0]?.inputs, 'circle')
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
    ? inputSnapshotValue(freshInputResult?.nodeReports.filter((report) => report.nodeId === nodeId).slice(-1)[0]?.inputs, 'searchLine')
    : undefined;
  const connectedSearchLine = searchLineInputEdge ? connectedSearchLineGeometry(searchLineInputSnapshot) : undefined;
  const canDisplayConnectedSearchLine = Boolean(measurementGeometryType === 'line' && connectedSearchLine && measurementGeometryFrameKind);
  const canEditMeasurementGeometry = Boolean(measurementGeometryType && !searchLineInputEdge
    && (!circleInputEdge || connectedCircle && !correctedCircleTransformConflict) && measurementGeometryFrameKind);

  useEffect(() => {
    if (!open || !canEditMeasurementGeometry && !canDisplayConnectedSearchLine) return;
    if (measurementGeometryFrameKind === 'saved-reference' || measurementGeometryFrameKind === 'current') {
      setPreviewNodeId(nodeId);
      setImageSource(measurementGeometryFrameKind === 'current' && !selectedInputAvailable && selectedOutputAvailable ? 'output' : 'input');
    } else if (cropMeasurementReferenceNodeId) {
      const currentImage = measurementImageView === 'current';
      setPreviewNodeId(currentImage ? nodeId : cropMeasurementReferenceNodeId);
      setImageSource(currentImage ? 'input' : 'output');
      if (!currentImage) setImageOutputPortName(cropMeasurementReferencePortName);
    }
  }, [canEditMeasurementGeometry, canDisplayConnectedSearchLine, cropMeasurementReferenceNodeId, cropMeasurementReferencePortName,
    freshInputResult?.runId, measurementGeometryFrameKind, nodeId, open, measurementCurrentImageAvailable, measurementImageView,
    selectedInputAvailable, selectedOutputAvailable, setImageSource, setImageOutputPortName, setPreviewNodeId]);

  useEffect(() => {
    if (open) setMeasurementImageView('reference');
  }, [nodeId, open, setMeasurementImageView]);

  const measurementInputImageDisplayed = previewNodeId === nodeId && imageSource === 'input'
    && imageSourceMatchesPort(imageViewerSource, measurementImageInputEdge?.source, measurementImageInputEdge?.sourceHandle);
  const measurementImageFrameReady = measurementGeometryFrameKind === 'saved-reference'
    ? previewNodeId === nodeId && Boolean(savedMeasurementReference && savedMeasurementReference.width > 0 && savedMeasurementReference.height > 0)
    : measurementGeometryFrameKind === 'transform-input'
      ? measurementImageView === 'current'
        ? measurementInputImageDisplayed && measurementCurrentImageAvailable
        : previewNodeId === cropMeasurementReferenceNodeId && imageSource === 'output'
          && imageSourceMatchesPort(imageViewerSource, cropMeasurementReferenceNodeId, cropMeasurementReferencePortName)
      : measurementInputImageDisplayed || previewNodeId === nodeId && imageSource === 'output'
        && imageSourceMatchesPort(imageViewerSource, nodeId, 'image');
  const measurementImageFrameUnavailable = Boolean((canEditMeasurementGeometry || canDisplayConnectedSearchLine)
    && !measurementImageFrameReady && !imageCatalogLoading && imageViewerSource.url);

  const geometryNumber = (key: string, fallback: number) => parameterNumber(draft, key, fallback);
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
    ? freshInputResult?.nodeReports.filter((report) => report.nodeId === nodeId).slice(-1)[0]
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
    width: measurementBoundsWidth, height: measurementBoundsHeight, boundsContext: measurementBoundsContext, geometryNumber
  });

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
  const measurementFitUndoAvailable = Boolean(measurementFitUndo && measurementFitUndo.afterFingerprint === draftFingerprint);
  const undoMeasurementGeometryFit = () => {
    if (!measurementFitUndoAvailable || !measurementFitUndo) return;
    setDraft(cloneParameters(measurementFitUndo.previous));
    setMeasurementFitUndo(undefined);
  };

  return {
    measurementInputImageEntry, measurementCurrentImageAvailable,
    measurementGeometryType, measurementGeometryFrameKind, circleInputSnapshot, connectedCircle, circleSourceReport,
    circleSourceParameters, circleSourceRoiType, circleInputIsBoundingApproximation, searchLineInputSnapshot,
    connectedSearchLine, canDisplayConnectedSearchLine, canEditMeasurementGeometry, measurementInputImageDisplayed,
    measurementImageFrameReady, measurementImageFrameUnavailable, geometryNumber, measurementGeometry,
    measurementInputReport, measurementTransformValue, measurementTransform, measurementTransformInvalid,
    potentiallyIncompatibleTransformSource, measurementTransformSourceNotice, mappedMeasurementGeometry,
    showCurrentMeasurementImage, viewerMeasurementGeometry, currentMeasurementGeometryUnavailable,
    measurementBoundsWidth, measurementBoundsHeight, transformedBoundsPreflightUnavailable, measurementBoundsContext,
    circleSamplingNotice, circleFitConditionNotice, caliperCircleBoundsNotice, caliperLineBoundsNotice,
    caliperSearchLineBoundsNotice, measurementGeometryParameters, updateMeasurementGeometry,
    fitMeasurementGeometryToImage, undoMeasurementGeometryFit, measurementFitUndoAvailable
  };
}
