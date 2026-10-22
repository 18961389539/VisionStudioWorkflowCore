import { describe, expect, it } from 'vitest';
import { createPixelBuffer, readPixel } from '../imagePixels';

describe('image pixel sampling', () => {
  it('samples exact source pixels at fractional image positions and the last valid pixel', () => {
    const buffer = createPixelBuffer(new Uint8ClampedArray([0, 0, 0, 255, 255, 255, 255, 255]), 2, 1);
    expect(readPixel(buffer, 0.99, 0.4)).toMatchObject({ x: 0, y: 0, r: 0, grayscale: true });
    expect(readPixel(buffer, 1.99, 0.99)).toMatchObject({ x: 1, y: 0, r: 255, a: 255 });
  });

  it('keeps RGB readouts for neutral pixels in a color image and preserves alpha', () => {
    const buffer = createPixelBuffer(new Uint8ClampedArray([64, 64, 64, 255, 128, 64, 32, 128]), 2, 1);
    expect(readPixel(buffer, 0, 0)).toMatchObject({ r: 64, g: 64, b: 64, grayscale: false });
    expect(readPixel(buffer, 1, 0)).toMatchObject({ r: 128, g: 64, b: 32, a: 128, grayscale: false });
  });

  it('rejects image borders, outside positions and non-finite coordinates', () => {
    const buffer = createPixelBuffer(new Uint8ClampedArray([32, 32, 32, 255]), 1, 1);
    for (const [x, y] of [[-0.01, 0], [0, -1], [1, 0], [0, 1], [NaN, 0], [0, Infinity]]) {
      expect(readPixel(buffer, x, y)).toBeUndefined();
    }
    expect(readPixel(buffer, 0, 0, 0, 1)).toBeUndefined();
  });

  it('maps logical image dimensions to the decoded source resolution', () => {
    const buffer = createPixelBuffer(new Uint8ClampedArray([0, 0, 0, 255, 200, 200, 200, 255]), 2, 1);
    expect(readPixel(buffer, 199, 99, 200, 100)).toMatchObject({ x: 199, y: 99, r: 200 });
  });
});
