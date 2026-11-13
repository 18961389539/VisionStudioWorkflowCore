/**
 * ImageViewer 工具栏。
 *
 * 纯展示 + 事件转发：工具切换时那一长串状态清理留在容器里（它要碰所有手势 ref），
 * 这里只负责把当前可用性渲染成按钮的 disabled 与 tooltip。
 */
import { Button, InputNumber, Segmented, Select, Space, Switch, Tooltip } from 'antd';
import { clamp, MAX_POLYGON_HANDLES } from './imageViewerUtils';
import type { MeasurementGeometry } from './imageViewerUtils';
import type { CircleDrawMode, ToolMode } from './imageViewerTypes';
import type { VisionRoi } from '../types';

export function ImageViewerToolbar({
  showRoiTools,
  roiEnabled,
  roiPointLimit,
  roiClearable,
  roi,
  tool,
  onToolChange,
  roiUndoAvailable,
  onUndoRoiEdit,
  brushWidth,
  onBrushWidthChange,
  canDrawMeasurementGeometry,
  geometryDrawMode,
  measurementGeometry,
  onToggleGeometryDrawMode,
  circleDrawMode,
  onCircleDrawModeChange,
  measurementUndoAvailable,
  onUndoMeasurementEdit,
  focusSearchLineDisabled,
  onFocusSearchLine,
  onFit,
  onOneToOne,
  onClearRoi,
  pixelated,
  onPixelatedChange,
  showOverlay,
  onShowOverlayChange,
  overlayNodeIds,
  overlayFilter,
  onOverlayFilterChange,
  nodeLabels
}: {
  showRoiTools: boolean;
  roiEnabled: boolean;
  roiPointLimit?: number;
  roiClearable: boolean;
  roi?: VisionRoi;
  tool: ToolMode;
  onToolChange: (tool: ToolMode) => void;
  roiUndoAvailable: boolean;
  onUndoRoiEdit: () => void;
  brushWidth: number;
  onBrushWidthChange: (width: number) => void;
  canDrawMeasurementGeometry: boolean;
  geometryDrawMode: boolean;
  measurementGeometry?: MeasurementGeometry;
  onToggleGeometryDrawMode: () => void;
  circleDrawMode: CircleDrawMode;
  onCircleDrawModeChange: (mode: CircleDrawMode) => void;
  measurementUndoAvailable: boolean;
  onUndoMeasurementEdit: () => void;
  /** 搜索线完全落在图像外时无法定位，按钮置灰。 */
  focusSearchLineDisabled: boolean;
  onFocusSearchLine: () => void;
  onFit: () => void;
  onOneToOne: () => void;
  onClearRoi: () => void;
  pixelated: boolean;
  onPixelatedChange: (value: boolean) => void;
  showOverlay: boolean;
  onShowOverlayChange: (value: boolean) => void;
  overlayNodeIds: string[];
  overlayFilter: string[];
  onOverlayFilterChange: (value: string[]) => void;
  nodeLabels?: Record<string, string>;
}) {
  return (
    <div className="viewer-toolbar">
      <Space size={5} wrap>
        {showRoiTools && <Segmented
          size="small"
          value={tool}
          onChange={(value) => onToolChange(value as ToolMode)}
          options={[
            { value: 'pan', label: <Tooltip title="平移画布；拖动已有选区可移动，矩形/圆形可缩放，多边形可编辑顶点">平移</Tooltip> },
            ...(roiPointLimit ? [
              { value: 'polygon', disabled: !roiEnabled, label: <Tooltip title={`依次点击 ${roiPointLimit} 个角点；最后一个点自动闭合`}>多边形</Tooltip> }
            ] : [
              { value: 'rect', disabled: !roiEnabled, label: <Tooltip title="拖动绘制矩形，并替换当前选区">矩形</Tooltip> },
              { value: 'circle', disabled: !roiEnabled, label: <Tooltip title="拖动绘制圆形，并替换当前选区">圆形</Tooltip> },
              { value: 'polygon', disabled: !roiEnabled, label: <Tooltip title="逐点绘制多边形，并替换当前选区；双击完成">多边形</Tooltip> },
              { value: 'brush', disabled: !roiEnabled, label: <Tooltip title="笔刷：按住鼠标拖动绘制选区">笔刷</Tooltip> },
              { value: 'eraser', disabled: !roiEnabled || roi?.type !== 'Brush', label: <Tooltip title="橡皮：按住鼠标拖动擦除">橡皮</Tooltip> }
            ])
          ]}
        />}
        {showRoiTools && roi?.type === 'Polygon' && roi.points.length > MAX_POLYGON_HANDLES && <Tooltip
          title="复杂多边形轮廓最多抽样 8192 个点预览，控制点最多抽样 32 个；在预览选区内部拖动可整体移动，执行仍使用完整 ROI。全部顶点可在属性面板分页编辑。">
          <span className="viewer-roi-handle-hint">复杂多边形 · 预览与控制点抽样</span>
        </Tooltip>}
        {showRoiTools && <span className="viewer-toolbar-divider" />}
        {showRoiTools && (tool === 'brush' || tool === 'eraser') && <span className="viewer-brush-size">
          <span title="笔刷直径（图像像素）">笔宽</span>
          <InputNumber aria-label="笔刷直径" size="small" min={1} max={512} step={1} value={brushWidth}
            disabled={!roiEnabled} onChange={(value) => onBrushWidthChange(clamp(Number(value ?? 1), 1, 512))} />
          <span>px</span>
        </span>}
        {showRoiTools && roiUndoAvailable && <Button size="small" onClick={onUndoRoiEdit}
          title="撤销最近一次在画布上完成的选区编辑">{roi?.type === 'Brush' ? '撤销笔迹' : '撤销选区编辑'}</Button>}
        {canDrawMeasurementGeometry && <>
          <span className="viewer-toolbar-divider" />
          <Button size="small" type={geometryDrawMode ? 'primary' : 'default'}
            onClick={onToggleGeometryDrawMode}
            title={geometryDrawMode ? '退出绘制模式' : measurementGeometry?.type === 'circle'
              ? '可选择圆心+半径、直径两端、三点定圆或三点选弧；三点选弧会同时确定圆和测量角度范围。橙色调整圆心/半径，蓝色/紫色调整圆弧起止角，青色/绿色调整搜索带，粉色调整投影宽度'
              : measurementGeometry?.profileCount !== undefined
                ? '蓝色起点、紫色终点固定表示参数端点；箭头表示选边顺序（最强为双向）。青色控制点调整采样跨度，粉色控制点调整投影宽度。拖动端点或线段可微调搜索线'
                : '蓝色起点、紫色终点固定表示参数端点；箭头表示选边顺序（最强为双向）。粉色控制点调整投影宽度。拖动端点或线段可微调搜索线'}>
            {geometryDrawMode ? '取消绘制' : measurementGeometry?.type === 'line' ? '重绘搜索线' : '重绘标称圆'}
          </Button>
        </>}
        {geometryDrawMode && measurementGeometry?.type === 'circle' && <Segmented size="small" aria-label="标称圆圈选方式"
          value={circleDrawMode} options={[{ label: '圆心+半径', value: 'center' },
            { label: '直径两端', value: 'diameter' }, { label: '三点定圆', value: 'threePoints' },
            { label: '三点选弧', value: 'threePointArc' }]}
          onChange={value => onCircleDrawModeChange(value as CircleDrawMode)} />}
        {measurementUndoAvailable && <Button size="small" onClick={onUndoMeasurementEdit}
          title="撤销最近一次在画布上完成的卡尺几何编辑">撤销测量编辑</Button>}
        {measurementGeometry?.profileWidth !== undefined && <span className="viewer-profile-width-hint"
          title="浅蓝色窄带表示每条卡尺沿垂直于搜索方向的实际灰度投影宽度">浅蓝色采样带 · {measurementGeometry.profileWidth} px</span>}
        {measurementGeometry?.type === 'line' && <Button size="small" onClick={onFocusSearchLine}
          disabled={focusSearchLineDisabled}
          title="放大并居中显示图像内的搜索线与投影带，不改变测量参数">定位搜索线</Button>}
        <Button size="small" onClick={onFit}>适应窗口</Button>
        <Button size="small" onClick={onOneToOne}>原始比例</Button>
        {showRoiTools && roiClearable && <Button size="small" danger disabled={!roi || !roiEnabled} onClick={onClearRoi}>清除选区</Button>}
        <span className="viewer-toolbar-divider" />
        <span className="viewer-toggle" title="像素化（默认）：放大时按真实像素方块显示，便于逐像素核对；关闭则使用平滑插值（缩小总览更顺眼）">像素化 <Switch size="small" checked={pixelated} onChange={onPixelatedChange} /></span>
        <span className="viewer-toggle">显示覆盖层 <Switch size="small" checked={showOverlay} onChange={onShowOverlayChange} /></span>
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
            onChange={onOverlayFilterChange}
            options={overlayNodeIds.map((id) => ({ value: id, label: nodeLabels?.[id] ?? id }))}
          />
        )}
      </Space>
    </div>
  );
}
