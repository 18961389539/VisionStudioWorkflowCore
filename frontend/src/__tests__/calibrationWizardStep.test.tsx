import '@testing-library/jest-dom/vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import CalibrationIntrinsicsWizard from '../components/CalibrationIntrinsicsWizard';

function stubBrowserApis() {
  vi.stubGlobal('matchMedia', vi.fn((query: string) => ({
    matches: false, media: query, onchange: null,
    addListener: vi.fn(), removeListener: vi.fn(),
    addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn()
  })));
  vi.stubGlobal('ResizeObserver', class {
    observe() { /* jsdom 无布局 */ }
    unobserve() { /* no-op */ }
    disconnect() { /* no-op */ }
  });
}

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function stubApi(overrides: Record<string, Response> = {}) {
  return vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (overrides[url]) return overrides[url];
    if (url === '/api/cameras') return json([]);
    if (url === '/api/camera-intrinsics') return json([]);
    return json({});
  });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('CalibrationIntrinsicsWizard step error smoke', () => {
  it('shows a 4xx rejection on step 1 without advancing the wizard', async () => {
    stubBrowserApis();
    vi.stubGlobal('fetch', stubApi({
      '/api/calibration/wizard/sessions': json({ error: 'board columns must be >= 3' }, 422)
    }));

    render(<CalibrationIntrinsicsWizard
      canApplyToSelectedNode={false}
      onApplyToSelectedNode={() => undefined}
    />);

    // 步骤 0（标定板设置）应该已渲染：创建会话按钮可见。
    const button = await screen.findByRole('button', { name: /创\s*建\s*向\s*导\s*会\s*话/ });
    expect(button).not.toBeDisabled();
    fireEvent.click(button);

    // 4xx 错误消息可见，向导仍在步骤 0（创建会话按钮仍可见 = 未推进到步骤 1）。
    expect(await screen.findByText(/board columns must be >= 3/)).toBeTruthy();
    expect(screen.getByRole('button', { name: /创\s*建\s*向\s*导\s*会\s*话/ })).not.toBeNull();
  });
});
