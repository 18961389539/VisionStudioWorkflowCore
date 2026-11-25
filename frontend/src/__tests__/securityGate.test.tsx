import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import SecurityGate, { SecurityAccountBadge } from '../security';

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const user = { id: 'u1', username: 'admin', displayName: '管理员', role: 'Administrator' };

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class {
    observe() { /* noop */ }
    unobserve() { /* noop */ }
    disconnect() { /* noop */ }
  });
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.sessionStorage.clear();
});

describe('SecurityGate request flow', () => {
  it('shows the login form when there is no session and enters the app after login', async () => {
    let session = false;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/auth/status') return json({ enabled: true, bootstrapRequired: false, autoLogin: false });
      if (url === '/api/auth/me') return session ? json(user) : json({ detail: 'unauthorized' }, 401);
      if (url === '/api/auth/login') { session = true; return json({ user }); }
      return json({}, 404);
    }));

    render(<SecurityGate><div>protected-ui</div></SecurityGate>);

    expect(await screen.findByRole('button', { name: /登\s*录/ })).toBeTruthy();
    expect(screen.queryByText('protected-ui')).toBeNull();
    fireEvent.change(screen.getByPlaceholderText('密码'), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: /登\s*录/ }));

    expect(await screen.findByText('protected-ui')).toBeTruthy();
  });

  it('falls back to the login form when the session expires', async () => {
    let session = true;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url === '/api/auth/status') return json({ enabled: true, bootstrapRequired: false, autoLogin: false });
      if (url === '/api/auth/me') return session ? json(user) : json({ detail: 'expired' }, 401);
      return json({}, 404);
    }));

    render(<SecurityGate><div>protected-ui</div></SecurityGate>);
    expect(await screen.findByText('protected-ui')).toBeTruthy();

    session = false;
    fireEvent.focus(window);

    await waitFor(() => expect(screen.getByRole('button', { name: /登\s*录/ })).toBeTruthy());
    expect(screen.queryByText('protected-ui')).toBeNull();
  });

  it('allows an operator to change password and requires re-login afterward', async () => {
    const operator = { id: 'op1', username: 'op1', displayName: '产线操作员', role: 'Operator' as const };
    let changed = false;
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url === '/api/auth/status') return json({ enabled: true, bootstrapRequired: false, autoLogin: false });
      if (url === '/api/auth/me') return changed ? json({ detail: 'revoked' }, 401) : json(operator);
      if (url === '/api/auth/change-password' && init?.method === 'POST') {
        changed = true;
        return json({ changed: true, reauthenticationRequired: true });
      }
      return json({}, 404);
    }));

    render(<SecurityGate><SecurityAccountBadge /></SecurityGate>);
    const trigger = await screen.findByRole('button', { name: /产线操作员/ });
    fireEvent.click(trigger);

    fireEvent.click(await screen.findByText('修改密码…'));
    fireEvent.change(await screen.findByPlaceholderText('当前密码'), { target: { value: 'old-password-123' } });
    fireEvent.change(screen.getByPlaceholderText('新密码（10–256 个字符）'), { target: { value: 'new-password-456' } });
    fireEvent.change(screen.getByPlaceholderText('确认新密码'), { target: { value: 'new-password-456' } });
    fireEvent.click(screen.getByRole('button', { name: /确认修改/ }));

    expect(await screen.findByText('密码已修改，请使用新密码重新登录。')).toBeTruthy();
    expect(screen.getByRole('button', { name: /登\s*录/ })).toBeTruthy();
  });
});
