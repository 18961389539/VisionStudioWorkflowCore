import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import ErrorBoundary from '../ErrorBoundary';

function Bomb(): null {
  throw new Error('boom');
}

describe('ErrorBoundary', () => {
  it('renders a recovery surface instead of a blank page when a component throws', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    try {
      render(
        <ErrorBoundary>
          <Bomb />
        </ErrorBoundary>
      );
      expect(screen.getByText('界面遇到未处理的错误')).toBeTruthy();
      expect(screen.getByText('重新加载')).toBeTruthy();
      expect(screen.getByText('尝试恢复')).toBeTruthy();
    } finally {
      spy.mockRestore();
    }
  });
});
