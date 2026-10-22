export type PixelBuffer = { data: Uint8ClampedArray; width: number; height: number; grayscale: boolean };

/** 只检查底图，覆盖层和显示缩放不会进入取样缓冲区。彩色图中的中性像素仍显示 RGB。 */
export function createPixelBuffer(data: Uint8ClampedArray, width: number, height: number): PixelBuffer {
  let grayscale = true;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i] !== data[i + 1] || data[i] !== data[i + 2]) { grayscale = false; break; }
  }
  return { data, width, height, grayscale };
}

export function readPixel(buffer: PixelBuffer, x: number, y: number, displayWidth = buffer.width, displayHeight = buffer.height) {
  if (!Number.isFinite(x) || !Number.isFinite(y) || displayWidth <= 0 || displayHeight <= 0
    || x < 0 || y < 0 || x >= displayWidth || y >= displayHeight) return undefined;
  const pixelX = Math.floor(x * buffer.width / displayWidth);
  const pixelY = Math.floor(y * buffer.height / displayHeight);
  const offset = (pixelY * buffer.width + pixelX) * 4;
  const [r, g, b, a] = buffer.data.subarray(offset, offset + 4);
  return { x: Math.floor(x), y: Math.floor(y), r, g, b, a, grayscale: buffer.grayscale };
}
