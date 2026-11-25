import { useCallback, useEffect, useState } from 'react';
import {
  Alert, Button, Checkbox, Form, Input, InputNumber, Modal, Popconfirm, Select, Space, Switch, Table, Tabs, Tag, Typography, message
} from 'antd';
import type { ContractDto } from '../types';
import { apiFetch } from '../apiClient';
import { responseErrorMessage } from '../apiErrors';
import { security } from '../endpoints';
import { localizeStatus } from '../i18n';
import { useSecurityAccount } from '../security';

type SecurityUserDto = ContractDto<'SecurityUserDto'>;
type ServiceTokenDto = ContractDto<'ServiceTokenDto'>;
type ServiceTokenCreated = ContractDto<'ServiceTokenCreated'>;
type AuditEventDto = ContractDto<'AuditEventDto'>;
type AuditChainVerification = ContractDto<'AuditChainVerification'>;
type UserRole = 'Operator' | 'Engineer' | 'Administrator';

const ROLE_OPTIONS: Array<{ value: UserRole; label: string }> = [
  { value: 'Operator', label: '操作员 (Operator)' },
  { value: 'Engineer', label: '工程师 (Engineer)' },
  { value: 'Administrator', label: '管理员 (Administrator)' },
];

const SERVICE_SCOPE_OPTIONS = [
  { value: 'results:read', label: 'results:read · 读取追溯与统计' },
  { value: 'stations:read', label: 'stations:read · 读取工位状态' },
  { value: 'stations:deliver', label: 'stations:deliver · 接收下发配方' },
  { value: 'stations:control', label: 'stations:control · 启停生产' },
  { value: 'runs:run', label: 'runs:run · 触发流程运行' },
];

type Props = { open: boolean; onClose: () => void };

export default function SecurityAdminPanel({ open, onClose }: Props) {
  const { enabled: securityEnabled, user } = useSecurityAccount();
  const canAdmin = !securityEnabled || user?.role === 'Administrator';
  const [messageApi, contextHolder] = message.useMessage();
  const [activeTab, setActiveTab] = useState<'users' | 'tokens' | 'audit'>('users');
  const [busyKey, setBusyKey] = useState<string>();
  const [loadError, setLoadError] = useState<string>();

  const [users, setUsers] = useState<SecurityUserDto[]>([]);
  const [createUserOpen, setCreateUserOpen] = useState(false);
  const [editingUser, setEditingUser] = useState<SecurityUserDto>();
  const [resetUser, setResetUser] = useState<SecurityUserDto>();
  const [createUserForm] = Form.useForm<{ username: string; displayName: string; role: UserRole; password: string }>();
  const [editUserForm] = Form.useForm<{ displayName: string; role: UserRole; enabled: boolean }>();
  const [resetPasswordForm] = Form.useForm<{ password: string }>();

  const [tokens, setTokens] = useState<ServiceTokenDto[]>([]);
  const [createTokenOpen, setCreateTokenOpen] = useState(false);
  const [revealedToken, setRevealedToken] = useState<ServiceTokenCreated>();
  const [createTokenForm] = Form.useForm<{ name: string; scopes: string[]; expiresInDays?: number }>();

  const [auditEvents, setAuditEvents] = useState<AuditEventDto[]>([]);
  const [auditUsername, setAuditUsername] = useState('');
  const [auditAction, setAuditAction] = useState('');
  const [verification, setVerification] = useState<AuditChainVerification>();

  const buildAuditUrl = useCallback(() => {
    const params = new URLSearchParams({ offset: '0', limit: '100' });
    if (auditUsername.trim()) params.set('username', auditUsername.trim());
    if (auditAction.trim()) params.set('action', auditAction.trim());
    return security.audit(params.toString());
  }, [auditAction, auditUsername]);

  const refreshAll = useCallback(async () => {
    if (!canAdmin) return;
    setLoadError(undefined);
    try {
      const [usersRes, tokensRes, auditRes] = await Promise.all([
        apiFetch(security.users),
        apiFetch(security.serviceTokens),
        apiFetch(buildAuditUrl()),
      ]);
      const failures: string[] = [];
      if (usersRes.ok) setUsers(await usersRes.json() as SecurityUserDto[]);
      else failures.push(await responseErrorMessage(usersRes, '用户列表读取失败'));

      if (tokensRes.ok) setTokens(await tokensRes.json() as ServiceTokenDto[]);
      else failures.push(await responseErrorMessage(tokensRes, '服务令牌读取失败'));

      if (auditRes.ok) setAuditEvents(await auditRes.json() as AuditEventDto[]);
      else failures.push(await responseErrorMessage(auditRes, '审计日志读取失败'));

      if (failures.length > 0) setLoadError(failures.join('；'));
    } catch (ex) {
      setLoadError(ex instanceof Error ? ex.message : '安全管理数据读取失败');
    }
  }, [buildAuditUrl, canAdmin]);

  useEffect(() => {
    if (open && canAdmin) void refreshAll();
  }, [open, canAdmin, refreshAll]);

  const queryAudit = async () => {
    setBusyKey('audit-query');
    try {
      const res = await apiFetch(buildAuditUrl());
      if (!res.ok) throw new Error(await responseErrorMessage(res, '审计日志查询失败'));
      setAuditEvents(await res.json() as AuditEventDto[]);
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '审计日志查询失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  const verifyAuditChain = async () => {
    setBusyKey('audit-verify');
    try {
      const res = await apiFetch(security.verifyAudit);
      if (!res.ok) throw new Error(await responseErrorMessage(res, '审计链校验失败'));
      const result = await res.json() as AuditChainVerification;
      setVerification(result);
      if (result.valid) messageApi.success(`审计链校验通过（共校验 ${result.checked} 条）`);
      else messageApi.error(`发现异常：第 ${result.firstInvalidSeq ?? '?'} 条 (${result.firstInvalidReason ?? 'unknown'})`);
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '审计链校验失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  const createUser = async () => {
    const values = await createUserForm.validateFields();
    setBusyKey('user-create');
    try {
      const res = await apiFetch(security.users, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(values),
      });
      if (!res.ok) throw new Error(await responseErrorMessage(res, '创建用户失败'));
      messageApi.success(`已创建用户 ${values.username}`);
      setCreateUserOpen(false);
      createUserForm.resetFields();
      await refreshAll();
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '创建用户失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  const saveEditUser = async () => {
    if (!editingUser) return;
    const values = await editUserForm.validateFields();
    setBusyKey(`user-edit:${editingUser.id}`);
    try {
      const res = await apiFetch(security.user(editingUser.id), {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(values),
      });
      if (!res.ok) throw new Error(await responseErrorMessage(res, '更新用户失败'));
      messageApi.success(`已更新用户 ${editingUser.username}`);
      setEditingUser(undefined);
      await refreshAll();
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '更新用户失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  const submitResetPassword = async () => {
    if (!resetUser) return;
    const values = await resetPasswordForm.validateFields();
    setBusyKey(`user-reset:${resetUser.id}`);
    try {
      const res = await apiFetch(security.resetPassword(resetUser.id), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(values),
      });
      if (!res.ok) throw new Error(await responseErrorMessage(res, '重置密码失败'));
      messageApi.success(`已重置 ${resetUser.username} 的密码（原会话已注销）`);
      setResetUser(undefined);
      resetPasswordForm.resetFields();
      await refreshAll();
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '重置密码失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  const unlockUser = async (target: SecurityUserDto) => {
    setBusyKey(`user-unlock:${target.id}`);
    try {
      const res = await apiFetch(security.unlock(target.id), { method: 'POST' });
      if (!res.ok) throw new Error(await responseErrorMessage(res, '解锁账号失败'));
      messageApi.success(`已解锁账号 ${target.username}`);
      await refreshAll();
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '解锁账号失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  const createServiceToken = async () => {
    const values = await createTokenForm.validateFields();
    setBusyKey('token-create');
    try {
      const res = await apiFetch(security.serviceTokens, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          name: values.name.trim(),
          scopes: values.scopes,
          expiresInDays: values.expiresInDays ?? null,
        }),
      });
      if (!res.ok) throw new Error(await responseErrorMessage(res, '签发服务令牌失败'));
      const created = await res.json() as ServiceTokenCreated;
      setRevealedToken(created);
      setCreateTokenOpen(false);
      createTokenForm.resetFields();
      messageApi.success(`已签发服务令牌 ${created.details.name}`);
      await refreshAll();
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '签发服务令牌失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  const rotateToken = async (target: ServiceTokenDto) => {
    setBusyKey(`token-rotate:${target.id}`);
    try {
      const res = await apiFetch(security.rotateServiceToken(target.id), { method: 'POST' });
      if (!res.ok) throw new Error(await responseErrorMessage(res, '轮换服务令牌失败'));
      const rotated = await res.json() as ServiceTokenCreated;
      setRevealedToken(rotated);
      messageApi.success(`已轮换令牌 ${target.name}，旧密钥立即失效`);
      await refreshAll();
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '轮换服务令牌失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  const revokeToken = async (target: ServiceTokenDto) => {
    setBusyKey(`token-revoke:${target.id}`);
    try {
      const res = await apiFetch(security.revokeServiceToken(target.id), { method: 'POST' });
      if (!res.ok) throw new Error(await responseErrorMessage(res, '吊销服务令牌失败'));
      messageApi.success(`已吊销令牌 ${target.name}`);
      await refreshAll();
    } catch (ex) {
      messageApi.error(ex instanceof Error ? ex.message : '吊销服务令牌失败');
    } finally {
      setBusyKey(undefined);
    }
  };

  return (
    <Modal open={open} onCancel={onClose} footer={null} width="min(1160px, calc(100vw - 32px))" title="安全与运维管理 · 用户 / 服务令牌 / 审计" destroyOnHidden>
      {contextHolder}
      {!canAdmin ? (
        <Alert
          type="warning"
          showIcon
          title="需要管理员权限（Administrator）"
          description="账号管理、服务令牌签发/吊销与审计链核验仅限管理员角色访问。"
        />
      ) : (
        <Space orientation="vertical" size={12} style={{ width: '100%' }}>
          {loadError && <Alert type="error" showIcon title="部分安全管理数据加载失败" description={loadError} action={<Button size="small" onClick={() => void refreshAll()}>重试</Button>} />}
          <Tabs
            activeKey={activeTab}
            onChange={(key) => setActiveTab(key as 'users' | 'tokens' | 'audit')}
            items={[
              {
                key: 'users',
                label: '用户管理',
                children: (
                  <Space orientation="vertical" size={10} style={{ width: '100%' }}>
                    <Space>
                      <Button type="primary" onClick={() => { createUserForm.setFieldsValue({ role: 'Operator' }); setCreateUserOpen(true); }}>新建用户</Button>
                      <Button onClick={() => void refreshAll()}>刷新</Button>
                    </Space>
                    <Table<SecurityUserDto>
                      size="small"
                      rowKey="id"
                      dataSource={users}
                      pagination={{ pageSize: 8 }}
                      columns={[
                        { title: '用户名', dataIndex: 'username' },
                        { title: '显示名称', dataIndex: 'displayName' },
                        { title: '角色', dataIndex: 'role', width: 130, render: (v: string) => <Tag color={v === 'Administrator' ? 'red' : v === 'Engineer' ? 'blue' : 'green'}>{localizeStatus(v)}</Tag> },
                        { title: '状态', width: 90, render: (_, row) => <Tag color={row.enabled ? 'green' : 'default'}>{row.enabled ? '启用' : '停用'}</Tag> },
                        { title: '最近登录', width: 170, render: (_, row) => row.lastLoginAt ? new Date(row.lastLoginAt).toLocaleString('zh-CN') : '—' },
                        {
                          title: '操作',
                          width: 240,
                          render: (_, row) => (
                            <Space size="small">
                              <Button size="small" onClick={() => {
                                setEditingUser(row);
                                editUserForm.setFieldsValue({ displayName: row.displayName, role: row.role as UserRole, enabled: row.enabled });
                              }}>编辑</Button>
                              <Button size="small" onClick={() => { setResetUser(row); resetPasswordForm.resetFields(); }}>重置密码</Button>
                              <Button size="small" loading={busyKey === `user-unlock:${row.id}`} onClick={() => void unlockUser(row)}>解锁</Button>
                            </Space>
                          ),
                        },
                      ]}
                    />
                  </Space>
                ),
              },
              {
                key: 'tokens',
                label: '服务令牌 (MES/上位机)',
                children: (
                  <Space orientation="vertical" size={10} style={{ width: '100%' }}>
                    {revealedToken && (
                      <Alert
                        type="warning"
                        showIcon
                        closable
                        onClose={() => setRevealedToken(undefined)}
                        title={`令牌「${revealedToken.details.name}」明文仅本次可见`}
                        description={
                          <Space orientation="vertical" size={4}>
                            <Typography.Text copyable code>{revealedToken.token}</Typography.Text>
                            <Typography.Text type="secondary">请立即复制到 MES 或上位机密钥库；刷新或关闭后系统只保留前缀 {revealedToken.details.prefix}。</Typography.Text>
                          </Space>
                        }
                      />
                    )}
                    <Space>
                      <Button type="primary" onClick={() => { createTokenForm.setFieldsValue({ scopes: ['results:read'], expiresInDays: 180 }); setCreateTokenOpen(true); }}>签发令牌</Button>
                      <Button onClick={() => void refreshAll()}>刷新</Button>
                    </Space>
                    <Table<ServiceTokenDto>
                      size="small"
                      rowKey="id"
                      dataSource={tokens}
                      pagination={{ pageSize: 8 }}
                      columns={[
                        { title: '名称', dataIndex: 'name' },
                        { title: '前缀', dataIndex: 'prefix', width: 130, render: (v: string) => <code>{v}</code> },
                        { title: '作用域', dataIndex: 'scopes', render: (scopes: string[]) => <Space wrap size={4}>{scopes.map((s) => <Tag key={s}>{s}</Tag>)}</Space> },
                        { title: '状态', width: 90, render: (_, row) => row.revokedAt ? <Tag color="red">已吊销</Tag> : <Tag color="green">有效</Tag> },
                        { title: '最近使用', width: 170, render: (_, row) => row.lastUsedAt ? new Date(row.lastUsedAt).toLocaleString('zh-CN') : '—' },
                        { title: '过期时间', width: 170, render: (_, row) => row.expiresAt ? new Date(row.expiresAt).toLocaleString('zh-CN') : '永不过期' },
                        {
                          title: '操作',
                          width: 160,
                          render: (_, row) => (
                            <Space size="small">
                              <Popconfirm title="轮换服务令牌" description="轮换会生成新密钥并使当前密钥立即失效，确认继续？" okText="轮换" cancelText="取消" onConfirm={() => void rotateToken(row)}>
                                <Button size="small" disabled={Boolean(row.revokedAt)} loading={busyKey === `token-rotate:${row.id}`}>轮换</Button>
                              </Popconfirm>
                              <Popconfirm title="吊销服务令牌" description={`吊销后「${row.name}」无法再访问接口且不可恢复。`} okText="吊销" cancelText="取消" okButtonProps={{ danger: true }} onConfirm={() => void revokeToken(row)}>
                                <Button size="small" danger disabled={Boolean(row.revokedAt)} loading={busyKey === `token-revoke:${row.id}`}>吊销</Button>
                              </Popconfirm>
                            </Space>
                          ),
                        },
                      ]}
                    />
                  </Space>
                ),
              },
              {
                key: 'audit',
                label: '审计日志与链校验',
                children: (
                  <Space orientation="vertical" size={10} style={{ width: '100%' }}>
                    <Space wrap>
                      <Input style={{ width: 180 }} placeholder="按用户名筛选" value={auditUsername} onChange={(e) => setAuditUsername(e.target.value)} onPressEnter={() => void queryAudit()} />
                      <Input style={{ width: 220 }} placeholder="按动作筛选，如 job.publish" value={auditAction} onChange={(e) => setAuditAction(e.target.value)} onPressEnter={() => void queryAudit()} />
                      <Button loading={busyKey === 'audit-query'} onClick={() => void queryAudit()}>查询</Button>
                      <Button type="primary" ghost loading={busyKey === 'audit-verify'} onClick={() => void verifyAuditChain()}>校验审计链完整性</Button>
                    </Space>
                    {verification && (
                      <Alert
                        type={verification.valid ? 'success' : 'error'}
                        showIcon
                        title={verification.valid ? `审计哈希链完整（已核验 ${verification.checked} 条）` : `审计哈希链校验失败（异常序号 ${verification.firstInvalidSeq ?? '未知'}）`}
                        description={verification.valid
                          ? `最新序号 ${verification.lastSeq ?? 0} · 校验时间 ${new Date(verification.verifiedAt).toLocaleString('zh-CN')}`
                          : `原因：${verification.firstInvalidReason ?? 'unknown'}`}
                      />
                    )}
                    <Table<AuditEventDto>
                      size="small"
                      rowKey="sequence"
                      dataSource={auditEvents}
                      pagination={{ pageSize: 10 }}
                      columns={[
                        { title: '序号', dataIndex: 'sequence', width: 70 },
                        { title: '时间', dataIndex: 'at', width: 170, render: (v: string) => new Date(v).toLocaleString('zh-CN') },
                        { title: '用户 / 角色', width: 180, render: (_, row) => `${row.username ?? '匿名'} (${localizeStatus(row.role) ?? '—'})` },
                        { title: '动作', dataIndex: 'action', width: 190 },
                        { title: '目标', dataIndex: 'target', width: 160, ellipsis: true, render: (v?: string | null) => v ?? '—' },
                        { title: '状态', width: 90, render: (_, row) => <Tag color={row.success ? 'green' : 'red'}>{row.statusCode}</Tag> },
                        { title: '路径', width: 200, ellipsis: true, render: (_, row) => <code>{row.method} {row.path}</code> },
                        { title: '来源 IP', dataIndex: 'clientIp', width: 130, render: (v?: string | null) => v ?? '—' },
                      ]}
                    />
                  </Space>
                ),
              },
            ]}
          />
        </Space>
      )}

      <Modal open={createUserOpen} title="新建安全用户" okText="创建" cancelText="取消" confirmLoading={busyKey === 'user-create'} onCancel={() => setCreateUserOpen(false)} onOk={() => void createUser()} destroyOnHidden>
        <Form form={createUserForm} layout="vertical">
          <Form.Item name="username" label="用户名" rules={[{ required: true, min: 3, max: 64, message: '用户名需 3–64 个字符' }]}>
            <Input placeholder="例如 op-line1" />
          </Form.Item>
          <Form.Item name="displayName" label="显示名称" rules={[{ max: 128 }]}>
            <Input placeholder="例如 一号产线操作员" />
          </Form.Item>
          <Form.Item name="role" label="角色" rules={[{ required: true }]}>
            <Select options={ROLE_OPTIONS} />
          </Form.Item>
          <Form.Item name="password" label="初始密码" rules={[{ required: true, min: 10, max: 256, message: '密码需 10–256 个字符' }]}>
            <Input.Password />
          </Form.Item>
        </Form>
      </Modal>

      <Modal open={Boolean(editingUser)} title={`编辑用户 · ${editingUser?.username ?? ''}`} okText="保存" cancelText="取消" confirmLoading={Boolean(editingUser && busyKey === `user-edit:${editingUser.id}`)} onCancel={() => setEditingUser(undefined)} onOk={() => void saveEditUser()} destroyOnHidden>
        <Form form={editUserForm} layout="vertical">
          <Form.Item name="displayName" label="显示名称" rules={[{ max: 128 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="role" label="角色" rules={[{ required: true }]}>
            <Select options={ROLE_OPTIONS} />
          </Form.Item>
          <Form.Item name="enabled" label="启用状态" valuePropName="checked">
            <Switch checkedChildren="启用" unCheckedChildren="停用" />
          </Form.Item>
        </Form>
      </Modal>

      <Modal open={Boolean(resetUser)} title={`重置密码 · ${resetUser?.username ?? ''}`} okText="确认重置" cancelText="取消" confirmLoading={Boolean(resetUser && busyKey === `user-reset:${resetUser.id}`)} onCancel={() => setResetUser(undefined)} onOk={() => void submitResetPassword()} destroyOnHidden>
        <Form form={resetPasswordForm} layout="vertical">
          <Form.Item name="password" label="新密码（10–256 个字符）" rules={[{ required: true, min: 10, max: 256, message: '密码需 10–256 个字符' }]}>
            <Input.Password />
          </Form.Item>
        </Form>
      </Modal>

      <Modal open={createTokenOpen} title="签发服务令牌" okText="签发" cancelText="取消" confirmLoading={busyKey === 'token-create'} onCancel={() => setCreateTokenOpen(false)} onOk={() => void createServiceToken()} destroyOnHidden>
        <Form form={createTokenForm} layout="vertical">
          <Form.Item name="name" label="令牌名称" rules={[{ required: true, message: '请填写唯一的令牌名称，如 mes-line1' }]}>
            <Input placeholder="例如 mes-line1" />
          </Form.Item>
          <Form.Item name="scopes" label="允许作用域" rules={[{ required: true, message: '至少选择一个作用域' }]}>
            <Checkbox.Group options={SERVICE_SCOPE_OPTIONS} />
          </Form.Item>
          <Form.Item name="expiresInDays" label="有效天数（留空表示永不过期，1–3650）">
            <InputNumber min={1} max={3650} style={{ width: '100%' }} />
          </Form.Item>
        </Form>
      </Modal>
    </Modal>
  );
}
