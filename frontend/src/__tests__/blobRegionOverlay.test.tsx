import { render } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { OverlayShape } from '../components/imageViewerUtils';

const canvas = vi.hoisted(() => ({
  beginPath: vi.fn(),
  rect: vi.fn(),
  fillShape: vi.fn()
}));

vi.mock('react-konva', () => ({
  Shape: ({ sceneFunc }: { sceneFunc: (context: typeof canvas, shape: object) => void }) => {
    sceneFunc(canvas, {});
    return null;
  }
}));

describe('blob region overlay', () => {
  it('draws only the inclusive pixel runs, leaving gaps unfilled', () => {
    render(<OverlayShape scale={1} overlay={{
      id: 'blob-0', type: 'Region',
      runs: [
        { row: 4, startColumn: 2, endColumn: 5 },
        { row: 5, startColumn: 2, endColumn: 2 },
        { row: 5, startColumn: 5, endColumn: 5 }
      ]
    }} />);

    expect(canvas.rect.mock.calls).toEqual([[2, 4, 4, 1], [2, 5, 1, 1], [5, 5, 1, 1]]);
    expect(canvas.fillShape).toHaveBeenCalledOnce();
  });
});
