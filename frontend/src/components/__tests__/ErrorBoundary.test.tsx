import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import ErrorBoundary from '../ErrorBoundary';

function Bomb(): null {
  throw new Error('boom');
}

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

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
      // 诊断必须可导出（不再只写 console）：崩溃上下文要能交给后端工程师。
      expect(screen.getByText('导出诊断')).toBeTruthy();
    } finally {
      spy.mockRestore();
    }
  });

  it('panel mode isolates one panel and can be dismissed without a reload', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const onDismiss = vi.fn();
    try {
      render(
        <div>
          <span>canvas still alive</span>
          <ErrorBoundary variant="panel" scope="相机" onDismiss={onDismiss}>
            <Bomb />
          </ErrorBoundary>
        </div>
      );
      expect(screen.getByText('相机无法显示')).toBeTruthy();
      expect(screen.getByText('canvas still alive')).toBeTruthy();
      fireEvent.click(screen.getByText('关闭面板'));
      expect(onDismiss).toHaveBeenCalledTimes(1);
    } finally {
      spy.mockRestore();
    }
  });

  it('panel mode can retry the failed subtree without remounting the app', () => {
    const spy = vi.spyOn(console, 'error').mockImplementation(() => undefined);
    let shouldThrow = true;
    function Flaky(): ReactNode {
      if (shouldThrow) throw new Error('transient');
      return <span>panel recovered</span>;
    }
    try {
      render(
        <ErrorBoundary variant="panel" scope="设备">
          <Flaky />
        </ErrorBoundary>
      );
      expect(screen.getByText('设备无法显示')).toBeTruthy();
      shouldThrow = false;
      fireEvent.click(screen.getByRole('button', { name: /重\s*试/ }));
      expect(screen.getByText('panel recovered')).toBeTruthy();
    } finally {
      spy.mockRestore();
    }
  });
});
