/**
 * ImageViewer 的类型定义。
 */
import type { CaliperSpokeDiagnostic } from '../caliperCircleDiagnostics';
import type { VisionOverlay, VisionRoi } from '../types';

export type ToolMode = 'pan' | 'rect' | 'circle' | 'polygon' | 'brush' | 'eraser';
export type CircleDrawMode = 'center' | 'diameter' | 'threePoints' | 'threePointArc';
export type ViewState = { scale: number; x: number; y: number };
export type Point = { x: number; y: number };
export type RoiMoveBounds = { minX: number; maxX: number; minY: number; maxY: number };
export type RoiDragMode = 'move' | 'nw' | 'ne' | 'sw' | 'se' | 'e' | 'w' | 'n' | 's' | `vertex:${number}`;
export type MeasurementDragMode = 'start' | 'end' | 'profileStart' | 'profileEnd' | 'profileWidth' | 'moveLine' | 'center' | 'moveCircle' | 'radius' | 'angleStart' | 'angleEnd' | 'innerBand' | 'outerBand';
export type { CaliperSpokeDiagnostic };
export type MeasurementGeometry =
  | { type: 'line'; start: Point; end: Point; profileSpan?: number; profileCount?: number; profileWidth?: number; direction?: string }
  | { type: 'circle'; center: Point; radius: number; band?: number; angleStartDeg?: number; angleEndDeg?: number;
      caliperCount?: number; samplingMode?: string; angleStepDeg?: number; profileWidth?: number;
      searchDirection?: string; centerEditable?: boolean; radiusEditable?: boolean };
export type CaliperDiagnosticSelection = {
  center: Point;
  radius: number;
  spokes: CaliperSpokeDiagnostic[];
  index: number;
  onSelect?: (index: number) => void;
};

export type ImageViewerProps = {
  previewUrl?: string;
  comparisonUrl?: string;
  comparisonWidth?: number;
  comparisonHeight?: number;
  comparisonLabel?: string;
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
};
