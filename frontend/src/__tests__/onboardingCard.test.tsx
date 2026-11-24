import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import OnboardingCard from '../components/OnboardingCard';

const mount = (overrides: Partial<React.ComponentProps<typeof OnboardingCard>> = {}) => render(
  <OnboardingCard
    onClose={vi.fn()}
    onOpenTemplates={vi.fn()}
    onOpenCameras={vi.fn()}
    onOpenCommand={vi.fn()}
    onOpenValidation={vi.fn()}
    onOpenJobs={vi.fn()}
    {...overrides} />,
);

afterEach(cleanup);

describe('上手引导卡片', () => {
  it('给出从选模板到发布产线的五步主线', () => {
    mount();
    const card = screen.getByRole('complementary', { name: '上手引导' });
    expect(card).toBeInTheDocument();
    ['选一个起点', '接上设备', '调节点参数', '验证流程', '发布到产线'].forEach((title) => {
      expect(screen.getByText(title)).toBeInTheDocument();
    });
  });

  it('每一步都能直接打开对应面板，而不是只写一句说明', () => {
    const actions = { templates: vi.fn(), cameras: vi.fn(), command: vi.fn(), validation: vi.fn(), jobs: vi.fn() };
    mount({ onOpenTemplates: actions.templates, onOpenCameras: actions.cameras, onOpenCommand: actions.command,
      onOpenValidation: actions.validation, onOpenJobs: actions.jobs });
    fireEvent.click(screen.getByRole('button', { name: '模板库' }));
    fireEvent.click(screen.getByRole('button', { name: '设备' }));
    fireEvent.click(screen.getByRole('button', { name: '命令面板' }));
    fireEvent.click(screen.getByRole('button', { name: '数据集验证' }));
    fireEvent.click(screen.getByRole('button', { name: '配方管理' }));
    expect(actions.templates).toHaveBeenCalledOnce();
    expect(actions.cameras).toHaveBeenCalledOnce();
    expect(actions.command).toHaveBeenCalledOnce();
    expect(actions.validation).toHaveBeenCalledOnce();
    expect(actions.jobs).toHaveBeenCalledOnce();
  });

  it('关闭按钮告诉用户以后去哪儿重新打开', () => {
    const onClose = vi.fn();
    mount({ onClose });
    expect(screen.getByText(/可在「工具」菜单重新打开/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: '关闭上手引导' }));
    expect(onClose).toHaveBeenCalledOnce();
  });
});
