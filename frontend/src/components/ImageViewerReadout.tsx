/**
 * ImageViewer 底部状态栏：图像尺寸、缩放比、光标像素读数、选区作用目标。
 *
 * 纯展示组件——像素取样与缩放计算都留在容器里，这里只负责把已有读数排版出来。
 */
import type { Point } from './imageViewerTypes';
import type { readPixel } from '../imagePixels';

type PixelValue = NonNullable<ReturnType<typeof readPixel>>;

export function ImageViewerReadout({
  imageWidth,
  imageHeight,
  scale,
  cursor,
  image,
  pixel,
  pixelUnavailable,
  roiEnabled,
  roiTargetLabel
}: {
  imageWidth: number;
  imageHeight: number;
  scale: number;
  /** 图像坐标下的光标位置；移出图像范围时为 undefined。 */
  cursor?: Point;
  image?: HTMLImageElement;
  pixel?: PixelValue;
  /** 底图存在但取样缓冲区创建失败（跨域画布等），与「尚未取到像素」区分显示。 */
  pixelUnavailable: boolean;
  roiEnabled: boolean;
  roiTargetLabel?: string;
}) {
  return (
    <div className="viewer-readout" role="group" aria-label="图像状态栏">
      <span className="viewer-image-size">{imageWidth > 0 ? `${imageWidth} × ${imageHeight}` : '无图像'}</span>
      <span className="viewer-zoom">{Math.round(scale * 100)}%</span>
      <span className="viewer-pixel-readout">{cursor && image ? <>
        <span>X {Math.floor(cursor.x)} · Y {Math.floor(cursor.y)}</span>
        <span>{pixel ? `${pixel.grayscale ? `灰度 ${pixel.r}` : `RGB (${pixel.r}, ${pixel.g}, ${pixel.b})`}${pixel.a < 255 ? ` · A ${pixel.a}` : ''}` : pixelUnavailable ? '像素值不可用' : ''}</span>
      </> : <span className="viewer-pixel-placeholder">移动鼠标查看像素</span>}</span>
      <span className="viewer-roi-target" title={roiEnabled ? `选区编辑作用于：${roiTargetLabel || '当前图像'}` : '只读：该节点/该图像不支持编辑选区（未开启 ROI 能力、正在查看预览图像或图像尚未产出）'}>{roiEnabled ? `选区 → ${roiTargetLabel || '当前图像'}` : '只读 · 不可编辑选区'}</span>
    </div>
  );
}
