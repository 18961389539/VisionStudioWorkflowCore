import { useMemo, useRef, useState } from 'react';
import type Konva from 'konva';
import type { VisionOverlay, VisionRoi } from '../types';
import { roiConfigurationIssue } from '../nodeConfigurationState';
import { useImagePixelSampler } from './useImagePixelSampler';
import { clipSegmentToImage, useHtmlImage, readPixelated, persistPixelated } from './imageViewerUtils';
import type { MeasurementGeometry } from './imageViewerUtils';
import type { CaliperDiagnosticSelection } from './imageViewerTypes';
import { useViewerViewport } from './useViewerViewport';
import { useViewerCursorStyle } from './useViewerCursorStyle';
import { useImageViewerInteractions } from './useImageViewerInteractions';
import { ImageViewerToolbar } from './ImageViewerToolbar';
import { ImageViewerStage } from './ImageViewerStage';
import { ImageViewerReadout } from './ImageViewerReadout';
import { ImageViewerDrawingHint } from './ImageViewerDrawingHint';
import { useEffect } from 'react';
export type { MeasurementGeometry } from './imageViewerUtils';
export { buildCaliperCircleAngles } from '../caliperCircleSampling';

export default function ImageViewer({
  previewUrl,
  imageWidth,
  imageHeight,
  overlays,
  roi: inputRoi,
  roiEnabled,
  showRoiTools = true,
  roiPointLimit,
  roiClearable = true,
  roiTargetLabel,
  loading,
  onRoiChange,
  measurementGeometry,
  measurementGeometryEditable = false,
  onMeasurementGeometryChange,
  caliperDiagnosticSelection,
  measurementMarkers = [],
  nodeLabels,
  focusTick,
  emptyHint = '运行工作流以显示图像和矢量覆盖层',
  emptyAction
}: {
  previewUrl?: string;
  imageWidth?: number;
  imageHeight?: number;
  overlays: VisionOverlay[];
  roi?: VisionRoi;
  roiEnabled: boolean;
  /** 纯图像预览时隐藏无关的绘图操作。 */
  showRoiTools?: boolean;
  /** 多边形点数上限；设置后只保留平移/多边形工具，达到点数立即闭合（如透视四角）。 */
  roiPointLimit?: number;
  /** 固定结构 ROI 无法用“清除”表达时关闭清除按钮（如透视四角始终需要四个点）。 */
  roiClearable?: boolean;
  roiTargetLabel?: string;
  loading?: boolean;
  onRoiChange: (roi?: VisionRoi) => void;
  /** Search geometry authored in the displayed image coordinate frame. */
  measurementGeometry?: MeasurementGeometry;
  measurementGeometryEditable?: boolean;
  onMeasurementGeometryChange?: (geometry: MeasurementGeometry, options?: { preserveValues?: boolean }) => void;
  /** Read-only samples in the displayed run image frame. Picking never changes measurement geometry. */
  caliperDiagnosticSelection?: CaliperDiagnosticSelection;
  /** Diagnostic cursor and candidates stay visible above the search geometry. */
  measurementMarkers?: VisionOverlay[];
  /** 覆盖层按节点筛选时的显示名映射 */
  nodeLabels?: Record<string, string>;
  /** 递增信号：来自属性面板“定位选区”，把视图适配到当前 ROI 区域 */
  focusTick?: number;
  /** 无图像时的提示文案（默认面向主画布流程运行；弹窗内可改为“运行此节点”） */
  emptyHint?: string;
  /** 无图像时的操作入口（如“运行此节点”）：空态不再只有一行灰字 */
  emptyAction?: { label: string; onClick: () => void; disabled?: boolean; title?: string };
}) {
  // Imported workflows can contain malformed legacy ROI JSON. Ignore it in the canvas so
  // the parameter editor can explain and clear it without crashing the whole configuration UI.
  const roi = useMemo(() => inputRoi && !roiConfigurationIssue(inputRoi, 'ROI', 1024) ? inputRoi : undefined, [inputRoi]);
  const hostRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<Konva.Stage | null>(null);
  const [showOverlay, setShowOverlay] = useState(true);
  const [overlayFilter, setOverlayFilter] = useState<string[]>([]);
  // 像素化（最近邻）默认开：放大即真实像素方块；开关可切回平滑插值
  const [pixelated, setPixelated] = useState(readPixelated);
  useEffect(() => { persistPixelated(pixelated); }, [pixelated]);
  useEffect(() => { setOverlayFilter([]); }, [previewUrl]);
  const { image, failed } = useHtmlImage(previewUrl);
  const actualWidth = imageWidth || image?.naturalWidth || 0;
  const actualHeight = imageHeight || image?.naturalHeight || 0;
  // 视口（缩放/平移/适配目标）由独立 hook 持有：它需要跨渲染保住"适配意图"，
  // 混在手势状态机里最容易在改交互时被无意重置。
  const { size, view, setView, fit, oneToOne, focusSearchLine } = useViewerViewport({
    hostRef, actualWidth, actualHeight, roi, focusTick, measurementGeometry
  });
  const canDrawMeasurementGeometry = Boolean(measurementGeometryEditable && measurementGeometry && (measurementGeometry.type === 'line'
    || measurementGeometry.centerEditable !== false && measurementGeometry.radiusEditable !== false));
  const {
    tool, cursorScreen, cursor, setCursor, brushWidth, setBrushWidth,
    draft, measurementDraft, geometryDrawMode, circleDrawMode, isThreePointDrawing,
    circleConstructionPoints, circleConstructionIssue, roiDragOffset, brushPreview,
    isPanning, isDragging, roiUndoAvailable, measurementUndoAvailable,
    changeTool, toggleGeometryDrawMode, changeCircleDrawMode, undoConstructionPoint,
    undoRoiEdit, undoMeasurementEdit, clearRoi,
    onWheel, onPointerDown, onPointerMove, onPointerUp,
    onStageTouchStart, onStageTouchMove, onStageTouchEnd, onClick, commitPolygon
  } = useImageViewerInteractions({
    hostRef,
    stageRef,
    previewUrl,
    actualWidth,
    actualHeight,
    view,
    setView,
    roi,
    roiEnabled,
    roiPointLimit,
    onRoiChange,
    measurementGeometry,
    measurementGeometryEditable,
    canDrawMeasurementGeometry,
    onMeasurementGeometryChange,
    caliperDiagnosticSelection
  });
  const { pixel, pixelUnavailable } = useImagePixelSampler(image, cursor, actualWidth, actualHeight);

  const cursorStyle = useViewerCursorStyle({
    caliperDiagnosticSelection,
    geometryDrawMode,
    isPanning,
    isDragging,
    cursorScreen,
    measurementGeometry,
    measurementGeometryEditable,
    roi,
    roiEnabled,
    tool,
    view,
    imageWidth: actualWidth,
    imageHeight: actualHeight
  });

  // 覆盖层按节点筛选：不选=显示全部；选了=只显示所选节点的覆盖层
  const overlayNodeIds = useMemo(() => {
    const ids: string[] = [];
    overlays.forEach((overlay) => {
      if (overlay.nodeId && !ids.includes(overlay.nodeId)) ids.push(overlay.nodeId);
    });
    return ids;
  }, [overlays]);
  const visibleOverlays = useMemo(
    () => (overlayFilter.length === 0 ? overlays : overlays.filter((overlay) => overlay.nodeId && overlayFilter.includes(overlay.nodeId))),
    [overlays, overlayFilter]);

  const showDrawingHint = Boolean(geometryDrawMode && (measurementGeometry?.type === 'line' || measurementGeometry?.type === 'circle'));
  return (
    <div className={'vision-viewer' + (showDrawingHint ? ' is-drawing' : '')}>
      <ImageViewerToolbar
        showRoiTools={showRoiTools}
        roiEnabled={roiEnabled}
        roiPointLimit={roiPointLimit}
        roiClearable={roiClearable}
        roi={roi}
        tool={tool}
        onToolChange={changeTool}
        roiUndoAvailable={roiUndoAvailable}
        onUndoRoiEdit={undoRoiEdit}
        brushWidth={brushWidth}
        onBrushWidthChange={setBrushWidth}
        canDrawMeasurementGeometry={canDrawMeasurementGeometry}
        geometryDrawMode={geometryDrawMode}
        measurementGeometry={measurementGeometry}
        onToggleGeometryDrawMode={toggleGeometryDrawMode}
        circleDrawMode={circleDrawMode}
        onCircleDrawModeChange={changeCircleDrawMode}
        measurementUndoAvailable={measurementUndoAvailable}
        onUndoMeasurementEdit={undoMeasurementEdit}
        focusSearchLineDisabled={!measurementGeometry || measurementGeometry.type !== 'line'
          || !clipSegmentToImage(measurementGeometry.start, measurementGeometry.end, actualWidth, actualHeight)}
        onFocusSearchLine={focusSearchLine}
        onFit={fit}
        onOneToOne={oneToOne}
        onClearRoi={clearRoi}
        pixelated={pixelated}
        onPixelatedChange={setPixelated}
        showOverlay={showOverlay}
        onShowOverlayChange={setShowOverlay}
        overlayNodeIds={overlayNodeIds}
        overlayFilter={overlayFilter}
        onOverlayFilterChange={setOverlayFilter}
        nodeLabels={nodeLabels}
      />
      {showDrawingHint && measurementGeometry && <ImageViewerDrawingHint
          kind={measurementGeometry.type}
          threePoint={isThreePointDrawing}
          circleDrawMode={circleDrawMode}
          constructionPoints={circleConstructionPoints}
          constructionIssue={circleConstructionIssue}
          measurementDraft={measurementDraft}
          onUndoConstructionPoint={undoConstructionPoint}
        />}

      <ImageViewerStage
        hostRef={hostRef}
        stageRef={stageRef}
        size={size}
        view={view}
        image={image}
        imageWidth={actualWidth}
        imageHeight={actualHeight}
        pixelated={pixelated}
        cursorStyle={cursorStyle}
        loading={Boolean(loading)}
        previewUrl={previewUrl}
        failed={failed}
        emptyHint={emptyHint}
        emptyAction={emptyAction}
        showOverlay={showOverlay}
        visibleOverlays={visibleOverlays}
        measurementGeometry={measurementGeometry}
        measurementDraft={measurementDraft}
        measurementGeometryEditable={measurementGeometryEditable}
        geometryDrawMode={geometryDrawMode}
        circleConstructionPoints={circleConstructionPoints}
        circleDrawMode={circleDrawMode}
        cursor={cursor}
        measurementMarkers={measurementMarkers}
        caliperDiagnosticSelection={caliperDiagnosticSelection}
        roi={roi}
        roiEnabled={roiEnabled}
        tool={tool}
        draft={draft}
        brushPreview={brushPreview}
        roiDragOffset={roiDragOffset}
        onWheel={onWheel}
        onPointerDown={onPointerDown}
        onStageTouchStart={onStageTouchStart}
        onPointerMove={onPointerMove}
        onStageTouchMove={onStageTouchMove}
        onPointerUp={onPointerUp}
        onStageTouchEnd={onStageTouchEnd}
        onClick={onClick}
        onDblClick={commitPolygon}
        onMouseLeave={() => { setCursor(undefined); }}
      />
      <ImageViewerReadout
        imageWidth={actualWidth}
        imageHeight={actualHeight}
        scale={view.scale}
        cursor={cursor}
        image={image}
        pixel={pixel}
        pixelUnavailable={pixelUnavailable}
        roiEnabled={roiEnabled}
        roiTargetLabel={roiTargetLabel}
      />
    </div>
  );
}
