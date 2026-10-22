/**
 * ImageViewer 的类型定义。
 */
export type ToolMode = 'pan' | 'rect' | 'circle' | 'polygon' | 'brush' | 'eraser';
export type CircleDrawMode = 'center' | 'diameter' | 'threePoints' | 'threePointArc';
export type ViewState = { scale: number; x: number; y: number };
export type Point = { x: number; y: number };
export type RoiMoveBounds = { minX: number; maxX: number; minY: number; maxY: number };
export type RoiDragMode = 'move' | 'nw' | 'ne' | 'sw' | 'se' | 'e' | 'w' | 'n' | 's' | `vertex:${number}`;
export type MeasurementDragMode = 'start' | 'end' | 'profileStart' | 'profileEnd' | 'profileWidth' | 'moveLine' | 'center' | 'moveCircle' | 'radius' | 'angleStart' | 'angleEnd' | 'innerBand' | 'outerBand';
import type { CaliperSpokeDiagnostic } from '../caliperCircleDiagnostics';
export type { CaliperSpokeDiagnostic };
export type CaliperDiagnosticSelection = {
  center: Point;
  radius: number;
  spokes: CaliperSpokeDiagnostic[];
  index: number;
  onSelect?: (index: number) => void;
};
