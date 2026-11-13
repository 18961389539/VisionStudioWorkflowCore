/**
 * ImageViewer 的光标样式推导。
 *
 * 光标是「当前悬停位置的隐含可操作性」的唯一可见反馈：同一位置在平移模式与绘制模式下
 * 含义不同，在控制点上与在空白处也不同。这段逻辑原样留在组件里会与命中检测重复一遍
 * 控制点枚举，提成 hook 后与 `imageViewerHitTest` 共用同一套判定。
 */
import { useMemo } from 'react';
import { circleRadiusHandle, pointInsidePolygon, pointInsideRoi, polygonPreviewPoints, radialResizeCursor } from './imageViewerUtils';
import { hitMeasurementHandle, hitRoiHandle, screenToImage } from './imageViewerHitTest';
import type { CaliperDiagnosticSelection, Point, ToolMode, ViewState } from './imageViewerTypes';
import type { MeasurementGeometry } from './imageViewerUtils';
import type { VisionRoi } from '../types';

export function useViewerCursorStyle({
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
  imageWidth,
  imageHeight
}: {
  caliperDiagnosticSelection?: CaliperDiagnosticSelection;
  geometryDrawMode: boolean;
  /** 平移手势进行中（`panStart` 已置位）。 */
  isPanning: boolean;
  /** 选区/测量几何拖动进行中。 */
  isDragging: boolean;
  cursorScreen?: Point;
  measurementGeometry?: MeasurementGeometry;
  measurementGeometryEditable: boolean;
  roi?: VisionRoi;
  roiEnabled: boolean;
  tool: ToolMode;
  view: ViewState;
  imageWidth: number;
  imageHeight: number;
}): string {
  return useMemo(() => {
    if (caliperDiagnosticSelection) return isPanning ? 'grabbing' : 'crosshair';
    if (geometryDrawMode) return 'crosshair';
    if (isPanning || isDragging) return 'grabbing';
    if (cursorScreen) {
      if (measurementGeometryEditable && measurementGeometry) {
        const handle = hitMeasurementHandle(measurementGeometry, cursorScreen, view, imageWidth, imageHeight);
        if (handle === 'radius' && measurementGeometry.type === 'circle')
          return radialResizeCursor(measurementGeometry.center,
            circleRadiusHandle(measurementGeometry.center, measurementGeometry.radius, imageWidth, imageHeight));
        if (handle === 'profileWidth') return 'move';
        if (handle === 'innerBand' || handle === 'outerBand') return 'ns-resize';
        if ((handle === 'profileStart' || handle === 'profileEnd') && measurementGeometry.type === 'line') {
          const dx = measurementGeometry.end.x - measurementGeometry.start.x;
          const dy = measurementGeometry.end.y - measurementGeometry.start.y;
          if (Math.abs(dy) > Math.abs(dx) * 1.5) return 'ew-resize';
          if (Math.abs(dx) > Math.abs(dy) * 1.5) return 'ns-resize';
          return (-dy) * dx >= 0 ? 'nwse-resize' : 'nesw-resize';
        }
        if (handle) return 'move';
      }
      if (roi && roiEnabled && tool === 'pan') {
        const handle = hitRoiHandle(roi, cursorScreen, view, imageWidth, imageHeight);
        if (handle && roi.type === 'Circle')
          return radialResizeCursor({ x: roi.x, y: roi.y }, circleRadiusHandle({ x: roi.x, y: roi.y }, roi.radius, imageWidth, imageHeight));
        if (handle === 'nw' || handle === 'se') return 'nwse-resize';
        if (handle === 'ne' || handle === 'sw') return 'nesw-resize';
        if (handle === 'w' || handle === 'e') return 'ew-resize';
        if (handle === 'n' || handle === 's') return 'ns-resize';
        if (handle?.startsWith('vertex:')) return 'move';
        const imageCursor = screenToImage(cursorScreen, view);
        if ((roi.type === 'Rectangle' || roi.type === 'Circle') && pointInsideRoi(roi, imageCursor)) return 'move';
        // Match the visible, cached preview contour without ray-casting every source vertex
        // on every pointer move; pointer-down still checks the full ROI for exact movement.
        if (roi.type === 'Polygon' && pointInsidePolygon(polygonPreviewPoints(roi.points), imageCursor)) return 'move';
      }
    }
    return tool === 'pan' ? 'grab' : 'crosshair';
  }, [caliperDiagnosticSelection, cursorScreen, geometryDrawMode, imageHeight, imageWidth, isDragging, isPanning,
    measurementGeometry, measurementGeometryEditable, roi, roiEnabled, tool, view]);
}
