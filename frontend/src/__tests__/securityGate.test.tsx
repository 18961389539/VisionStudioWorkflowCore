import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import SecurityGate from '../security';

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

const user = { id: 'u1', username: 'admin', displayName: '管理员', role: 'Administrator' };

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
});
