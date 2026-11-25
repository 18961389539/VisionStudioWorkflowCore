import { useEffect, useMemo, useState } from 'react';
import { createPixelBuffer, readPixel } from '../imagePixels';

/**
 * 延迟按需像素取样：仅当用户实际将光标移入图像区域时才创建离屏 Canvas 并读取像素缓冲区，
 * 避免切换大图（如 5 MP 相机帧）时在主线程无条件执行全图 drawImage + getImageData。
 */
export function useImagePixelSampler(
  image: HTMLImageElement | undefined,
  cursor: { x: number; y: number } | undefined,
  displayWidth: number,
  displayHeight: number
) {
  const [activatedImage, setActivatedImage] = useState<HTMLImageElement>();

  useEffect(() => {
    if (cursor && image && activatedImage !== image) {
      setActivatedImage(image);
    }
  }, [cursor, image, activatedImage]);

  const pixelBuffer = useMemo(() => {
    if (!image || activatedImage !== image) return undefined;
    try {
      const canvas = document.createElement('canvas');
      canvas.width = image.naturalWidth;
      canvas.height = image.naturalHeight;
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) return null;
      context.drawImage(image, 0, 0);
      return createPixelBuffer(context.getImageData(0, 0, canvas.width, canvas.height).data, canvas.width, canvas.height);
    } catch {
      return null;
    }
  }, [image, activatedImage]);

  const pixel = cursor && pixelBuffer
    ? readPixel(pixelBuffer, cursor.x, cursor.y, displayWidth, displayHeight)
    : undefined;

  return {
    pixel,
    pixelUnavailable: pixelBuffer === null
  };
}
