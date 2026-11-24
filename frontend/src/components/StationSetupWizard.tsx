import { useMemo, useState } from 'react';
import { Alert, Button, Input, Modal, Space, message } from 'antd';
import {
  emptyStationDraft, stationConfigJson, stationEnvText, validateStationDraft,
  type StationConfigDraft, type StationDraftField,
} from '../stationConfigDraft';

type Props = {
  /** 新工位在 `ProductionFleet.Stations` 里的下标：环境变量按列表下标绑定，不能按 ID 猜。 */
  startIndex: number;
  existingIds: string[];
  stationCount: number;
  onClose: () => void;
};

function Field({ label, hint, error, children }: {
  label: string; hint?: string; error?: string; children: React.ReactNode;
}) {
  return <label style={{ display: 'grid', gap: 4, alignContent: 'start' }}>
    <span style={{ fontSize: 12, color: 'var(--vs-text-3)' }}>{label}</span>
    {children}
    {error
      ? <span style={{ fontSize: 11, color: 'var(--vs-text-danger)' }}>{error}</span>
      : hint ? <span style={{ fontSize: 11, color: 'var(--vs-text-5)' }}>{hint}</span> : null}
  </label>;
}

const codeStyle: React.CSSProperties = {
  margin: 0, padding: 10, borderRadius: 6, background: 'var(--vs-bg-code)', color: 'var(--vs-text-code)',
  fontSize: 12, lineHeight: 1.5, maxHeight: 190, overflow: 'auto', whiteSpace: 'pre',
};

export default function StationSetupWizard({ startIndex, existingIds, stationCount, onClose }: Props) {
  const [messageApi, contextHolder] = message.useMessage();
  const [draft, setDraft] = useState<StationConfigDraft>(emptyStationDraft);
  const issues = validateStationDraft(draft, { existingIds, stationCount });
  const issueOf = (field: StationDraftField) => issues.find((issue) => issue.field === field)?.message;
  const ready = issues.length === 0;
  const json = useMemo(() => stationConfigJson(draft, startIndex), [draft, startIndex]);
  const env = useMemo(() => stationEnvText(draft, startIndex), [draft, startIndex]);
  const update = (field: StationDraftField) => (event: React.ChangeEvent<HTMLInputElement>) =>
    setDraft((previous) => ({ ...previous, [field]: event.target.value }));

  const copy = async (text: string, label: string) => {
    try {
      await navigator.clipboard.writeText(text);
      void messageApi.success(label + '已复制');
    } catch {
      void messageApi.warning('浏览器拒绝了剪贴板访问，请手动选中文本复制');
    }
  };

  return <Modal open title={stationCount > 0 ? `添加工位（第 ${startIndex + 1} 个）` : '配置第一个工位'}
    width={1040} footer={null} onCancel={onClose} destroyOnHidden>
    {contextHolder}
    <Space direction="vertical" size="middle" style={{ width: '100%' }}>
      <Alert type="info" showIcon title="向导只生成文本，不改配置文件"
        description={<>
          <ol style={{ margin: '4px 0 0', paddingLeft: 18, lineHeight: 1.7 }}>
            <li>在目标工位用管理员创建服务令牌：读取用 <code>stations:read</code>、配方下发用 <code>stations:deliver</code>、远程启停用 <code>stations:control</code>。</li>
            <li>把右侧 JSON 合并进总览宿主的 <code>site-config/appsettings.site.json</code>，令牌写入宿主运行账户的环境变量。</li>
            <li>重启总览宿主（配置文件不热加载），回到「多工位」点「刷新总览」。</li>
          </ol>
          <div style={{ marginTop: 6 }}>
            配置在启动时校验：<strong>ID 重复、地址不合规或令牌缺失都会让宿主拒绝启动</strong>，所以两段文本都要落地再重启。
          </div>
        </>} />

      {/* 左填右看：生成结果紧贴表单，改一个字段就能看到文本变化，不用来回滚动 */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(360px, 1fr))', gap: 16, alignItems: 'start' }}>
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))', gap: 10 }}>
          <Field label="工位 ID" hint="字母、数字、- 或 _，最长 64；总览与工位两侧必须一致" error={issueOf('id')}>
            <Input value={draft.id} onChange={update('id')} placeholder="inspection-a" aria-label="工位 ID" />
          </Field>
          <Field label="工位名称" hint="显示在总览列表里，最长 80" error={issueOf('name')}>
            <Input value={draft.name} onChange={update('name')} placeholder="正面检测" aria-label="工位名称" />
          </Field>
          <Field label="工位地址" hint="跨主机必须 https；http 仅限 localhost 测试" error={issueOf('baseUrl')}>
            <Input value={draft.baseUrl} onChange={update('baseUrl')} placeholder="https://inspection-a.example/" aria-label="工位地址" />
          </Field>
          <Field label="读取令牌（stations:read）" hint="以 vst_ 开头；只写进环境变量，不写进 JSON" error={issueOf('token')}>
            <Input value={draft.token} onChange={update('token')} placeholder="vst_..." aria-label="读取令牌" />
          </Field>
          <Field label="下发令牌（可选，stations:deliver）" hint="必须与读取令牌不同" error={issueOf('deliveryToken')}>
            <Input value={draft.deliveryToken} onChange={update('deliveryToken')} placeholder="vst_..." aria-label="下发令牌" />
          </Field>
          <Field label="控制令牌（可选，stations:control）" hint="用于总览远程启停；必须与其它令牌不同" error={issueOf('controlToken')}>
            <Input value={draft.controlToken} onChange={update('controlToken')} placeholder="vst_..." aria-label="控制令牌" />
          </Field>
          <Field label="期望配方 ID（可选）" hint="版本核验目标；留空表示不核验" error={issueOf('expectedJobId')}>
            <Input value={draft.expectedJobId} onChange={update('expectedJobId')} placeholder="front-cover" aria-label="期望配方 ID" />
          </Field>
          <Field label="期望版本（可选）" hint="填了配方 ID 就必须填大于 0 的版本" error={issueOf('expectedVersion')}>
            <Input value={draft.expectedVersion} onChange={update('expectedVersion')} placeholder="4" inputMode="numeric" aria-label="期望版本" />
          </Field>
          <Field label="流程指纹（可选）" hint="64 位十六进制；用于区分同名不同内容" error={issueOf('expectedWorkflowHash')}>
            <Input value={draft.expectedWorkflowHash} onChange={update('expectedWorkflowHash')} placeholder="64 位十六进制" aria-label="流程指纹" />
          </Field>
          {!ready && <div style={{ gridColumn: '1 / -1' }}>
            <Alert type="warning" showIcon title={`还有 ${issues.length} 处需要修正`}
              description={issues.map((issue) => issue.message).join('；')} />
          </div>}
        </div>

        <div style={{ display: 'grid', gap: 10 }}>
          <div>
            <Space style={{ width: '100%', justifyContent: 'space-between' }}>
              <strong style={{ fontSize: 13 }}>① 站点配置片段</strong>
              <Button size="small" disabled={!ready} onClick={() => void copy(json, '配置片段')}>复制 JSON</Button>
            </Space>
            <pre style={codeStyle} aria-label="站点配置片段">{ready ? json : '填写并修正后自动生成'}</pre>
            <span style={{ fontSize: 11, color: 'var(--vs-text-5)' }}>
              {startIndex > 0
                ? `粘到现有 Stations 数组末尾（下标 ${startIndex}）；已有工位时不要把整份文件替换成这一段。`
                : 'Stations 为空时可直接替换 ProductionFleet 段。'}
            </span>
          </div>
          <div>
            <Space style={{ width: '100%', justifyContent: 'space-between' }}>
              <strong style={{ fontSize: 13 }}>② 令牌环境变量（宿主运行账户）</strong>
              <Button size="small" disabled={!ready} onClick={() => void copy(env, '环境变量')}>复制变量</Button>
            </Space>
            <pre style={codeStyle} aria-label="令牌环境变量">{ready ? env : '填写并修正后自动生成'}</pre>
            <span style={{ fontSize: 11, color: 'var(--vs-text-5)' }}>
              变量名按列表下标绑定（当前 {startIndex}）。用 Windows 服务方式运行时，请设在服务所属账户的环境变量里。
            </span>
          </div>
          <Alert type="error" showIcon title="这段文本含令牌"
            description="向导只在当前页面生成文本，不会发送到后端；复制后请勿粘贴到工单、聊天记录或截图里。" />
        </div>
      </div>
    </Space>
  </Modal>;
}
