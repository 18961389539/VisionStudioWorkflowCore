import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import DataErrorAlert from '../components/DataErrorAlert';

/**
 * 失败态与空态的区分（2026-10-25）：加载失败必须给出原因与重试入口，
 * 不能退化成"没有数据"。这个组件是面板里唯一的失败态表达。
 */

afterEach(() => cleanup());

describe('DataErrorAlert', () => {
  it('没有错误时不渲染任何内容（不影响空态）', () => {
    const { container } = render(<DataErrorAlert scope="标定资产" error={undefined} />);
    expect(container.childElementCount).toBe(0);
  });

  it('展示作用域与具体原因', () => {
    render(<DataErrorAlert scope="标定资产" error="读取标定资产失败（HTTP 503）" />);
    expect(screen.getByText('标定资产加载失败')).toBeTruthy();
    expect(screen.getByText('读取标定资产失败（HTTP 503）')).toBeTruthy();
  });

  it('提供重试入口并回传点击', () => {
    const onRetry = vi.fn();
    render(<DataErrorAlert scope="训练包列表" error="网络请求未完成" onRetry={onRetry} />);
    fireEvent.click(screen.getByRole('button', { name: /重\s*试/ }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('未提供 onRetry 时不显示重试按钮', () => {
    render(<DataErrorAlert scope="验证数据" error="HTTP 500" />);
    expect(screen.queryByRole('button', { name: /重\s*试/ })).toBeNull();
  });
});
