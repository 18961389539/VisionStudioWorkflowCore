import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';
import type Konva from 'konva';
import type { OverlayPoint, VisionRoi, VisionRoiBrushStroke } from '../types';
import { parameterFingerprint } from '../nodeConfigurationState';
import { buildCaliperCircleAngles, effectiveSampleWidth, normalizeCaliperCircleArc } from '../caliperCircleSampling';
import { circleArcThroughThreePoints, circleThroughThreePoints } from '../circleConstruction';
import {
  clamp, nearestDiagnosticSpoke, caliperCircleProfileWidthHandle,
  maxInteractiveCircleRadius, normalizeRect, pointInsideRoi, brushRoiBounds, boundedMeasurementPoint,
  polygonRoiBounds, clampRoiMove, applyRoiDrag, type MeasurementGeometry
} from './imageViewerUtils';
import { applyMeasurementDrag } from './imageViewerMeasurementDrag';
import { hitMeasurementHandle, hitRoiHandle } from './imageViewerHitTest';
import type {
  Point, ViewState, RoiDragMode, MeasurementDragMode, CircleDrawMode, ToolMode, RoiMoveBounds, CaliperDiagnosticSelection
} from './imageViewerTypes';

type ActiveGesture =
  | { kind: 'pan'; pointer: Point; view: ViewState }
  | { kind: 'roi-draw'; start: Point }
  | { kind: 'roi-drag'; mode: RoiDragMode; origin: VisionRoi; start: Point; moveBounds?: RoiMoveBounds }
  | { kind: 'brush'; baseStrokes: VisionRoiBrushStroke[]; activeStroke: VisionRoiBrushStroke }
  | { kind: 'geometry-draw'; start: Point }
  | { kind: 'geometry-drag'; mode: MeasurementDragMode; origin: MeasurementGeometry; start: Point; profileWidthAngle?: number };

export function useImageViewerInteractions(options: {
  hostRef: RefObject<HTMLDivElement | null>;
  stageRef: RefObject<Konva.Stage | null>;
  previewUrl?: string;
  actualWidth: number;
  actualHeight: number;
  view: ViewState;
  setView: (view: ViewState) => void;
  roi?: VisionRoi;
  roiEnabled: boolean;
  roiPointLimit?: number;
  onRoiChange: (roi?: VisionRoi) => void;
  measurementGeometry?: MeasurementGeometry;
  measurementGeometryEditable: boolean;
  canDrawMeasurementGeometry: boolean;
  onMeasurementGeometryChange?: (geometry: MeasurementGeometry, options?: { preserveValues?: boolean }) => void;
  caliperDiagnosticSelection?: CaliperDiagnosticSelection;
}) {
  const {
    hostRef, stageRef, previewUrl, actualWidth, actualHeight, view, setView,
    roi, roiEnabled, roiPointLimit, onRoiChange,
    measurementGeometry, measurementGeometryEditable, canDrawMeasurementGeometry, onMeasurementGeometryChange,
    caliperDiagnosticSelection
  } = options;

  const [tool, setTool] = useState<ToolMode>('pan');
  const [cursorScreen, setCursor] = useState<Point>();
  const [brushWidth, setBrushWidth] = useState(20);
  const [measurementDraft, setMeasurementDraft] = useState<MeasurementGeometry>();
  const [geometryDrawMode, setGeometryDrawMode] = useState(false);
  const [circleDrawMode, setCircleDrawMode] = useState<CircleDrawMode>('center');
  const isThreePointDrawing = circleDrawMode === 'threePoints' || circleDrawMode === 'threePointArc';
  const [circleConstructionPoints, setCircleConstructionPoints] = useState<Point[]>([]);
  const [circleConstructionIssue, setCircleConstructionIssue] = useState<string>();
  const [roiUndoEntry, setRoiUndoEntry] = useState<{ previous?: VisionRoi; afterFingerprint: string }>();
  const [measurementUndoEntry, setMeasurementUndoEntry] = useState<{ previous: MeasurementGeometry; afterFingerprint: string }>();
  const [draft, setDraft] = useState<VisionRoi>();
  const [roiDragOffset, setRoiDragOffset] = useState<Point>();
  const [brushPreview, setBrushPreview] = useState<VisionRoiBrushStroke>();

  const activeGesture = useRef<ActiveGesture | undefined>(undefined);
  const polygonPoints = useRef<OverlayPoint[]>([]);
  const pinchRef = useRef<{ distance: number; scale: number; anchor: Point } | undefined>(undefined);
  const pointerUpHandler = useRef<() => void>(() => undefined);
  const pointerCancelHandler = useRef<() => void>(() => undefined);
  const pointerMoveHandler = useRef<() => void>(() => undefined);

  const cursorPoint = cursorScreen ? { x: (cursorScreen.x - view.x) / view.scale, y: (cursorScreen.y - view.y) / view.scale } : undefined;
  const cursor = cursorPoint && cursorPoint.x >= 0 && cursorPoint.y >= 0 && cursorPoint.x < actualWidth && cursorPoint.y < actualHeight
    ? cursorPoint : undefined;

  useEffect(() => {
    if (!roiEnabled) {
      setTool('pan');
      setDraft(undefined);
      setBrushPreview(undefined);
      polygonPoints.current = [];
      activeGesture.current = undefined;
      setRoiDragOffset(undefined);
      setRoiUndoEntry(undefined);
    }
  }, [roiEnabled]);

  useEffect(() => {
    setCursor(undefined);
    setDraft(undefined);
    setBrushPreview(undefined);
    setMeasurementDraft(undefined);
    setGeometryDrawMode(false);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    setRoiUndoEntry(undefined);
    setMeasurementUndoEntry(undefined);
    activeGesture.current = undefined;
    polygonPoints.current = [];
    setRoiDragOffset(undefined);
  }, [previewUrl]);

  useEffect(() => {
    if (measurementGeometryEditable && canDrawMeasurementGeometry) return;
    setGeometryDrawMode(false);
    setMeasurementDraft(undefined);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    if (!measurementGeometryEditable) setMeasurementUndoEntry(undefined);
    if (activeGesture.current?.kind === 'geometry-draw' || activeGesture.current?.kind === 'geometry-drag') {
      activeGesture.current = undefined;
    }
  }, [measurementGeometryEditable, canDrawMeasurementGeometry]);

  const commitRoiEdit = (next?: VisionRoi) => {
    if (parameterFingerprint(roi) === parameterFingerprint(next)) return;
    setRoiUndoEntry({ previous: roi, afterFingerprint: parameterFingerprint(next) });
    onRoiChange(next);
  };

  const undoRoiEdit = () => {
    if (!roiUndoEntry) return;
    setRoiUndoEntry(undefined);
    if (roiUndoEntry.afterFingerprint === parameterFingerprint(roi)) onRoiChange(roiUndoEntry.previous);
  };

  const committedMeasurementGeometry = (geometry: MeasurementGeometry): MeasurementGeometry => {
    if (geometry.type === 'line') {
      return geometry.profileWidth === undefined ? geometry
        : { ...geometry, profileWidth: effectiveSampleWidth(geometry.profileWidth) };
    }
    const normalizedArc = normalizeCaliperCircleArc(geometry.angleStartDeg ?? -180, geometry.angleEndDeg ?? 180);
    return {
      ...geometry,
      angleStartDeg: normalizedArc.start,
      angleEndDeg: normalizedArc.end,
      band: clamp(geometry.band ?? 1, 1, 500),
      ...(geometry.profileWidth === undefined ? {} : { profileWidth: effectiveSampleWidth(geometry.profileWidth) })
    };
  };

  const commitMeasurementEdit = (next: MeasurementGeometry) => {
    if (!measurementGeometry || !onMeasurementGeometryChange) return;
    const expected = committedMeasurementGeometry(next);
    setMeasurementUndoEntry({ previous: measurementGeometry, afterFingerprint: parameterFingerprint(expected) });
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

  const stageTouchPoint = (clientX: number, clientY: number): Point | undefined => {
    const container = stageRef.current?.container();
    if (!container) return undefined;
    const rect = container.getBoundingClientRect();
    return { x: clientX - rect.left, y: clientY - rect.top };
  };

  const onStageTouchStart = (event: Konva.KonvaEventObject<TouchEvent>) => {
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
    const activeStroke: VisionRoiBrushStroke = { mode, width: brushWidth, points: [point, point] };
    activeGesture.current = { kind: 'brush', baseStrokes: roi?.type === 'Brush' ? roi.strokes : [], activeStroke };
    setDraft(undefined);
    setBrushPreview(activeStroke);
  };

  const onPointerDown = () => {
    const p = pointer();
    if (!p) return;
    setCursor(p);
    const ip = imagePoint(p);
    if (caliperDiagnosticSelection) {
      const index = nearestDiagnosticSpoke(caliperDiagnosticSelection, ip, view.scale);
      if (index !== undefined) caliperDiagnosticSelection.onSelect?.(index);
      else activeGesture.current = { kind: 'pan', pointer: p, view };
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
      activeGesture.current = { kind: 'geometry-draw', start: ip };
      setMeasurementDraft(measurementGeometry.type === 'line'
        ? { ...measurementGeometry, start: ip, end: ip }
        : { ...measurementGeometry, center: ip, radius: 0 });
      return;
    }
    if (measurementGeometry && measurementGeometryEditable) {
      const handle = hitMeasurementHandle(measurementGeometry, p, view, actualWidth, actualHeight);
      if (handle) {
        const profileWidthAngle = handle === 'profileWidth' && measurementGeometry.type === 'circle'
          ? caliperCircleProfileWidthHandle(measurementGeometry, buildCaliperCircleAngles(measurementGeometry), actualWidth, actualHeight)?.angle
          : undefined;
        activeGesture.current = { kind: 'geometry-drag', mode: handle, origin: measurementGeometry, start: ip, profileWidthAngle };
        return;
      }
    }
    if (roi && roiEnabled && tool === 'pan') {
      const handle = hitRoiHandle(roi, p, view, actualWidth, actualHeight);
      if (handle) {
        activeGesture.current = { kind: 'roi-drag', mode: handle, origin: roi, start: imagePoint(p) };
        return;
      }
      if (pointInsideRoi(roi, imagePoint(p))) {
        const moveBounds = roi.type === 'Brush' ? brushRoiBounds(roi)
          : roi.type === 'Polygon' ? polygonRoiBounds(roi) : undefined;
        activeGesture.current = { kind: 'roi-drag', mode: 'move', origin: roi, start: imagePoint(p), moveBounds };
        if (roi.type === 'Brush' || roi.type === 'Polygon') setRoiDragOffset({ x: 0, y: 0 });
        return;
      }
    }
    if (tool === 'pan') {
      activeGesture.current = { kind: 'pan', pointer: p, view };
      return;
    }
    if (!roiEnabled || tool === 'polygon' || !inside(ip)) return;
    if (tool === 'brush') { beginBrushStroke(ip, 'Add'); return; }
    if (tool === 'eraser') {
      if (roi?.type === 'Brush') beginBrushStroke(ip, 'Erase');
      return;
    }
    activeGesture.current = { kind: 'roi-draw', start: ip };
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

    const gesture = activeGesture.current;
    if (!gesture) return;
    switch (gesture.kind) {
      case 'geometry-draw': {
        if (!measurementGeometry) return;
        const end = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
        setMeasurementDraft(measurementGeometry.type === 'line'
          ? { ...measurementGeometry, start: gesture.start, end: boundedMeasurementPoint(ip) }
          : circleDrawMode === 'diameter'
            ? { ...measurementGeometry, center: { x: (gesture.start.x + end.x) / 2, y: (gesture.start.y + end.y) / 2 },
              radius: Math.hypot(end.x - gesture.start.x, end.y - gesture.start.y) / 2 }
            : { ...measurementGeometry, center: gesture.start,
              radius: Math.min(Math.hypot(end.x - gesture.start.x, end.y - gesture.start.y), maxInteractiveCircleRadius(gesture.start, actualWidth, actualHeight)) });
        return;
      }
      case 'geometry-drag': {
        const next = applyMeasurementDrag({
          origin: gesture.origin, mode: gesture.mode, start: gesture.start, pointer: ip,
          current: { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) },
          profileWidthAngle: gesture.profileWidthAngle, imageWidth: actualWidth, imageHeight: actualHeight
        });
        if (next) setMeasurementDraft(next);
        return;
      }
      case 'brush': {
        const clamped = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
        const previous = gesture.activeStroke.points[gesture.activeStroke.points.length - 1];
        if (Math.hypot(clamped.x - previous.x, clamped.y - previous.y) < 0.35) return;
        const activeStroke = { ...gesture.activeStroke, points: [...gesture.activeStroke.points, clamped] };
        activeGesture.current = { ...gesture, activeStroke };
        setBrushPreview(activeStroke);
        return;
      }
      case 'roi-drag': {
        if ((gesture.origin.type === 'Brush' || gesture.origin.type === 'Polygon') && gesture.moveBounds) {
          setRoiDragOffset(clampRoiMove(gesture.moveBounds,
            { x: ip.x - gesture.start.x, y: ip.y - gesture.start.y }, actualWidth, actualHeight));
          return;
        }
        const next = applyRoiDrag(gesture.origin, gesture.mode, gesture.start, ip, actualWidth, actualHeight, gesture.moveBounds);
        if (next) setDraft(next);
        return;
      }
      case 'pan': {
        setView({ ...gesture.view, x: gesture.view.x + p.x - gesture.pointer.x, y: gesture.view.y + p.y - gesture.pointer.y });
        return;
      }
      case 'roi-draw': {
        if (!roiEnabled) return;
        const end = { x: clamp(ip.x, 0, actualWidth), y: clamp(ip.y, 0, actualHeight) };
        if (tool === 'rect') setDraft(normalizeRect(gesture.start, end));
        if (tool === 'circle') {
          const dx = end.x - gesture.start.x;
          const dy = end.y - gesture.start.y;
          setDraft({ type: 'Circle', x: gesture.start.x, y: gesture.start.y,
            radius: Math.min(Math.hypot(dx, dy), maxInteractiveCircleRadius(gesture.start, actualWidth, actualHeight)) });
        }
      }
    }
  };

  const onPointerUp = () => {
    const gesture = activeGesture.current;
    activeGesture.current = undefined;
    if (!gesture) return;
    switch (gesture.kind) {
      case 'pan':
        return;
      case 'geometry-draw':
      case 'geometry-drag': {
        const next = measurementDraft;
        if (next && onMeasurementGeometryChange && (next.type === 'line'
          ? Math.hypot(next.end.x - next.start.x, next.end.y - next.start.y) >= 2
          : next.radius >= 1)) commitMeasurementEdit(next);
        setMeasurementDraft(undefined);
        if (gesture.kind === 'geometry-draw') setGeometryDrawMode(false);
        return;
      }
      case 'brush':
        setBrushPreview(undefined);
        setDraft(undefined);
        commitRoiEdit({ type: 'Brush', strokes: [...gesture.baseStrokes, gesture.activeStroke] });
        return;
      case 'roi-drag': {
        const next = gesture.origin.type === 'Brush' || gesture.origin.type === 'Polygon' && gesture.mode === 'move'
          ? applyRoiDrag(gesture.origin, gesture.mode, { x: 0, y: 0 }, roiDragOffset ?? { x: 0, y: 0 },
            actualWidth, actualHeight, gesture.moveBounds)
          : draft;
        if (next && (next.type !== 'Rectangle' || (next.width >= 2 && next.height >= 2)) && (next.type !== 'Circle' || next.radius >= 1)) {
          commitRoiEdit(next);
        }
        setRoiDragOffset(undefined);
        setDraft(undefined);
        return;
      }
      case 'roi-draw':
        if (draft) {
          if (draft.type === 'Rectangle' && draft.width >= 2 && draft.height >= 2) commitRoiEdit(draft);
          if (draft.type === 'Circle' && draft.radius >= 1) commitRoiEdit(draft);
        }
        setDraft(undefined);
    }
  };

  const onPointerCancel = () => {
    activeGesture.current = undefined;
    setRoiDragOffset(undefined);
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
      if (!activeGesture.current) return;
      if (event.target instanceof Element && hostRef.current?.contains(event.target)) return;
      stageRef.current?.setPointersPositions?.(event);
      pointerMoveHandler.current();
    };
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
  }, [hostRef, stageRef]);

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
    if (points.length > 1 && points.at(-1)!.x === points[0].x && points.at(-1)!.y === points[0].y)
      points.pop();
    if (points.length >= 3) commitRoiEdit({ type: 'Polygon', points });
    polygonPoints.current = [];
    setDraft(undefined);
  };

  const clearRoi = () => {
    polygonPoints.current = [];
    if (activeGesture.current?.kind === 'brush') activeGesture.current = undefined;
    setBrushPreview(undefined);
    setDraft(undefined);
    commitRoiEdit(undefined);
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        if (geometryDrawMode) { e.preventDefault(); e.stopPropagation(); }
        activeGesture.current = undefined;
        setRoiDragOffset(undefined);
        polygonPoints.current = [];
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

  const changeTool = (value: ToolMode) => {
    polygonPoints.current = [];
    activeGesture.current = undefined;
    setBrushPreview(undefined);
    setGeometryDrawMode(false);
    setMeasurementDraft(undefined);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    setRoiDragOffset(undefined);
    setDraft(undefined);
    setTool(value);
  };

  const toggleGeometryDrawMode = () => {
    setMeasurementDraft(undefined);
    if (activeGesture.current?.kind === 'geometry-draw') activeGesture.current = undefined;
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    setGeometryDrawMode(value => !value);
    setTool('pan');
  };

  const changeCircleDrawMode = (value: CircleDrawMode) => {
    setCircleDrawMode(value);
    setCircleConstructionPoints([]);
    setCircleConstructionIssue(undefined);
    setMeasurementDraft(undefined);
    if (activeGesture.current?.kind === 'geometry-draw') activeGesture.current = undefined;
  };

  const undoConstructionPoint = () => {
    setCircleConstructionPoints(points => points.slice(0, -1));
    setMeasurementDraft(undefined);
    setCircleConstructionIssue(undefined);
  };

  return {
    tool,
    cursorScreen,
    cursor,
    setCursor,
    brushWidth,
    setBrushWidth,
    draft,
    measurementDraft,
    geometryDrawMode,
    circleDrawMode,
    isThreePointDrawing,
    circleConstructionPoints,
    circleConstructionIssue,
    roiDragOffset,
    brushPreview,
    isPanning: activeGesture.current?.kind === 'pan',
    isDragging: activeGesture.current?.kind === 'roi-drag' || activeGesture.current?.kind === 'geometry-drag',
    roiUndoAvailable: Boolean(roiUndoEntry && roiUndoEntry.afterFingerprint === parameterFingerprint(roi)),
    measurementUndoAvailable: Boolean(measurementUndoEntry && measurementGeometry
      && measurementUndoEntry.afterFingerprint === parameterFingerprint(measurementGeometry)),
    changeTool,
    toggleGeometryDrawMode,
    changeCircleDrawMode,
    undoConstructionPoint,
    undoRoiEdit,
    undoMeasurementEdit,
    clearRoi,
    onWheel,
    onPointerDown,
    onPointerMove,
    onPointerUp,
    onPointerCancel,
    onStageTouchStart,
    onStageTouchMove,
    onStageTouchEnd,
    onClick,
    commitPolygon
  };
}
