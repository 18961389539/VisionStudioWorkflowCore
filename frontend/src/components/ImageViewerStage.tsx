/**
 * ImageViewer 的 Konva 舞台渲染。
 *
 * 只负责把「已经算好的视图变换与几何」画出来并转发指针事件；任何状态机、
 * 命中判定、几何运算都不在这里。分层的顺序即压盖顺序，改动前先读下面每层的注释。
 */
import type { RefObject } from 'react';
import type Konva from 'konva';
import { Circle, Group, Image as KonvaImage, Layer, Line, Stage, Text } from 'react-konva';
import { Button } from 'antd';
import type { VisionOverlay, VisionRoi, VisionRoiBrushStroke } from '../types';
import { BrushRoiShape, CaliperDiagnosticShape, MeasurementGeometryShape, OverlayShape, RoiHandles, RoiShape } from './imageViewerUtils';
import type { MeasurementGeometry } from './imageViewerUtils';
import type { CaliperDiagnosticSelection, CircleDrawMode, Point, ToolMode, ViewState } from './imageViewerTypes';

export function ImageViewerStage({
  hostRef,
  stageRef,
  size,
  view,
  image,
  imageWidth,
  imageHeight,
  pixelated,
  cursorStyle,
  loading,
  previewUrl,
  failed,
  emptyHint,
  emptyAction,
  showOverlay,
  visibleOverlays,
  measurementGeometry,
  measurementDraft,
  measurementGeometryEditable,
  geometryDrawMode,
  circleConstructionPoints,
  circleDrawMode,
  cursor,
  measurementMarkers,
  caliperDiagnosticSelection,
  roi,
  roiEnabled,
  tool,
  draft,
  brushPreview,
  roiDragOffset,
  onWheel,
  onPointerDown,
  onStageTouchStart,
  onPointerMove,
  onStageTouchMove,
  onPointerUp,
  onStageTouchEnd,
  onClick,
  onDblClick,
  onMouseLeave
}: {
  hostRef: RefObject<HTMLDivElement | null>;
  stageRef: RefObject<Konva.Stage | null>;
  size: { width: number; height: number };
  view: ViewState;
  image?: HTMLImageElement;
  imageWidth: number;
  imageHeight: number;
  pixelated: boolean;
  cursorStyle: string;
  loading: boolean;
  previewUrl?: string;
  failed: boolean;
  emptyHint: string;
  emptyAction?: { label: string; onClick: () => void; disabled?: boolean; title?: string };
  showOverlay: boolean;
  visibleOverlays: VisionOverlay[];
  measurementGeometry?: MeasurementGeometry;
  measurementDraft?: MeasurementGeometry;
  measurementGeometryEditable: boolean;
  geometryDrawMode: boolean;
  circleConstructionPoints: Point[];
  circleDrawMode: CircleDrawMode;
  /** 图像坐标下的光标位置，用于三点圈选时的预览连线。 */
  cursor?: Point;
  measurementMarkers: VisionOverlay[];
  caliperDiagnosticSelection?: CaliperDiagnosticSelection;
  roi?: VisionRoi;
  roiEnabled: boolean;
  tool: ToolMode;
  draft?: VisionRoi;
  brushPreview?: VisionRoiBrushStroke;
  roiDragOffset?: Point;
  onWheel: (event: Konva.KonvaEventObject<WheelEvent>) => void;
  onPointerDown: () => void;
  onStageTouchStart: (event: Konva.KonvaEventObject<TouchEvent>) => void;
  onPointerMove: () => void;
  onStageTouchMove: (event: Konva.KonvaEventObject<TouchEvent>) => void;
  onPointerUp: () => void;
  onStageTouchEnd: () => void;
  onClick: () => void;
  onDblClick: () => void;
  onMouseLeave: () => void;
}) {
  return (
    <div ref={hostRef} className={`viewer-stage ${loading ? 'is-loading' : ''}`} style={{ cursor: cursorStyle }}>
      {!previewUrl && !failed && <div className="image-placeholder">
        <span>{emptyHint}</span>
        {emptyAction && <Button size="small" type="primary" disabled={emptyAction.disabled} title={emptyAction.title} onClick={emptyAction.onClick}>{emptyAction.label}</Button>}
      </div>}
      {failed && <div className="image-placeholder error">图像加载失败：预览不可用或已被清理</div>}
      {previewUrl && !failed && (
        <Stage
          ref={stageRef}
          width={size.width}
          height={size.height}
          onWheel={onWheel}
          onMouseDown={onPointerDown}
          onTouchStart={onStageTouchStart}
          onMouseMove={onPointerMove}
          onTouchMove={onStageTouchMove}
          onMouseUp={onPointerUp}
          onTouchEnd={onStageTouchEnd}
          onClick={onClick}
          onDblClick={onDblClick}
          onMouseLeave={onMouseLeave}
        >
          {/* 1. 底图：pixelated 用最近邻插值，放大时保留真实像素方块 */}
          <Layer imageSmoothingEnabled={!pixelated}>
            <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
              {image && <KonvaImage image={image} width={imageWidth} height={imageHeight} listening={false} />}
            </Group>
          </Layer>
          {/* 2. 运行结果覆盖层：受「显示覆盖层」开关与节点筛选控制 */}
          <Layer>
            <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
              {showOverlay && visibleOverlays.map((o) => <OverlayShape key={`${o.nodeId ?? ''}-${o.id}`} overlay={o} scale={view.scale} />)}
            </Group>
          </Layer>
          {/* 3. 卡尺搜索几何：绘制中显示 draft，否则显示已提交几何 */}
          {measurementGeometry && <Layer>
            <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
              <MeasurementGeometryShape geometry={measurementDraft ?? measurementGeometry} scale={view.scale} editable={measurementGeometryEditable && !geometryDrawMode}
                imageWidth={imageWidth} imageHeight={imageHeight} />
              {/* 三点圈选的已选点与跟随光标预览线 */}
              {circleConstructionPoints.length > 0 && <Group listening={false}>
                <Line points={[...circleConstructionPoints, ...(cursor ? [cursor] : [])].flatMap(point => [point.x, point.y])}
                  stroke="#fa8c16" strokeWidth={1 / view.scale} dash={[4 / view.scale, 4 / view.scale]} />
                {circleConstructionPoints.map((point, index) => <Group key={index}>
                  <Circle name={`circle-construction-point-${index}`} x={point.x} y={point.y} radius={4 / view.scale}
                    fill="#fa8c16" stroke="#fff" strokeWidth={1.5 / view.scale} />
                  <Text x={point.x + 7 / view.scale} y={point.y - 14 / view.scale}
                    text={circleDrawMode === 'threePointArc' ? ['端点 1', '经过点', '端点 2'][index] : `${index + 1}`}
                    fill="#fa8c16" fontSize={12 / view.scale} />
                </Group>)}
              </Group>}
            </Group>
          </Layer>}
          {/* 4. 诊断光标与候选：压在搜索几何之上，保证拾取时看得见 */}
          {showOverlay && measurementMarkers.length > 0 && <Layer>
            <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
              {measurementMarkers.map(marker => <OverlayShape key={marker.id} overlay={marker} scale={view.scale} />)}
            </Group>
          </Layer>}
          {caliperDiagnosticSelection && <Layer>
            <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
              <CaliperDiagnosticShape selection={caliperDiagnosticSelection} scale={view.scale} />
            </Group>
          </Layer>}
          {/* 5. 选区与草稿：笔刷型单独走多段路径渲染，其余共用 RoiShape */}
          <Layer>
            <Group x={view.x} y={view.y} scaleX={view.scale} scaleY={view.scale}>
              {roiDragOffset && (roi?.type === 'Brush' || roi?.type === 'Polygon')
                ? <Group x={roiDragOffset.x} y={roiDragOffset.y}>
                  <RoiShape roi={roi} scale={view.scale} />
                  {roi.type === 'Polygon' && roiEnabled && <RoiHandles roi={roi} scale={view.scale} imageWidth={imageWidth} imageHeight={imageHeight} />}
                </Group>
                : <>
                  {brushPreview || roi?.type === 'Brush'
                    ? <>
                      <BrushRoiShape strokes={roi?.type === 'Brush' ? roi.strokes : []} activeStroke={brushPreview} />
                      {draft?.type !== 'Brush' && <RoiShape roi={draft} scale={view.scale} draft />}
                    </>
                    : <>
                      <RoiShape roi={roi} scale={view.scale} />
                      <RoiShape roi={draft} scale={view.scale} draft />
                    </>}
                </>}
              {roi && roiEnabled && tool === 'pan' && !draft && !brushPreview && !roiDragOffset
                && <RoiHandles roi={roi} scale={view.scale} imageWidth={imageWidth} imageHeight={imageHeight} />}
            </Group>
          </Layer>
        </Stage>
      )}
      {loading && <div className="viewer-loading">运行中…</div>}
    </div>
  );
}
