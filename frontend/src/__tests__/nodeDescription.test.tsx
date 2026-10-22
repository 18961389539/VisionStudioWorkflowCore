import { describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach } from 'vitest';
import NodeDescription from '../components/NodeDescription';

afterEach(cleanup);

describe('NodeDescription', () => {
  it('shows the purpose first and expands the remaining documentation on demand', () => {
    render(<NodeDescription text="按灰度阈值分割图像。随后可接区域清理和测量节点。" />);
    expect(screen.getByText('按灰度阈值分割图像。')).toBeTruthy();
    expect(screen.queryByText(/随后可接/)).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '展开完整说明' }));
    expect(screen.getByText('按灰度阈值分割图像。随后可接区域清理和测量节点。')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '收起说明' }));
    expect(screen.queryByText(/随后可接/)).toBeNull();
  });

  it('does not add a toggle for a single-sentence description', () => {
    render(<NodeDescription text="只有一个用途说明。" />);
    expect(screen.queryByRole('button')).toBeNull();
  });
});
