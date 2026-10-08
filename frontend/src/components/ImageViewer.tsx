import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Button, Segmented, Select, Space, Switch, Tag, Tooltip } from 'antd';
import { Circle, Group, Image as KonvaImage, Layer, Line, Rect, Stage, Text } from 'react-konva';
import type { OverlayPoint, VisionOverlay, VisionRoi } from '../types';

type ToolMode = 'pan' | 'rect' | 'circle' | 'polygon';
type ViewState = { scale: number; x: number; y: number };
type Point = { x: number; y: number };
type RoiDragMode = 'move' | 'nw' | 'ne' | 'sw' | 'se' | 'e' | 'w' | 'n' | 's';

const clamp = (v: number, min: number, max: number) => Math.min(max, Math.max(min, v));
const flatten = (points?: OverlayPoint[] | null) => (points ?? []).flatMap((p) => [p.x, p.y]);
/** 控制点命中半径（屏幕像素），缩放后仍保持同样的手感 */
const HANDLE_HIT = 10;

function useHtmlImage(url?: string) {
  const [image, setImage] = useState<HTMLImageElement>();
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    if (!url) { setImage(undefined); setFailed(false); return; }
    setFailed(false);
    const img = new window.Image();
    img.onload = () => setImage(img);
    img.onerror = () => { setImage(undefined); setFailed(true); };
    img.src = url;
    return () => { img.onload = null; img.onerror = null; };
  }, [url]);
  return { image, failed };
}

function normalizeRect(start: Point, end: Point): VisionRoi {
  return {
    type: 'Rectangle',
    x: Math.min(start.x, end.x),
    y: Math.min(start.y, end.y),
    width: Math.abs(end.x - start.x),
    height: Math.abs(end.y - start.y)
  };
}

/** ROI 包围盒（图像坐标），用于“定位选区”的视野适配 */
function roiBounds(roi: VisionRoi) {
  if (roi.type === 'Rectangle') return { x: roi.x, y: roi.y, width: Math.max(1, roi.width), height: Math.max(1, roi.height) };
  if (roi.type === 'Circle') return { x: roi.x - roi.radius, y: roi.y - roi.radius, width: Math.max(1, roi.radius * 2), height: Math.max(1, roi.radius * 2) };
  const xs = roi.points.map((p) => p.x);
  const ys = roi.points.map((p) => p.y);
  const minX = Math.min(...xs);
  const minY = Math.min(...ys);
  return { x: minX, y: minY, width: Math.max(1, Math.max(...xs) - minX), height: Math.max(1, Math.max(...ys) - minY) };
}

function pointInsideRoi(roi: VisionRoi, point: Point): boolean {
  if (roi.type === 'Rectangle') return point.x >= roi.x && point.x <= roi.x + roi.width && point.y >= roi.y && point.y <= roi.y + roi.height;
  if (roi.type === 'Circle') return Math.hypot(point.x - roi.x, point.y - roi.y) <= roi.radius;
  let inside = false;
  const pts = roi.points;
  for (let i = 0, j = pts.length - 1; i < pts.length; j = i++) {
    const xi = pts[i].x; const yi = pts[i].y; const xj = pts[j].x; const yj = pts[j].y;
    if ((yi > point.y) !== (yj > point.y) && point.x < ((xj - xi) * (point.y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

/** 按拖动模式生成新的 ROI：矩形四角缩放 / 圆形按半径 / 多边形整体平移；拖动过程始终限制在图像范围内 */
function applyRoiDrag(origin: VisionRoi, mode: RoiDragMode, start: Point, current: Point, maxW: number, maxH: number): VisionRoi | undefined {
  const dx = current.x - start.x;
  const dy = current.y - start.y;
  if (origin.type === 'Rectangle') {
    if (mode === 'move') {
      return { ...origin, x: clamp(origin.x + dx, 0, Math.max(0, maxW - origin.width)), y: clamp(origin.y + dy, 0, Math.max(0, maxH - origin.height)) };
    }
    let x1 = origin.x; let y1 = origin.y; let x2 = origin.x + origin.width; let y2 = origin.y + origin.height;
    if (mode.includes('w')) x1 = clamp(origin.x + dx, 0, x2 - 2);
    if (mode.includes('n')) y1 = clamp(origin.y + dy, 0, y2 - 2);
    if (mode.includes('e')) x2 = clamp(origin.x + origin.width + dx, x1 + 2, maxW);
    if (mode.includes('s')) y2 = clamp(origin.y + origin.height + dy, y1 + 2, maxH);
    return { type: 'Rectangle', x: x1, y: y1, width: x2 - x1, height: y2 - y1 };
  }
  if (origin.type === 'Circle') {
    if (mode === 'move') {
      return { ...origin, x: clamp(origin.x + dx, 0, maxW), y: clamp(origin.y + dy, 0, maxH) };
    }
    return { ...origin, radius: clamp(Math.hypot(current.x - origin.x, current.y - origin.y), 2, Math.max(maxW, maxH)) };
  }
  if (mode === 'move') {
    return { type: 'Polygon', points: origin.points.map((p) => ({ x: clamp(p.x + dx, 0, maxW), y: clamp(p.y + dy, 0, maxH) })) };
  }
  return undefined;
}

function OverlayShape({ overlay, scale }: { overlay: VisionOverlay; scale: number }) {
  const stroke = overlay.stroke ?? '#52c41a';
  const strokeWidth = (overlay.strokeWidth ?? 2) / scale;
  const x = overlay.x ?? 0;
  const y = overlay.y ?? 0;
  switch (overlay.type) {
    case 'Point': {
      const arm = 9 / scale;
      return <Group listening={false}>
        <Line points={[x - arm, y, x + arm, y]} stroke={stroke} strokeWidth={strokeWidth} />
        <Line points={[x, y - arm, x, y + arm]} stroke={stroke} strokeWidth={strokeWidth} />
      </Group>;
    }
    case 'Line':
      return <Line listening={false} points={[x, y, overlay.x2 ?? x, overlay.y2 ?? y]} stroke={stroke} strokeWidth={strokeWidth} />;
    case 'Circle':
      return <Circle listening={false} x={x} y={y} radius={overlay.radius ?? 0} stroke={stroke} strokeWidth={strokeWidth} fill={overlay.fill ?? undefined} />;
    case 'Rectangle':
      return <Rect listening={false} x={x} y={y} width={overlay.width ?? 0} height={overlay.height ?? 0} rotation={overlay.angleDeg ?? 0} stroke={stroke} strokeWidth={strokeWidth} fill={overlay.fill ?? undefined} />;
    case 'Polygon':
    case 'Contour':
      return <Line listening={false} points={flatten(overlay.points)} closed={true} stroke={stroke} strokeWidth={strokeWidth} fill={overlay.fill ?? undefined} />;
    case 'Text':
      return <Text listening={false} x={x} y={y} text={overlay.label ?? ''} fill={stroke} fontSize={14 / scale} fontFamily="monospace" />;
    default:
      return null;
  }
}

function RoiShape({ roi, scale, draft = false }: { roi?: VisionRoi; scale: number; draft?: boolean }) {
  if (!roi) return null;
  const stroke = draft ? '#ffd666' : '#ff4d4f';
  const sw = 2 / scale;
  if (roi.type === 'Rectangle')
    return <Rect listening={false} x={roi.x} y={roi.y} width={roi.width} height={roi.height} stroke={stroke} strokeWidth={sw} dash={[7 / scale, 4 / scale]} />;
  if (roi.type === 'Circle')
    return <Circle listening={false} x={roi.x} y={roi.y} radius={roi.radius} stroke={stroke} strokeWidth={sw} dash={[7 / scale, 4 / scale]} />;
  return <Line listening={false} points={flatten(roi.points)} closed={roi.points.length >= 3 && !draft} stroke={stroke} strokeWidth={sw} dash={[7 / scale, 4 / scale]} />;
}

/** 选区控制点：矩形四角 / 圆形四向半径点；尺寸按屏幕像素折算，缩放时手感不变 */
function RoiHandles({ roi, scale }: { roi: VisionRoi; scale: number }) {
  const size = 9 / scale;
  const dots: Point[] = roi.type === 'Rectangle'
    ? [{ x: roi.x, y: roi.y }, { x: roi.x + roi.width, y: roi.y }, { x: roi.x, y: roi.y + roi.height }, { x: roi.x + roi.width, y: roi.y + roi.height }]
    : roi.type === 'Circle'
      ? [{ x: roi.x + roi.radius, y: roi.y }, { x: roi.x - roi.radius, y: roi.y }, { x: roi.x, y: roi.y - roi.radius }, { x: roi.x, y: roi.y + roi.radius }]
      : [];
  if (dots.length === 0) return null;
  return <Group listening={false}>
    {dots.map((dot, index) => (
      <Rect key={index} x={dot.x - size / 2} y={dot.y - size / 2} width={size} height={size} fill="#ff4d4f" stroke="#ffffff" strokeWidth={1 / scale} />
    ))}
  </Group>;
}

export default function ImageViewer({
  previewUrl,
  imageWidth,
  imageHeight,
  overlays,
  roi,
  roiEnabled,
  roiTargetLabel,
  loading,
  onRoiChange,
  nodeLabels,
  focusTick
}: {
  previewUrl?: string;
  imageWidth?: number;
  imageHeight?: number;
  overlays: VisionOverlay[];
  roi?: VisionRoi;
  roiEnabled: boolean;
  roiTargetLabel?: string;
  loading?: boolean;
  onRoiChange: (roi?: VisionRoi) => void;
  /** 覆盖层按节点筛选时的显示名映射 */
  nodeLabels?: Record<string, string>;
  /** 递增信号：来自属性面板“定位选区”，把视图适配到当前 ROI 区域 */
  focusTick?: number;
}) {
  const hostRef = useRef<HTMLDivElement>(null);
  const stageRef = useRef<any>(null);
  const [size, setSize] = useState({ width: 400, height: 260 });
  const [view, setView] = useState<ViewState>({ scale: 1, x: 0, y: 0 });
  const [tool, setTool] = useState<ToolMode>('pan');
  const [cursor, setCursor] = useState<Point>();
  const [showOverlay, setShowOverlay] = useState(true);
  const [overlayFilter, setOverlayFilter] = useState<string[]>([]);
  const [draft, setDraft] = useState<VisionRoi>();
  const gestureStart = useRef<Point | undefined>(undefined);
  const panStart = useRef<{ pointer: Point; view: ViewState } | undefined>(undefined);
  const roiDrag = useRef<{ mode: RoiDragMode; origin: VisionRoi; start: Point } | undefined>(undefined);
  const polygonPoints = useRef<OverlayPoint[]>([]);
  const lastFocusTick = useRef(focusTick ?? 0);
  const { image, failed } = useHtmlImage(previewUrl);
  const actualWidth = imageWidth || image?.naturalWidth || 0;
  const actualHeight = imageHeight || image?.naturalHeight || 0;

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const observer = new ResizeObserver(([entry]) => {
      const r = entry.contentRect;
      setSize({ width: Math.max(10, r.width), height: Math.max(10, r.height) });
    });
    observer.observe(host);
    return () => observer.disconnect();
  }, []);

  // 视图适配目标：fit=全图，定位选区=ROI 包围盒。容器尺寸变化时保持同一目标重适配，
  // 避免“适应窗口”式重置覆盖刚触发的定位意图
  const fitTargetRef = useRef<{ x: number; y: number; width: number; height: number } | undefined>(undefined);
  const setViewToTarget = (target: { x: number; y: number; width: number; height: number }, pad: number) => {
    // 留白按容器尺寸自适应：容器很矮（如底部面板展开时）时固定留白会挤掉大部分可用空间
    const effPad = Math.min(pad, Math.min(size.width, size.height) * 0.12);
    const scale = clamp(Math.min((size.width - effPad * 2) / target.width, (size.height - effPad * 2) / target.height), 0.02, 30);
    setView({
      scale,
      x: size.width / 2 - (target.x + target.width / 2) * scale,
      y: size.height / 2 - (target.y + target.height / 2) * scale
    });
  };

  const fit = () => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setViewToTarget(fitTargetRef.current, 12);
  };

  const oneToOne = () => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setView({ scale: 1, x: (size.width - actualWidth) / 2, y: (size.height - actualHeight) / 2 });
  };

  // 图像（尺寸）变化：目标重置为全图并适配
  useEffect(() => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setViewToTarget(fitTargetRef.current, 12);
  }, [actualWidth, actualHeight]);

  // 容器尺寸变化：按当前目标（全图或 ROI）重新适配
  useEffect(() => {
    if (!fitTargetRef.current) return;
    setViewToTarget(fitTargetRef.current, 12);
  }, [size.width, size.height]);

  // 定位选区：属性面板每次触发递增 focusTick，这里把视图适配到当前 ROI 包围盒
  useEffect(() => {
    if (focusTick === undefined || focusTick === lastFocusTick.current) return;
    lastFocusTick.current = focusTick;
    if (!roi) return;
    fitTargetRef.current = roiBounds(roi);
    setViewToTarget(fitTargetRef.current, 28);
  }, [focusTick, roi, size.width, size.height]);

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

  /** 命中选区控制点（屏幕距离判定）：返回拖动模式 */
  const hitRoiHandle = (target: VisionRoi, screen: Point): RoiDragMode | undefined => {
    const near = (x: number, y: number) => Math.hypot(screen.x - (view.x + x * view.scale), screen.y - (view.y + y * view.scale)) <= HANDLE_HIT;
    if (target.type === 'Rectangle') {
      if (near(target.x, target.y)) return 'nw';
      if (near(target.x + target.width, target.y)) return 'ne';
      if (near(target.x, target.y + target.height)) return 'sw';
      if (near(target.x + target.width, target.y + target.height)) return 'se';
      return undefined;
    }
    if (target.type === 'Circle') {
      if (near(target.x + target.radius, target.y)) return 'e';
      if (near(target.x - target.radius, target.y)) return 'w';
      if (near(target.x, target.y - target.radius)) return 'n';
      if (near(target.x, target.y + target.radius)) return 's';
      return undefined;
    }
    return undefined;
  };

  const onWheel = (event: any) => {
    event.evt.preventDefault();
    const p = pointer();
    if (!p || !actualWidth) return;
    const before = imagePoint(p);
    const factor = event.evt.deltaY > 0 ? 0.9 : 1.1;
    const scale = clamp(view.scale * factor, 0.02, 30);
    setView({ scale, x: p.x - before.x * scale, y: p.y - before.y * scale });
  };

  const onPointerDown = () => {
    const p = pointer();
    if (!p) return;
    // 已有选区时优先响应拖动：控制点缩放 > 选区内部平移 > 平移画布 / 新建选区
    if (roi && roiEnabled && tool !== 'polygon') {
      const handle = hitRoiHandle(roi, p);
      if (handle) {
        roiDrag.current = { mode: handle, origin: roi, start: imagePoint(p) };
        return;
      }
      if (pointInsideRoi(roi, imagePoint(p))) {
        roiDrag.current = { mode: 'move', origin: roi, start: imagePoint(p) };
        return;
      }
    }
    if (tool === 'pan') {
      panStart.current = { pointer: p, view };
      return;
    }
    if (!roiEnabled || tool === 'polygon') return;
    const ip = imagePoint(p);
    if (!inside(ip)) return;
    gestureStart.current = ip;
    if (tool === 'rect') setDraft(normalizeRect(ip, ip));
    if (tool === 'circle') setDraft({ type: 'Circle', x: ip.x, y: ip.y, radius: 0 });
  };

  const onPointerMove = () => {
    const p = pointer();
    if (!p) return;
    const ip = imagePoint(p);
    setCursor(inside(ip) ? ip : undefined);
    if (roiDrag.current) {
      const next = applyRoiDrag(roiDrag.current.origin, roiDrag.current.mode, roiDrag.current.start, ip, actualWidth, actualHeight);
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
      setDraft({ type: 'Circle', x: gestureStart.current.x, y: gestureStart.current.y, radius: Math.hypot(dx, dy) });
    }
  };

  const onPointerUp = () => {
    panStart.current = undefined;
    if (roiDrag.current) {
      roiDrag.current = undefined;
      if (draft && (draft.type !== 'Rectangle' || (draft.width >= 2 && draft.height >= 2)) && (draft.type !== 'Circle' || draft.radius >= 2)) {
        onRoiChange(draft);
      }
      setDraft(undefined);
      return;
    }
    if (gestureStart.current && draft) {
      if (draft.type === 'Rectangle' && draft.width >= 2 && draft.height >= 2) onRoiChange(draft);
      if (draft.type === 'Circle' && draft.radius >= 2) onRoiChange(draft);
    }
    gestureStart.current = undefined;
    setDraft(undefined);
  };

  const onClick = () => {
    if (tool !== 'polygon' || !roiEnabled) return;
    const p = pointer();
    if (!p) return;
    const ip = imagePoint(p);
    if (!inside(ip)) return;
    polygonPoints.current = [...polygonPoints.current, ip];
    setDraft({ type: 'Polygon', points: polygonPoints.current });
  };

  const commitPolygon = () => {
    if (tool !== 'polygon' || polygonPoints.current.length < 3) return;
    onRoiChange({ type: 'Polygon', points: polygonPoints.current });
    polygonPoints.current = [];
    setDraft(undefined);
  };

  const clearRoi = () => {
    polygonPoints.current = [];
    setDraft(undefined);
    onRoiChange(undefined);
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        gestureStart.current = undefined;
        roiDrag.current = undefined;
        polygonPoints.current = [];
        setDraft(undefined);
        setTool('pan');
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const cursorStyle = useMemo(() => {
    if (tool === 'pan') return panStart.current ? 'grabbing' : 'grab';
    return 'crosshair';
  }, [tool, view]); // view forces cursor refresh during pan

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

  return (
    <div className="vision-viewer">
      <div className="viewer-toolbar">
        <Space size={5} wrap>
          <Segmented
            size="small"
            value={tool}
            onChange={(value) => { polygonPoints.current = []; setDraft(undefined); setTool(value as ToolMode); }}
            options={[
              { value: 'pan', label: <Tooltip title="平移">平移</Tooltip> },
              { value: 'rect', disabled: !roiEnabled, label: <Tooltip title="矩形选区">矩形</Tooltip> },
              { value: 'circle', disabled: !roiEnabled, label: <Tooltip title="圆形选区">圆形</Tooltip> },
              { value: 'polygon', disabled: !roiEnabled, label: <Tooltip title="多边形选区 · 双击完成">多边形</Tooltip> }
            ]}
          />
          <span className="viewer-toolbar-divider" />
          <Button size="small" onClick={fit}>适应窗口</Button>
          <Button size="small" onClick={oneToOne}>原始比例</Button>
          <Button size="small" danger disabled={!roi} onClick={clearRoi}>清除选区</Button>
          <span className="viewer-toolbar-divider" />
          <span className="viewer-toggle">显示覆盖层 <Switch size="small" checked={showOverlay} onChange={setShowOverlay} /></span>
          {/* 覆盖层按节点筛选：仅当存在多个节点来源时才有意义（单节点输出视图 App 已按节点过滤） */}
          {overlayNodeIds.length > 1 && (
            <Select
              size="small"
              mode="multiple"
              allowClear
              maxTagCount={1}
              style={{ minWidth: 118, maxWidth: 190 }}
              placeholder="覆盖层：全部节点"
              title="按节点筛选覆盖层（可多选）"
              value={overlayFilter}
              onChange={setOverlayFilter}
              options={overlayNodeIds.map((id) => ({ value: id, label: nodeLabels?.[id] ?? id }))}
            />
          )}
          <Tag>{Math.round(view.scale * 100)}%</Tag>
        </Space>
        <div className="viewer-readout">
          {actualWidth > 0 && <span>{actualWidth} × {actualHeight}</span>}
          {cursor && <span>X {cursor.x.toFixed(1)} · Y {cursor.y.toFixed(1)}</span>}
          <span>选区 → {roiTargetLabel || '请选择图像节点'}</span>
        </div>
      </div>

      <div ref={hostRef} className={`viewer-stage ${loading ? 'is-loading' : ''}`} style={{ cursor: cursorStyle }}>
        {!previewUrl && !failed && <div className="image-placeholder">运行工作流以显示图像和矢量覆盖层</div>}
        {failed && <div className="image-placeholder error">图像加载失败：预览不可用或已被清理</div>}
        {previewUrl && !failed && (
          <Stage
            ref={stageRef}
            width={size.width}
            height={size.height}
            onWheel={onWheel}
            onMouseDown={onPointerDown}
            onTouchStart={onPointerDown}
            onMouseMove={onPointerMove}
            onTouchMove={onPointerMove}
            onMouseUp={onPointerUp}
            onTouchEnd={onPointerUp}
            onClick={onClick}
            onDblClick={commitPolygon}
            onMouseLeave={() => { setCursor(undefined); panStart.current = undefined; }}
          >
            <Layer>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                {image && <KonvaImage image={image} width={actualWidth} height={actualHeight} listening={false} />}
              </Group>
            </Layer>
            <Layer>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                {showOverlay && visibleOverlays.map((o) => <OverlayShape key={`${o.nodeId ?? ''}-${o.id}`} overlay={o} scale={view.scale} />)}
              </Group>
            </Layer>
            <Layer>
              <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
                <RoiShape roi={roi} scale={view.scale} />
                <RoiShape roi={draft} scale={view.scale} draft />
                {roi && roiEnabled && !draft && <RoiHandles roi={roi} scale={view.scale} />}
              </Group>
            </Layer>
          </Stage>
        )}
        {loading && <div className="viewer-loading">运行中…</div>}
      </div>
    </div>
  );
}