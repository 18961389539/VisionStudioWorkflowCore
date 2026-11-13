import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type Konva from 'konva';
import type { OverlayPoint, VisionOverlay, VisionRoi, VisionRoiBrushStroke } from '../types';
import { parameterFingerprint, roiConfigurationIssue } from '../nodeConfigurationState';
import { createPixelBuffer, readPixel } from '../imagePixels';
import { buildCaliperCircleAngles, effectiveSampleWidth, normalizeCaliperCircleArc } from '../caliperCircleSampling';
import { circleArcThroughThreePoints, circleThroughThreePoints } from '../circleConstruction';
import { clamp, clipSegmentToImage, nearestDiagnosticSpoke, caliperCircleProfileWidthHandle,
  maxInteractiveCircleRadius, normalizeRect, pointInsideRoi, brushRoiBounds, boundedMeasurementPoint,
  polygonRoiBounds, clampRoiMove, applyRoiDrag, useHtmlImage, readPixelated, persistPixelated
} from './imageViewerUtils';
import { applyMeasurementDrag } from './imageViewerMeasurementDrag';
import type { Point, ViewState, RoiDragMode, MeasurementDragMode, CircleDrawMode, ToolMode, RoiMoveBounds, CaliperDiagnosticSelection } from './imageViewerTypes';
import type { MeasurementGeometry } from './imageViewerUtils';
import { hitMeasurementHandle, hitRoiHandle } from './imageViewerHitTest';
import { useViewerViewport } from './useViewerViewport';
import { useViewerCursorStyle } from './useViewerCursorStyle';
import { ImageViewerToolbar } from './ImageViewerToolbar';
import { ImageViewerStage } from './ImageViewerStage';
import { ImageViewerReadout } from './ImageViewerReadout';
import { ImageViewerDrawingHint } from './ImageViewerDrawingHint';
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
  const [tool, setTool] = useState<ToolMode>('pan');
  const [cursorScreen, setCursor] = useState<Point>();
  const [showOverlay, setShowOverlay] = useState(true);
  const [overlayFilter, setOverlayFilter] = useState<string[]>([]);
  const [brushWidth, setBrushWidth] = useState(20);
  const [measurementDraft, setMeasurementDraft] = useState<MeasurementGeometry>();
  const [geometryDrawMode, setGeometryDrawMode] = useState(false);
  const [circleDrawMode, setCircleDrawMode] = useState<CircleDrawMode>('center');
  const isThreePointDrawing = circleDrawMode === 'threePoints' || circleDrawMode === 'threePointArc';
  const [circleConstructionPoints, setCircleConstructionPoints] = useState<Point[]>([]);
  const [circleConstructionIssue, setCircleConstructionIssue] = useState<string>();
  const [roiUndoEntry, setRoiUndoEntry] = useState<{ previous?: VisionRoi; afterFingerprint: string }>();
  const [measurementUndoEntry, setMeasurementUndoEntry] = useState<{ previous: MeasurementGeometry; afterFingerprint: string }>();
  // 像素化（最近邻）默认开：放大即真实像素方块；开关可切回平滑插值
  const [pixelated, setPixelated] = useState(readPixelated);
  useEffect(() => { persistPixelated(pixelated); }, [pixelated]);
  const [draft, setDraft] = useState<VisionRoi>();
  const gestureStart = useRef<Point | undefined>(undefined);
  const geometryGestureStart = useRef<Point | undefined>(undefined);
  const geometryDrag = useRef<{ mode: MeasurementDragMode; origin: MeasurementGeometry; start: Point; profileWidthAngle?: number } | undefined>(undefined);
  const panStart = useRef<{ pointer: Point; view: ViewState } | undefined>(undefined);
  const roiDrag = useRef<{ mode: RoiDragMode; origin: VisionRoi; start: Point; moveBounds?: RoiMoveBounds } | undefined>(undefined);
  const [roiDragOffset, setRoiDragOffset] = useState<Point>();
  const polygonPoints = useRef<OverlayPoint[]>([]);
  const brushStroke = useRef<{ baseStrokes: VisionRoiBrushStroke[]; activeStroke: VisionRoiBrushStroke } | undefined>(undefined);
  const [brushPreview, setBrushPreview] = useState<VisionRoiBrushStroke>();
  const pointerUpHandler = useRef<() => void>(() => undefined);
  const pointerCancelHandler = useRef<() => void>(() => undefined);
  const pointerMoveHandler = useRef<() => void>(() => undefined);
  const { image, failed } = useHtmlImage(previewUrl);
  const actualWidth = imageWidth || image?.naturalWidth || 0;
  const actualHeight = imageHeight || image?.naturalHeight || 0;
  // 视口（缩放/平移/适配目标）由独立 hook 持有：它需要跨渲染保住"适配意图"，
  // 混在手势状态机里最容易在改交互时被无意重置。
  const { size, view, setView, fit, oneToOne, focusSearchLine } = useViewerViewport({
    hostRef, actualWidth, actualHeight, roi, focusTick, measurementGeometry
  });
  const canDrawMeasurementGeometry = measurementGeometryEditable && measurementGeometry && (measurementGeometry.type === 'line'
    || measurementGeometry.centerEditable !== false && measurementGeometry.radiusEditable !== false);
  const cursorPoint = cursorScreen ? { x: (cursorScreen.x - view.x) / view.scale, y: (cursorScreen.y - view.y) / view.scale } : undefined;
  const cursor = cursorPoint && cursorPoint.x >= 0 && cursorPoint.y >= 0 && cursorPoint.x < actualWidth && cursorPoint.y < actualHeight ? cursorPoint : undefined;
  const pixelBuffer = useMemo(() => {
    if (!image) return undefined;
    try {
      const canvas = document.createElement('canvas');
      canvas.width = image.naturalWidth;
      canvas.height = image.naturalHeight;
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) return null;
      context.drawImage(image, 0, 0);
      return createPixelBuffer(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height);
    } catch { return null; }
  }, [image]);
  const pixel = cursor && pixelBuffer ? readPixel(pixelBuffer, cursor.x, cursor.y, actualWidth, actualHeight) : undefined;

  useEffect(() => {
    if (!roiEnabled) {
      setTool('pan');
      setDraft(undefined);
      setBrushPreview(undefined);
      polygonPoints.current = [];
      brushStroke.current = undefined;
      gestureStart.current = undefined;
      roiDrag.current = undefined;
      setRoiDragOffset(undefined);
      setRoiUndoEntry(undefined);
    }
  }, [roiEnabled]);
  useEffect(() => {
    setOverlayFilter([]);
    setCursor(undefined);
    setDraft(undefined);
    setBrushPreview(undefined);
    setMeasurementDraft(undefined);
    setGeometryDrawMode(false);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    setRoiUndoEntry(undefined);
    setMeasurementUndoEntry(undefined);
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
    polygonPoints.current = [];
    brushStroke.current = undefined;
    roiDrag.current = undefined;
    setRoiDragOffset(undefined);
  }, [previewUrl]);
  useEffect(() => {
    if (measurementGeometryEditable && canDrawMeasurementGeometry) return;
    setGeometryDrawMode(false);
    setMeasurementDraft(undefined);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    if (!measurementGeometryEditable) setMeasurementUndoEntry(undefined);
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
  }, [measurementGeometryEditable, Boolean(canDrawMeasurementGeometry)]);

  const commitRoiEdit = (next?: VisionRoi) => {
    if (parameterFingerprint(roi) === parameterFingerprint(next)) return;
    const entry = { previous: roi, afterFingerprint: parameterFingerprint(next) };
    setRoiUndoEntry(entry);
    onRoiChange(next);
  };

  const undoRoiEdit = () => {
    if (!roiUndoEntry) return;
    setRoiUndoEntry(undefined);
    if (roiUndoEntry.afterFingerprint === parameterFingerprint(roi)) onRoiChange(roiUndoEntry.previous);
  };

  const committedMeasurementGeometry = (geometry: MeasurementGeometry): MeasurementGeometry => {
    if (geometry.type === 'line') return geometry.profileWidth === undefined ? geometry
      : { ...geometry, profileWidth: effectiveSampleWidth(geometry.profileWidth) };
    const start = geometry.angleStartDeg ?? -180;
    const end = geometry.angleEndDeg ?? 180;
    const normalizedArc = normalizeCaliperCircleArc(start, end);
    return { ...geometry, angleStartDeg: normalizedArc.start, angleEndDeg: normalizedArc.end,
      band: clamp(geometry.band ?? 1, 1, 500),
      ...(geometry.profileWidth === undefined ? {} : { profileWidth: effectiveSampleWidth(geometry.profileWidth) }) };
  };

  const commitMeasurementEdit = (next: MeasurementGeometry) => {
    if (!measurementGeometry || !onMeasurementGeometryChange) return;
    const expected = committedMeasurementGeometry(next);
    const entry = { previous: measurementGeometry, afterFingerprint: parameterFingerprint(expected) };
    setMeasurementUndoEntry(entry);
    onMeasurementGeometryChange(next);
  };

  const undoMeasurementEdit = () => {
    if (!measurementUndoEntry || !measurementGeometry || !onMeasurementGeometryChange) return;
    setMeasurementUndoEntry(undefined);
    if (measurementUndoEntry.afterFingerprint === parameterFingerprint(measurementGeometry))
      onMeasurementGeometryChange(measurementUndoEntry.previous, { preserveValues: true });
  };

  const imagePoint = useCallback((screen: Point): Point => ({
    x: (screen.x - view.x) / view.scale,
    y: (screen.y - view.y) / view.scale
  }), [view]);

  const inside = useCallback((p: Point) => p.x >= 0 && p.y >= 0 && p.x <= actualWidth && p.y <= actualHeight,
    [actualHeight, actualWidth]);

  const pointer = (): Point | undefined => {
    const p = stageRef.current?.getPointerPosition();
    return p ? { x: p.x, y: p.y } : undefined;
  };

  const onWheel = (event: Konva.KonvaEventObject<WheelEvent>) => {
    event.evt.preventDefault();
    const p = pointer();
    if (!p || !actualWidth) return;
    const before = imagePoint(p);
    const factor = event.evt.deltaY > 0 ? 0.9 : 1.1;
    const scale = clamp(view.scale * factor, 0.02, 30);
    setView({ scale, x: p.x - before.x * scale, y: p.y - before.y * scale });
  };

  // 双指缩放（2026-10-25）：此前触摸只有平移/绘制，缩放依赖 onWheel——一体机与平板上等于没有缩放。
  // 以两指中点为锚点缩放；首次识别到双指时记录基准距离与图像锚点，避免逐帧漂移。
  const pinchRef = useRef<{ distance: number; scale: number; anchor: Point } | undefined>(undefined);

  const stageTouchPoint = (clientX: number, clientY: number): Point | undefined => {
    const container = stageRef.current?.container();
    if (!container) return undefined;
    const rect = container.getBoundingClientRect();
    return { x: clientX - rect.left, y: clientY - rect.top };
  };

  const onStageTouchStart = (event: Konva.KonvaEventObject<TouchEvent>) => {
    // Konva 在某些调用路径（含测试里的合成事件）不保证有 evt，一律做空值保护
    const touches = event?.evt?.touches as TouchList | undefined;
    if (touches && touches.length >= 2) { pinchRef.current = undefined; return; }
    onPointerDown();
  };

  const onStageTouchMove = (event: Konva.KonvaEventObject<TouchEvent>) => {
    const touches = event?.evt?.touches as TouchList | undefined;
    if (!touches || touches.length < 2) { onPointerMove(); return; }
    event?.evt?.preventDefault?.();
    const [first, second] = [touches[0], touches[1]];
    const distance = Math.hypot(first.clientX - second.clientX, first.clientY - second.clientY);
    const center = stageTouchPoint((first.clientX + second.clientX) / 2, (first.clientY + second.clientY) / 2);
    if (!center) return;
    if (!pinchRef.current || !pinchRef.current.distance) {
      pinchRef.current = {
        distance,
        scale: view.scale,
        anchor: { x: (center.x - view.x) / view.scale, y: (center.y - view.y) / view.scale }
      };
      return;
    }
    const pinch = pinchRef.current;
    const scale = clamp(pinch.scale * (distance / pinch.distance), 0.02, 30);
    setView({ scale, x: center.x - pinch.anchor.x * scale, y: center.y - pinch.anchor.y * scale });
  };

  const onStageTouchEnd = () => {
    pinchRef.current = undefined;
    onPointerUp();
  };

  const beginBrushStroke = (point: Point, mode: VisionRoiBrushStroke['mode']) => {
    const activeStroke = { mode, width: brushWidth, points: [point, point] };
    brushStroke.current = { baseStrokes: roi?.type === 'Brush' ? roi.strokes : [], activeStroke };
    setDraft(undefined);
    setBrushPreview(activeStroke);
  };

  const extendBrushStroke = (point: Point) => {
    const active = brushStroke.current;
    if (!active) return;
    const previous = active.activeStroke.points[active.activeStroke.points.length - 1];
    if (Math.hypot(point.x - previous.x, point.y - previous.y) < 0.35) return;
    const activeStroke = { ...active.activeStroke, points: [...active.activeStroke.points, point] };
    brushStroke.current = { ...active, activeStroke };
    setBrushPreview(activeStroke);
  };

  const finishBrushStroke = () => {
    const active = brushStroke.current;
    if (!active) return;
    brushStroke.current = undefined;
    setBrushPreview(undefined);
    setDraft(undefined);
    commitRoiEdit({ type: 'Brush', strokes: [...active.baseStrokes, active.activeStroke] });
  };

  const onPointerDown = () => {
    const p = pointer();
    if (!p) return;
    setCursor(p);
    const ip = imagePoint(p);
    if (caliperDiagnosticSelection) {
      const index = nearestDiagnosticSpoke(caliperDiagnosticSelection, ip, view.scale);
      if (index !== undefined) caliperDiagnosticSelection.onSelect?.(index);
      else panStart.current = { pointer: p, view };
      return;
    }
    if (geometryDrawMode && canDrawMeasurementGeometry && measurementGeometry) {
      if (!inside(ip)) return;
      if (measurementGeometry.type === 'circle' && isThreePointDrawing) {
        const picked = [...circleConstructionPoints, ip];
        setCircleConstructionIssue(undefined);
        if (picked.length === 2 && Math.hypot(ip.x - picked[0].x, ip.y - picked[0].y) < 1) {
          setCircleConstructionIssue('第 2 点距离第 1 点过近，请换一个边缘点。');
          return;
        }
        if (picked.length < 3) {
          setCircleConstructionPoints(picked);
        } else {
          const circle = circleDrawMode === 'threePointArc'
            ? circleArcThroughThreePoints(picked[0], picked[1], picked[2])
            : circleThroughThreePoints(picked[0], picked[1], picked[2]);
          if (!circle) {
            setCircleConstructionIssue('这三个点过近或接近共线，无法确定有效圆。请重新选择第 3 点。');
            setMeasurementDraft(undefined);
            return;
          }
          commitMeasurementEdit({ ...measurementGeometry, ...circle });
          setCircleConstructionPoints([]);
          setMeasurementDraft(undefined);
          setGeometryDrawMode(false);
        }
        return;
      }
      geometryGestureStart.current = ip;
      setMeasurementDraft(measurementGeometry.type === 'line'
        ? { ...measurementGeometry, start: ip, end: ip }
        : { ...measurementGeometry, center: ip, radius: 0 });
      return;
    }
    if (measurementGeometryEditable && measurementGeometry) {
      const handle = hitMeasurementHandle(measurementGeometry, p, view, actualWidth, actualHeight);
      if (handle) {
        const profileWidthAngle = handle === 'profileWidth' && measurementGeometry.type === 'circle'
          ? caliperCircleProfileWidthHandle(measurementGeometry, buildCaliperCircleAngles(measurementGeometry), actualWidth, actualHeight)?.angle
          : undefined;
        geometryDrag.current = { mode: handle, origin: measurementGeometry, start: ip, profileWidthAngle };
        return;
      }
    }
    // 平移模式负责编辑已有 ROI；绘制工具始终新建/替换选区，避免 ROI 内部点击被吞成移动。
    if (roi && roiEnabled && tool === 'pan') {
      const handle = hitRoiHandle(roi, p, view, actualWidth, actualHeight);
      if (handle) {
        roiDrag.current = { mode: handle, origin: roi, start: imagePoint(p) };
        return;
      }
      if (pointInsideRoi(roi, imagePoint(p))) {
        const moveBounds = roi.type === 'Brush' ? brushRoiBounds(roi)
          : roi.type === 'Polygon' ? polygonRoiBounds(roi) : undefined;
        roiDrag.current = { mode: 'move', origin: roi, start: imagePoint(p), moveBounds };
        if (roi.type === 'Brush' || roi.type === 'Polygon') setRoiDragOffset({ x: 0, y: 0 });
        return;
      }
    }
    if (tool === 'pan') {
      panStart.current = { pointer: p, view };
      return;
    }
    if (!roiEnabled || tool === 'polygon') return;
    if (!inside(ip)) return;
    if (tool === 'brush') {
      beginBrushStroke(ip, 'Add');
      return;
    }
    if (tool === 'eraser') {
      if (roi?.type !== 'Brush') return;
      beginBrushStroke(ip, 'Erase');
      return;
    }
    gestureStart.current = ip;
    if (tool === 'rect') setDraft(normalizeRect(ip, ip));
    if (tool === 'circle') setDraft({ type: 'Circle', x: ip.x, y: ip.y, radius: 0 });
  };

  const onPointerMove = () => {
    const p = pointer();
    if (!p) return;
    const ip = imagePoint(p);
    setCursor(p);
    if (geometryDrawMode && isThreePointDrawing && measurementGeometry?.type === 'circle') {
      const circle = circleConstructionPoints.length === 2 && inside(ip)
        ? (circleDrawMode === 'threePointArc' ? circleArcThroughThreePoints : circleThroughThreePoints)(circleConstructionPoints[0], circleConstructionPoints[1], ip)
        : undefined;
      setMeasurementDraft(circle ? { ...measurementGeometry, ...circle } : undefined);
      return;
    }
    if (geometryGestureStart.current && measurementGeometry) {
      const start = geometryGestureStart.current;
      const end = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
      setMeasurementDraft(measurementGeometry.type === 'line'
        ? { ...measurementGeometry, start, end: boundedMeasurementPoint(ip) }
        : circleDrawMode === 'diameter'
          ? { ...measurementGeometry, center: { x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 },
            radius: Math.hypot(end.x - start.x, end.y - start.y) / 2 }
          : { ...measurementGeometry, center: start,
            radius: Math.min(Math.hypot(end.x - start.x, end.y - start.y), maxInteractiveCircleRadius(start, actualWidth, actualHeight)) });
      return;
    }
    if (geometryDrag.current) {
      const { mode, origin, start, profileWidthAngle } = geometryDrag.current;
      const next = applyMeasurementDrag({ origin, mode, start, pointer: ip,
        current: { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) },
        profileWidthAngle, imageWidth: actualWidth, imageHeight: actualHeight });
      if (next) setMeasurementDraft(next);
      return;
    }
    if (brushStroke.current) {
      extendBrushStroke({ x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) });
      return;
    }
    if (roiDrag.current) {
      const active = roiDrag.current;
      if ((active.origin.type === 'Brush' || active.origin.type === 'Polygon') && active.moveBounds) {
        setRoiDragOffset(clampRoiMove(active.moveBounds,
          { x: ip.x - active.start.x, y: ip.y - active.start.y }, actualWidth, actualHeight));
        return;
      }
      const next = applyRoiDrag(active.origin, active.mode, active.start, ip, actualWidth, actualHeight, active.moveBounds);
      if (next) setDraft(next);
      return;
    }
    if (tool === 'pan' && panStart.current) {
      const start = panStart.current;
      setView({ ...start.view, x: start.view.x + p.x - start.pointer.x, y: start.view.y + p.y - start.pointer.y });
      return;
    }
    if (!gestureStart.current || !roiEnabled) return;
    const end = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
    if (tool === 'rect') setDraft(normalizeRect(gestureStart.current, end));
    if (tool === 'circle') {
      const dx = end.x - gestureStart.current.x;
      const dy = end.y - gestureStart.current.y;
      const center = gestureStart.current;
      setDraft({ type: 'Circle', x: center.x, y: center.y, radius: Math.min(Math.hypot(dx, dy), maxInteractiveCircleRadius(center, actualWidth, actualHeight)) });
    }
  };

  const onPointerUp = () => {
    panStart.current = undefined;
    if (geometryGestureStart.current) {
      geometryGestureStart.current = undefined;
      const next = measurementDraft;
      if (next && onMeasurementGeometryChange && (next.type === 'line'
        ? Math.hypot(next.end.x - next.start.x, next.end.y - next.start.y) >= 2
        : next.radius >= 1)) commitMeasurementEdit(next);
      setMeasurementDraft(undefined);
      setGeometryDrawMode(false);
      return;
    }
    if (geometryDrag.current) {
      geometryDrag.current = undefined;
      const next = measurementDraft;
      if (next && onMeasurementGeometryChange && (next.type === 'line'
        ? Math.hypot(next.end.x - next.start.x, next.end.y - next.start.y) >= 2
        : next.radius >= 1)) commitMeasurementEdit(next);
      setMeasurementDraft(undefined);
      return;
    }
    if (brushStroke.current) {
      finishBrushStroke();
      return;
    }
    if (roiDrag.current) {
      const active = roiDrag.current;
      roiDrag.current = undefined;
      const next = active.origin.type === 'Brush' || active.origin.type === 'Polygon' && active.mode === 'move'
        ? applyRoiDrag(active.origin, active.mode, { x: 0, y: 0 }, roiDragOffset ?? { x: 0, y: 0 },
          actualWidth, actualHeight, active.moveBounds)
        : draft;
      if (next && (next.type !== 'Rectangle' || (next.width >= 2 && next.height >= 2)) && (next.type !== 'Circle' || next.radius >= 1)) {
        commitRoiEdit(next);
      }
      setRoiDragOffset(undefined);
      setDraft(undefined);
      return;
    }
    if (gestureStart.current && draft) {
      if (draft.type === 'Rectangle' && draft.width >= 2 && draft.height >= 2) commitRoiEdit(draft);
      if (draft.type === 'Circle' && draft.radius >= 1) commitRoiEdit(draft);
    }
    gestureStart.current = undefined;
    setDraft(undefined);
  };

  const onPointerCancel = () => {
    panStart.current = undefined;
    gestureStart.current = undefined;
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
    roiDrag.current = undefined;
    setRoiDragOffset(undefined);
    brushStroke.current = undefined;
    setBrushPreview(undefined);
    setDraft(undefined);
    setMeasurementDraft(undefined);
    setGeometryDrawMode(false);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
  };

  useEffect(() => { pointerUpHandler.current = onPointerUp; });
  useEffect(() => { pointerCancelHandler.current = onPointerCancel; });
  useEffect(() => { pointerMoveHandler.current = onPointerMove; });
  useEffect(() => {
    const releaseInteraction = () => pointerUpHandler.current();
    const cancelInteraction = () => pointerCancelHandler.current();
    const moveOutsideViewer = (event: MouseEvent) => {
      if (!(panStart.current || roiDrag.current || brushStroke.current || geometryDrag.current
        || gestureStart.current || geometryGestureStart.current)) return;
      if (event.target instanceof Element && hostRef.current?.contains(event.target)) return;
      stageRef.current?.setPointersPositions?.(event);
      pointerMoveHandler.current();
    };
    // Konva's stage only receives mouse/touch end while the pointer is over the viewer.
    // Track mouse drags outside the canvas and listen for release over a toolbar or elsewhere.
    document.addEventListener('mousemove', moveOutsideViewer);
    document.addEventListener('mouseup', releaseInteraction);
    document.addEventListener('touchend', releaseInteraction);
    document.addEventListener('touchcancel', cancelInteraction);
    return () => {
      document.removeEventListener('mousemove', moveOutsideViewer);
      document.removeEventListener('mouseup', releaseInteraction);
      document.removeEventListener('touchend', releaseInteraction);
      document.removeEventListener('touchcancel', cancelInteraction);
    };
  }, []);

  const onClick = () => {
    if (tool !== 'polygon' || !roiEnabled) return;
    const p = pointer();
    if (!p) return;
    const ip = imagePoint(p);
    if (!inside(ip)) return;
    polygonPoints.current = [...polygonPoints.current, ip];
    if (roiPointLimit && polygonPoints.current.length >= roiPointLimit) {
      const points = polygonPoints.current.slice(0, roiPointLimit);
      polygonPoints.current = [];
      setDraft(undefined);
      commitRoiEdit({ type: 'Polygon', points });
      return;
    }
    setDraft({ type: 'Polygon', points: polygonPoints.current });
  };

  const commitPolygon = () => {
    if (tool !== 'polygon') return;
    if (roiPointLimit && polygonPoints.current.length !== roiPointLimit) return;
    if (polygonPoints.current.length < 3) return;
    const points = polygonPoints.current.filter((point, index, all) => index === 0
      || point.x !== all[index - 1].x || point.y !== all[index - 1].y);
    // Browsers emit two click events before dblclick. Trim the terminal click (and
    // an optional repeated first point used to close the polygon) from the ROI.
    if (points.length > 1 && points.at(-1)!.x === points[0].x && points.at(-1)!.y === points[0].y)
      points.pop();
    if (points.length >= 3) commitRoiEdit({ type: 'Polygon', points });
    polygonPoints.current = [];
    setDraft(undefined);
  };

  const clearRoi = () => {
    polygonPoints.current = [];
    brushStroke.current = undefined;
    setBrushPreview(undefined);
    setDraft(undefined);
    commitRoiEdit(undefined);
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        // Cancel the unfinished construction before the containing modal handles Escape.
        if (geometryDrawMode) { e.preventDefault(); e.stopPropagation(); }
        gestureStart.current = undefined;
        geometryGestureStart.current = undefined;
        geometryDrag.current = undefined;
        roiDrag.current = undefined;
        setRoiDragOffset(undefined);
        polygonPoints.current = [];
        brushStroke.current = undefined;
        setBrushPreview(undefined);
        setDraft(undefined);
        setMeasurementDraft(undefined);
        setGeometryDrawMode(false);
        setCircleConstructionPoints([]);
        setCircleConstructionIssue(undefined);
        setTool('pan');
      }
    };
    window.addEventListener('keydown', onKey, true);
    return () => window.removeEventListener('keydown', onKey, true);
  }, [geometryDrawMode]);

  // 切换到新工具前清掉所有进行中的手势：半截的笔迹/多边形/草稿如果跨工具残留，
  // 下一次 pointerup 会把它误当成新工具的提交。
  const changeTool = (value: ToolMode) => {
    polygonPoints.current = [];
    brushStroke.current = undefined;
    setBrushPreview(undefined);
    gestureStart.current = undefined;
    geometryGestureStart.current = undefined;
    geometryDrag.current = undefined;
    setGeometryDrawMode(false);
    setMeasurementDraft(undefined);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    roiDrag.current = undefined;
    setRoiDragOffset(undefined);
    setDraft(undefined);
    setTool(value);
  };

  const cursorStyle = useViewerCursorStyle({
    caliperDiagnosticSelection,
    geometryDrawMode,
    isPanning: Boolean(panStart.current),
    isDragging: Boolean(roiDrag.current || geometryDrag.current),
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
  const roiUndoAvailable = Boolean(roiUndoEntry && roiUndoEntry.afterFingerprint === parameterFingerprint(roi));
  const measurementUndoAvailable = Boolean(measurementUndoEntry && measurementGeometry
    && measurementUndoEntry.afterFingerprint === parameterFingerprint(measurementGeometry));

  return (
    <div className={'vision-viewer' + (geometryDrawMode && measurementGeometry?.type === 'circle' ? ' is-drawing-circle' : '')}>
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
        canDrawMeasurementGeometry={Boolean(canDrawMeasurementGeometry)}
        geometryDrawMode={geometryDrawMode}
        measurementGeometry={measurementGeometry}
        onToggleGeometryDrawMode={() => {
          setMeasurementDraft(undefined);
          geometryGestureStart.current = undefined;
          setCircleConstructionPoints([]);
          setCircleConstructionIssue(undefined);
          setGeometryDrawMode(value => !value);
          setTool('pan');
        }}
        circleDrawMode={circleDrawMode}
        onCircleDrawModeChange={(value) => {
          setCircleDrawMode(value);
          setCircleConstructionPoints([]);
          setCircleConstructionIssue(undefined);
          setMeasurementDraft(undefined);
          geometryGestureStart.current = undefined;
        }}
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
      {geometryDrawMode && (measurementGeometry?.type === 'line' || measurementGeometry?.type === 'circle')
        && <ImageViewerDrawingHint
          kind={measurementGeometry.type}
          threePoint={isThreePointDrawing}
          circleDrawMode={circleDrawMode}
          constructionPoints={circleConstructionPoints}
          constructionIssue={circleConstructionIssue}
          measurementDraft={measurementDraft}
          onUndoConstructionPoint={() => {
            setCircleConstructionPoints(points => points.slice(0, -1));
            setMeasurementDraft(undefined);
            setCircleConstructionIssue(undefined);
          }}
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
        pixelUnavailable={pixelBuffer === null}
        roiEnabled={roiEnabled}
        roiTargetLabel={roiTargetLabel}
      />
    </div>
  );
}
