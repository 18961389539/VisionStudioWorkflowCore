import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ConfigProvider } from 'antd';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import StationSetupWizard from '../components/StationSetupWizard';

type WizardProps = React.ComponentProps<typeof StationSetupWizard>;

function mount(props?: Partial<WizardProps>) {
  return render(<ConfigProvider theme={{ token: { motion: false } }}>
    <StationSetupWizard startIndex={0} existingIds={[]} stationCount={0} onClose={vi.fn()} {...props} />
  </ConfigProvider>);
}

const fill = (label: string, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } });

function fillValid() {
  fill('工位 ID', 'inspection-a');
  fill('工位名称', '正面检测');
  fill('工位地址', 'https://inspection-a.example/');
  fill('读取令牌', 'vst_read_token');
}

// antd Modal 渲染到 body，不在 render 的 container 里
const pre = (label: string) =>
  document.querySelector(`pre[aria-label="${label}"]`)?.textContent ?? '';

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', () => ({
    matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {},
  }));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('工位配置向导', () => {
  it('未填写时不给可复制的文本，并说明还差几处', () => {
    mount();
    expect(screen.getByRole('button', { name: '复制 JSON' })).toBeDisabled();
    expect(screen.getByRole('button', { name: '复制变量' })).toBeDisabled();
    expect(screen.getByText(/还有 \d+ 处需要修正/)).toBeInTheDocument();
    expect(pre('站点配置片段')).toBe('填写并修正后自动生成');
  });

  it('填完整后生成配置骨架与下标环境变量，且 JSON 里没有令牌', () => {
    mount();
    fillValid();
    const json = pre('站点配置片段');
    expect(json).toContain('"Id": "inspection-a"');
    expect(json).toContain('"ProductionFleet"');
    expect(json).not.toContain('vst_');
    expect(pre('令牌环境变量')).toBe('ProductionFleet__Stations__0__Token=vst_read_token');
    expect(screen.getByRole('button', { name: '复制 JSON' })).toBeEnabled();
  });

  it('已有工位时只生成可粘进数组的单个对象，并按下标生成变量', () => {
    mount({ startIndex: 2, stationCount: 2 });
    fillValid();
    const json = pre('站点配置片段');
    expect(json).toContain('"Id": "inspection-a"');
    expect(json).not.toContain('ProductionFleet');
    expect(pre('令牌环境变量')).toContain('ProductionFleet__Stations__2__Token=');
    expect(screen.getByText(/下标 2/)).toBeInTheDocument();
  });

  it('字段错误就地提示：ID 重复、跨主机 http、令牌缺前缀', () => {
    mount({ existingIds: ['inspection-a'], stationCount: 1 });
    fill('工位 ID', 'inspection-a');
    fill('工位名称', 'A');
    fill('工位地址', 'http://inspection-a.example/');
    fill('读取令牌', 'read-token');
    // 同一条错误会出现在字段下方与汇总提示里
    expect(screen.getAllByText('该工位 ID 已存在（大小写不敏感）').length).toBeGreaterThan(0);
    expect(screen.getAllByText(/跨主机必须用 https/).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/读取令牌必须以 vst_ 开头/).length).toBeGreaterThan(0);
  });

  it('期望版本必须与配方 ID 成对填写', () => {
    mount();
    fillValid();
    fill('期望版本', '4');
    expect(screen.getAllByText('填写期望版本或流程指纹前，必须先填配方 ID').length).toBeGreaterThan(0);
    fill('期望配方 ID', 'front-cover');
    expect(screen.queryByText('填写期望版本或流程指纹前，必须先填配方 ID')).toBeNull();
  });
});
