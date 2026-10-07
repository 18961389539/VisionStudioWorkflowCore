import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Alert, Button, Card, Dropdown, Input, Space, Spin, Tag, Typography } from 'antd';
import { localizeStatus } from './i18n';

type SecurityStatus = { enabled: boolean; bootstrapRequired: boolean; autoLogin: boolean };
export type SecurityUser = { id: string; username: string; displayName: string; role: 'Operator' | 'Engineer' | 'Administrator' };

const AUTO_LOGIN_SKIPPED_KEY = 'visionstudio.auto-login-skipped';

function autoLoginSkipped() {
  try { return window.sessionStorage.getItem(AUTO_LOGIN_SKIPPED_KEY) === '1'; } catch { return false; }
}

// 手工退出登录后本标签页内不再自动登录；关闭标签页重新打开即恢复自动登录
function markAutoLoginSkipped() {
  try { window.sessionStorage.setItem(AUTO_LOGIN_SKIPPED_KEY, '1'); } catch { /* 忽略存储失败 */ }
}

async function readError(response: Response) {
  try {
    const data = await response.json();
    return data.detail ?? data.title ?? `HTTP ${response.status}`;
  } catch {
    return `HTTP ${response.status}`;
  }
}

export default function SecurityGate({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<SecurityStatus>();
  const [user, setUser] = useState<SecurityUser>();
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [username, setUsername] = useState('admin');
  const [displayName, setDisplayName] = useState('管理员');
  const [password, setPassword] = useState('');

  const refresh = useCallback(async () => {
    try {
      const statusResponse = await fetch('/api/auth/status');
      if (!statusResponse.ok) throw new Error(await readError(statusResponse));
      const nextStatus: SecurityStatus = await statusResponse.json();
      setStatus(nextStatus);
      if (!nextStatus.enabled) { setUser(undefined); return; }
      const me = await fetch('/api/auth/me');
      if (me.ok) { setUser(await me.json()); return; }
      if (me.status !== 401) throw new Error(await readError(me));
      if (nextStatus.autoLogin && !autoLoginSkipped()) {
        const auto = await fetch('/api/auth/auto-login', { method: 'POST' });
        if (auto.ok) { setUser((await auto.json()).user); return; }
        setError(await readError(auto));
      }
      setUser(undefined);
    } catch (e) {
      setError(e instanceof Error ? e.message : '安全服务不可用');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);
  useEffect(() => {
    if (!status?.enabled || !user) return;
    const verify = () => { void fetch('/api/auth/me').then(async r => {
      if (r.ok) setUser(await r.json());
      else if (r.status === 401) setUser(undefined);
    }).catch(() => undefined); };
    const timer = window.setInterval(verify, 60_000);
    window.addEventListener('focus', verify);
    return () => { window.clearInterval(timer); window.removeEventListener('focus', verify); };
  }, [status?.enabled, user?.id]);

  const login = async (name = username, secret = password) => {
    setBusy(true); setError(undefined);
    try {
      const response = await fetch('/api/auth/login', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: name, password: secret })
      });
      if (!response.ok) throw new Error(await readError(response));
      const data = await response.json();
      setUser(data.user);
      setStatus(current => current ? { ...current, bootstrapRequired: false } : current);
      setPassword('');
    } catch (e) { setError(e instanceof Error ? e.message : '登录失败'); }
    finally { setBusy(false); }
  };

  const bootstrap = async () => {
    setBusy(true); setError(undefined);
    try {
      const response = await fetch('/api/auth/bootstrap', {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username, displayName, password })
      });
      if (!response.ok) throw new Error(await readError(response));
      await login(username, password);
    } catch (e) { setError(e instanceof Error ? e.message : '初始化管理员账户失败'); setBusy(false); }
  };

  const logout = async () => {
    setBusy(true);
    try { await fetch('/api/auth/logout', { method: 'POST' }); }
    finally { markAutoLoginSkipped(); setUser(undefined); setBusy(false); }
  };

  if (loading) return <div className="security-screen"><Spin size="large" /><div>正在加载 VisionStudio 安全设置…</div></div>;

  if (status?.enabled && !user) {
    const firstRun = status.bootstrapRequired;
    return (
      <div className="security-screen">
        <Card className="security-card" title={firstRun ? 'VisionStudio · 首次安全设置' : 'VisionStudio · 登录'}>
          <Space direction="vertical" size={12} style={{ width: '100%' }}>
            {firstRun && <Alert type="info" showIcon message="创建首个管理员账户" description="创建首个账户后，此初始化入口会自动关闭。" />}
            {error && <Alert type="error" showIcon message={error} />}
            <Input value={username} onChange={e => setUsername(e.target.value)} placeholder="用户名" autoComplete="username" />
            {firstRun && <Input value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder="显示名称" />}
            <Input.Password value={password} onChange={e => setPassword(e.target.value)} placeholder="密码（至少 10 个字符）" autoComplete={firstRun ? 'new-password' : 'current-password'} onPressEnter={() => void (firstRun ? bootstrap() : login())} />
            <Button type="primary" block loading={busy} disabled={username.trim().length < 3 || password.length < 10} onClick={() => void (firstRun ? bootstrap() : login())}>
              {firstRun ? '创建管理员并登录' : '登录'}
            </Button>
            <Typography.Text type="secondary">本地凭据以 PBKDF2 哈希形式保存。浏览器会话使用 HttpOnly Cookie。</Typography.Text>
          </Space>
        </Card>
      </div>
    );
  }

  return <>
    {children}
    {/* 账号入口：收起为顶栏右上角的一个轻量按钮（角色色点+显示名），点击展开退出登录 */}
    <div className="security-badge">
      {status?.enabled ? (
        <Dropdown
          trigger={['click']}
          menu={{
            items: [
              { key: 'role', label: `角色：${localizeStatus(user?.role) ?? '未知'}`, disabled: true },
              { key: 'name', label: `用户：${user?.displayName ?? user?.username ?? '未知'}`, disabled: true },
              { type: 'divider' },
              { key: 'logout', label: '退出登录', danger: true, onClick: () => void logout() }
            ]
          }}
        >
          <button type="button" className="security-badge-trigger" title="账号">
            <span className={`security-role-dot role-${String(user?.role ?? 'operator').toLowerCase()}`} />
            {user?.displayName ?? user?.username ?? '未登录'}
          </button>
        </Dropdown>
      ) : <Tag color="orange">安全功能已关闭</Tag>}
    </div>
  </>;
}
