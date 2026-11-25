import { useMemo, useRef, useState } from 'react';
import type Konva from 'konva';
import { roiConfigurationIssue } from '../nodeConfigurationState';
import { useImagePixelSampler } from './useImagePixelSampler';
import { clipSegmentToImage, useHtmlImage, readPixelated, persistPixelated } from './imageViewerUtils';
import type { ImageViewerProps } from './imageViewerTypes';
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
  comparisonUrl,
  comparisonWidth,
  comparisonHeight,
  comparisonLabel,
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
}: ImageViewerProps) {
  // Imported workflows can contain malformed legacy ROI JSON. Ignore it in the canvas so
  // the parameter editor can explain and clear it without crashing the whole configuration UI.
  const roi = useMemo(() => inputRoi && !roiConfigurationIssue(inputRoi, 'ROI', 1024) ? inputRoi : undefined, [inputRoi]);
  const hostRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<Konva.Stage | null>(null);
  const [showOverlay, setShowOverlay] = useState(true);
  const [overlayFilter, setOverlayFilter] = useState<string[]>([]);
  const [comparing, setComparing] = useState(false);
  const comparisonActive = comparing && Boolean(comparisonUrl);
  const displayedUrl = comparisonActive ? comparisonUrl : previewUrl;
  // 像素化（最近邻）默认开：放大即真实像素方块；开关可切回平滑插值
  const [pixelated, setPixelated] = useState(readPixelated);
  useEffect(() => { persistPixelated(pixelated); }, [pixelated]);
  useEffect(() => { setOverlayFilter([]); }, [previewUrl]);
  useEffect(() => {
    if (!comparing) return;
    const release = () => setComparing(false);
    window.addEventListener('pointerup', release);
    window.addEventListener('pointercancel', release);
    window.addEventListener('blur', release);
    return () => {
      window.removeEventListener('pointerup', release);
      window.removeEventListener('pointercancel', release);
      window.removeEventListener('blur', release);
    };
  }, [comparing]);
  const { image, failed } = useHtmlImage(displayedUrl);
  const actualWidth = (comparisonActive ? comparisonWidth : imageWidth) || image?.naturalWidth || 0;
  const actualHeight = (comparisonActive ? comparisonHeight : imageHeight) || image?.naturalHeight || 0;
  // 视口（缩放/平移/适配目标）由独立 hook 持有：它需要跨渲染保住"适配意图"，
  // 混在手势状态机里最容易在改交互时被无意重置。
  const { size, view, setView, fit, oneToOne, focusSearchLine } = useViewerViewport({
    hostRef, actualWidth, actualHeight, roi, focusTick, measurementGeometry
  });
  const canDrawMeasurementGeometry = Boolean(!comparisonActive && measurementGeometryEditable && measurementGeometry && (measurementGeometry.type === 'line'
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
    previewUrl: displayedUrl,
    actualWidth,
    actualHeight,
    view,
    setView,
    roi: comparisonActive ? undefined : roi,
    roiEnabled: !comparisonActive && roiEnabled,
    roiPointLimit,
    onRoiChange,
    measurementGeometry: comparisonActive ? undefined : measurementGeometry,
    measurementGeometryEditable: !comparisonActive && measurementGeometryEditable,
    canDrawMeasurementGeometry,
    onMeasurementGeometryChange,
    caliperDiagnosticSelection: comparisonActive ? undefined : caliperDiagnosticSelection
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
        comparisonAvailable={Boolean(comparisonUrl)}
        comparisonLabel={comparisonLabel}
        comparing={comparisonActive}
        onCompareStart={() => setComparing(true)}
        onCompareEnd={() => setComparing(false)}
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
        previewUrl={displayedUrl}
        failed={failed}
        emptyHint={emptyHint}
        emptyAction={comparisonActive ? undefined : emptyAction}
        showOverlay={showOverlay && !comparisonActive}
        visibleOverlays={visibleOverlays}
        measurementGeometry={comparisonActive ? undefined : measurementGeometry}
        measurementDraft={measurementDraft}
        measurementGeometryEditable={!comparisonActive && measurementGeometryEditable}
        geometryDrawMode={geometryDrawMode}
        circleConstructionPoints={circleConstructionPoints}
        circleDrawMode={circleDrawMode}
        cursor={cursor}
        measurementMarkers={comparisonActive ? [] : measurementMarkers}
        caliperDiagnosticSelection={comparisonActive ? undefined : caliperDiagnosticSelection}
        roi={comparisonActive ? undefined : roi}
        roiEnabled={!comparisonActive && roiEnabled}
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
