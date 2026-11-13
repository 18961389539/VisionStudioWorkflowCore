/**
 * 绘制模式下的操作提示条。
 *
 * 只负责把「下一步该做什么」和「当前尺寸」说清楚：绘制中途没有光标反馈时，
 * 用户只能靠这段文字判断还要点几下、当前半径/长度是多少。
 */
import { Button } from 'antd';
import { circleArcSpan } from '../caliperCircleSampling';
import type { MeasurementGeometry } from './imageViewerUtils';
import type { CircleDrawMode, Point } from './imageViewerTypes';

export function ImageViewerDrawingHint({
  kind,
  threePoint,
  circleDrawMode,
  constructionPoints,
  constructionIssue,
  measurementDraft,
  onUndoConstructionPoint
}: {
  kind: 'line' | 'circle';
  /** 三点定圆 / 三点选弧：提示条要显示已选点数与撤回按钮。 */
  threePoint: boolean;
  circleDrawMode: CircleDrawMode;
  constructionPoints: Point[];
  constructionIssue?: string;
  measurementDraft?: MeasurementGeometry;
  onUndoConstructionPoint: () => void;
}) {
  if (kind === 'line') {
    return <div className="viewer-drawing-hint" aria-label="搜索线绘制提示">
      <div className="viewer-drawing-instructions"><span role="status">从起点拖到终点，松开完成；搜索线至少 2 px，Esc 取消。</span></div>
      <div className="viewer-drawing-feedback">{measurementDraft?.type === 'line' ? <output aria-label="搜索线绘制尺寸" aria-live="off">
        长度 {Math.hypot(measurementDraft.end.x - measurementDraft.start.x, measurementDraft.end.y - measurementDraft.start.y).toFixed(2)} px
        {' · '}方向 {(Math.atan2(measurementDraft.end.y - measurementDraft.start.y, measurementDraft.end.x - measurementDraft.start.x) * 180 / Math.PI).toFixed(1)}°
      </output> : <span>极性按起点 → 终点解释；投影长度和选边顺序在重绘后保留。</span>}</div>
    </div>;
  }
  return <div className="viewer-drawing-hint" aria-label="标称圆圈选提示">
    <div className="viewer-drawing-instructions"><span role="status">{threePoint
      ? circleDrawMode === 'threePointArc'
        ? `点选圆弧第 ${constructionPoints.length + 1}/3 点：端点 → 经过点 → 另一端；Esc 取消。`
        : `沿目标圆边缘点选第 ${constructionPoints.length + 1}/3 点；尽量分散选点，Esc 取消。`
      : circleDrawMode === 'diameter'
        ? '从圆边缘拖到直径另一端，松开完成；Esc 取消。'
        : '从圆心拖到目标边缘，松开完成；Esc 取消。'}</span>
    {threePoint && <Button size="small" disabled={constructionPoints.length === 0} onClick={onUndoConstructionPoint}>撤回上一点</Button>}</div>
    <div className="viewer-drawing-feedback">{constructionIssue ? <span role="alert">{constructionIssue}</span>
    : measurementDraft?.type === 'circle' ? <output aria-label="圈选尺寸" aria-live="off">
      圆心 ({measurementDraft.center.x.toFixed(2)}, {measurementDraft.center.y.toFixed(2)}) · 半径 {measurementDraft.radius.toFixed(2)} px
      {circleDrawMode === 'threePointArc' && ` · 圆弧 ${circleArcSpan(measurementDraft.angleStartDeg ?? -180, measurementDraft.angleEndDeg ?? 180).span.toFixed(1)}°`}
    </output> : <span>{circleDrawMode === 'threePointArc' ? '自动选择经过第 2 点的圆弧；完成后可旋转或反选圆弧。' : '完成圈选后可继续拖动圆心、半径和搜索带。'}</span>}</div>
  </div>;
}
