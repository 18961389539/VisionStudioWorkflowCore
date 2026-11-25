import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import App from '../App';

const account = vi.hoisted(() => ({ enabled: true, user: { role: 'Administrator' } }));
vi.mock('../security', () => ({
  useSecurityAccount: () => account,
  SecurityAccountBadge: () => <span data-testid="account-badge" />,
}));
vi.mock('../EditorShell', () => { throw new Error('Terminal entry must not import the editor shell'); });
vi.mock('../components/ProductionPanel', () => ({
  default: (props: { runtimeOnly: boolean; initialView: string; stationId?: string; kiosk: boolean; headerActions?: ReactNode }) =>
    <div data-testid="production" data-runtime={String(props.runtimeOnly)} data-view={props.initialView}
      data-station={props.stationId} data-kiosk={String(props.kiosk)}>{props.headerActions}</div>,
}));
vi.mock('../components/SpcDashboard', () => ({ default: () => <div data-testid="spc" /> }));

function response(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(() => {
  account.user.role = 'Administrator';
  window.history.replaceState({}, '', '/');
});
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.history.replaceState({}, '', '/');
});

it.each([
  ['/?view=operator&kiosk=1&stationId=line-a', 'Administrator', false, 'operator', 'line-a', true],
  ['/?view=stations&kiosk=1&stationId=ignored', 'Administrator', false, 'stations', null, false],
  ['/', 'Operator', false, 'operator', null, false],
  ['/', 'Administrator', true, 'operator', null, false],
] as const)('routes %s (%s) without importing the editor or fetching its data', async (url, role, runtimeOnly, view, station, kiosk) => {
  window.history.replaceState({}, '', url);
  account.user.role = role;
  const fetcher = vi.fn<typeof fetch>(async () => response({ runtimeOnly }));
  vi.stubGlobal('fetch', fetcher);
  render(<App />);
  const panel = await screen.findByTestId('production');
  expect(panel.getAttribute('data-runtime')).toBe(String(runtimeOnly));
  expect(panel.getAttribute('data-view')).toBe(view);
  expect(panel.getAttribute('data-station')).toBe(station);
  expect(panel.getAttribute('data-kiosk')).toBe(String(kiosk));
  expect(screen.queryByTestId('account-badge') !== null).toBe(!kiosk);
  expect(fetcher.mock.calls.map((call) => call[0])).toEqual(['/api/host-mode']);
});

it('routes SPC before host-mode and role fallback', async () => {
  account.user.role = 'Operator';
  window.history.replaceState({}, '', '/?view=spc&kiosk=1');
  const fetcher = vi.fn();
  vi.stubGlobal('fetch', fetcher);
  render(<App />);
  expect(await screen.findByTestId('spc')).toBeTruthy();
  expect(fetcher).not.toHaveBeenCalled();
});

it('never mounts an editor while runtime-only discovery is pending', async () => {
  let resolve!: (result: Response) => void;
  vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>((done) => { resolve = done; })));
  render(<App />);
  expect(screen.getByRole('status')).toBeTruthy();
  expect(screen.queryByTestId('production')).toBeNull();
  await act(async () => { resolve(response({ runtimeOnly: true })); });
  expect(await screen.findByTestId('production')).toBeTruthy();
});

it.each([response({ detail: 'host unavailable' }, 503), response({ runtimeOnly: 'false' })])(
  'shows host-mode failure instead of falling into the editor and allows retry', async (failedResponse) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValueOnce(failedResponse).mockResolvedValueOnce(response({ runtimeOnly: true })));
    render(<App />);
    expect(await screen.findByText('无法确认宿主运行模式')).toBeTruthy();
    expect(screen.queryByTestId('production')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /重\s*试/ }));
    expect(await screen.findByTestId('production')).toBeTruthy();
  },
);
