import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import CanvasMiniMap from '../components/CanvasMiniMap';

vi.mock('@xyflow/react', () => ({
  MiniMap: () => <div role="img" aria-label="流程小地图" />,
  Panel: ({ children }: { children: ReactNode }) => <div>{children}</div>
}));
afterEach(() => { cleanup(); localStorage.clear(); });
describe('canvas minimap visibility', () => {
  it('hides on small graphs, can be opened, and remembers the explicit choice', () => {
    const view = render(<CanvasMiniMap nodeCount={3} />);
    expect(screen.queryByRole('img')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '展开小地图' }));
    expect(screen.getByRole('img', { name: '流程小地图' })).toBeTruthy();
    view.unmount();
    render(<CanvasMiniMap nodeCount={3} />);
    expect(screen.getByRole('img')).toBeTruthy();
  });
  it('automatically shows larger graphs and respects manual collapse', () => {
    const view = render(<CanvasMiniMap nodeCount={3} />);
    view.rerender(<CanvasMiniMap nodeCount={8} />);
    expect(screen.getByRole('img')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '收起小地图' }));
    view.rerender(<CanvasMiniMap nodeCount={12} />);
    expect(screen.queryByRole('img')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '小地图恢复自动显隐' }));
    expect(screen.getByRole('img')).toBeTruthy();
    view.rerender(<CanvasMiniMap nodeCount={3} />);
    expect(screen.queryByRole('img')).toBeNull();
  });
});
