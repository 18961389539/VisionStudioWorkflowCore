import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { Alert, Button, Card, Dropdown, Input, Modal, Space, Spin, Tag, Typography } from 'antd';
import { localizeStatus } from './i18n';
import { apiFetch } from './apiClient';
import { auth } from './endpoints';

type SecurityStatus = { enabled: boolean; bootstrapRequired: boolean; autoLogin: boolean };
export type SecurityUser = { id: string; username: string; displayName: string; role: 'Operator' | 'Engineer' | 'Administrator' };

/**
 * 账号上下文：SecurityGate 持有登录态，账号徽标由顶栏（或运行时页）在**普通 flex 流**中渲染。
 * 之前徽标是 gate 内固定定位（fixed + 顶栏预留 108px）的浮层——长显示名会挤占「运行」按钮，
 * 且与运行时页的「诊断」按钮叠压。改为上下文 + 组件后，布局完全由所在容器的 flex 决定。
 */
type SecurityAccount = {
  enabled: boolean;
  user?: SecurityUser;
  busy: boolean;
  logout: () => Promise<void>;
  onPasswordChanged: () => void;
};
const SecurityAccountContext = createContext<SecurityAccount>({
  enabled: false,
  busy: false,
  logout: async () => undefined,
  onPasswordChanged: () => undefined
});

export function useSecurityAccount() {
  return useContext(SecurityAccountContext);
}

/** 账号徽标：角色色点 + 显示名，点击展开角色/用户信息、修改密码与退出登录。 */
export function SecurityAccountBadge() {
  const { enabled, user, busy, logout, onPasswordChanged } = useContext(SecurityAccountContext);
  const [passwordOpen, setPasswordOpen] = useState(false);
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [saving, setSaving] = useState(false);
  const [passwordError, setPasswordError] = useState<string>();

  const name = user?.displayName ?? user?.username;
  if (!enabled) return <Tag className="security-badge-disabled" color="orange">安全功能已关闭</Tag>;

  const openPasswordModal = () => {
    setCurrentPassword('');
    setNewPassword('');
    setConfirmPassword('');
    setPasswordError(undefined);
    setPasswordOpen(true);
  };

  const closePasswordModal = () => {
    if (saving) return;
    setPasswordOpen(false);
    setPasswordError(undefined);
  };

  const submitPassword = async () => {
    if (!currentPassword) {
      setPasswordError('请输入当前密码。');
      return;
    }
    if (newPassword.length < 10 || newPassword.length > 256) {
      setPasswordError('新密码长度需为 10 到 256 个字符。');
      return;
    }
    if (newPassword !== confirmPassword) {
      setPasswordError('两次输入的新密码不一致。');
      return;
    }
    setSaving(true);
    setPasswordError(undefined);
    try {
      const response = await apiFetch(auth.changePassword, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ currentPassword, newPassword })
      });
      if (!response.ok) throw new Error(await readError(response));
      setPasswordOpen(false);
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
      onPasswordChanged();
    } catch (e) {
      setPasswordError(e instanceof Error ? e.message : '修改密码失败');
    } finally {
      setSaving(false);
    }
  };

  return (
    <>
      <Dropdown
        trigger={['click']}
        menu={{
          items: [
            { key: 'role', label: `角色：${localizeStatus(user?.role) ?? '未知'}`, disabled: true },
            { key: 'name', label: `用户：${name ?? '未知'}`, disabled: true },
            { type: 'divider' },
            ...(user ? [{ key: 'change-password', label: '修改密码…', disabled: busy, onClick: openPasswordModal }] : []),
            { key: 'logout', label: '退出登录', danger: true, disabled: busy, onClick: () => void logout() }
          ]
        }}
      >
        <button type="button" className="security-badge-trigger" title="账号：查看角色、修改密码与退出登录">
          <span className={`security-role-dot role-${String(user?.role ?? 'operator').toLowerCase()}`} />
          <span className="security-badge-name">{name ?? '未登录'}</span>
        </button>
      </Dropdown>
      <Modal
        open={passwordOpen}
        title="修改登录密码"
        okText="确认修改"
        cancelText="取消"
        confirmLoading={saving}
        closable={!saving}
        keyboard={!saving}
        mask={{ closable: !saving }}
        cancelButtonProps={{ disabled: saving }}
        onCancel={closePasswordModal}
        onOk={() => void submitPassword()}
        destroyOnHidden
      >
        <Space orientation="vertical" size={10} style={{ width: '100%' }}>
          {passwordError && <Alert type="error" showIcon title={passwordError} />}
          <Input.Password
            value={currentPassword}
            onChange={(e) => setCurrentPassword(e.target.value)}
            placeholder="当前密码"
            autoComplete="current-password"
            disabled={saving}
          />
          <Input.Password
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            placeholder="新密码（10–256 个字符）"
            autoComplete="new-password"
            disabled={saving}
          />
          <Input.Password
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
            placeholder="确认新密码"
            autoComplete="new-password"
            disabled={saving}
            onPressEnter={() => void submitPassword()}
          />
          <Typography.Text type="secondary">修改成功后当前会话会注销，需使用新密码重新登录。</Typography.Text>
        </Space>
      </Modal>
    </>
  );
}

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
  const [notice, setNotice] = useState<string>();
  const [username, setUsername] = useState('admin');
  const [displayName, setDisplayName] = useState('管理员');
  const [password, setPassword] = useState('');

  const refresh = useCallback(async () => {
    try {
      const statusResponse = await apiFetch(auth.status);
      if (!statusResponse.ok) throw new Error(await readError(statusResponse));
      const nextStatus: SecurityStatus = await statusResponse.json();
      setStatus(nextStatus);
      if (!nextStatus.enabled) { setUser(undefined); return; }
      const me = await apiFetch(auth.me);
      if (me.ok) { setUser(await me.json()); return; }
      if (me.status !== 401) throw new Error(await readError(me));
      if (nextStatus.autoLogin && !autoLoginSkipped()) {
        const auto = await apiFetch(auth.autoLogin, { method: 'POST' });
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
  const userId = user?.id;
  useEffect(() => {
    if (!status?.enabled || userId == null) return;
    const verify = () => { void apiFetch(auth.me).then(async r => {
      if (r.ok) setUser(await r.json());
      else if (r.status === 401) setUser(undefined);
    }).catch(() => undefined); };
    const timer = window.setInterval(verify, 60_000);
    window.addEventListener('focus', verify);
    return () => { window.clearInterval(timer); window.removeEventListener('focus', verify); };
  }, [status?.enabled, userId]);

  const login = async (name = username, secret = password) => {
    setBusy(true); setError(undefined); setNotice(undefined);
    try {
      const response = await apiFetch(auth.login, {
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
      const response = await apiFetch(auth.bootstrap, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username, displayName, password })
      });
      if (!response.ok) throw new Error(await readError(response));
      await login(username, password);
    } catch (e) { setError(e instanceof Error ? e.message : '初始化管理员账户失败'); setBusy(false); }
  };

  const logout = async () => {
    setBusy(true);
    try { await apiFetch(auth.logout, { method: 'POST' }); }
    finally { markAutoLoginSkipped(); setNotice(undefined); setUser(undefined); setBusy(false); }
  };

  const onPasswordChanged = useCallback(() => {
    markAutoLoginSkipped();
    setError(undefined);
    setNotice('密码已修改，请使用新密码重新登录。');
    setUser(undefined);
  }, []);

  if (loading) return <div className="security-screen"><Spin size="large" /><div>正在加载 VisionStudio 安全设置…</div></div>;

  if (status?.enabled && !user) {
    const firstRun = status.bootstrapRequired;
    return (
      <div className="security-screen">
        <Card className="security-card" title={firstRun ? 'VisionStudio · 首次安全设置' : 'VisionStudio · 登录'}>
          <Space orientation="vertical" size={12} style={{ width: '100%' }}>
            {firstRun && <Alert type="info" showIcon title="创建首个管理员账户" description="创建首个账户后，此初始化入口会自动关闭。" />}
            {notice && <Alert type="success" showIcon title={notice} />}
            {error && <Alert type="error" showIcon title={error} />}
            <Input value={username} onChange={e => setUsername(e.target.value)} placeholder="用户名" autoComplete="username" />
            {firstRun && <Input value={displayName} onChange={e => setDisplayName(e.target.value)} placeholder="显示名称" />}
            <Input.Password value={password} onChange={e => setPassword(e.target.value)} placeholder={firstRun ? '密码（至少 10 个字符）' : '密码'} autoComplete={firstRun ? 'new-password' : 'current-password'} onPressEnter={() => void (firstRun ? bootstrap() : login())} />
            <Button type="primary" block loading={busy} disabled={username.trim().length < 3 || password.length < (firstRun ? 10 : 1)} onClick={() => void (firstRun ? bootstrap() : login())}>
              {firstRun ? '创建管理员并登录' : '登录'}
            </Button>
            <Typography.Text type="secondary">本地凭据以 PBKDF2 哈希形式保存。浏览器会话使用 HttpOnly Cookie。</Typography.Text>
          </Space>
        </Card>
      </div>
    );
  }

  return <SecurityAccountContext.Provider value={{ enabled: Boolean(status?.enabled), user, busy, logout, onPasswordChanged }}>
    {children}
  </SecurityAccountContext.Provider>;
}
