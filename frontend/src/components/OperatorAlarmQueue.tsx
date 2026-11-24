import { useState } from 'react';
import { Alert, Button, Tag } from 'antd';
import type { AlarmRecord } from '../types';
import { apiFetch } from '../apiClient';
import { responseErrorMessage } from '../apiErrors';
import { alarms } from '../endpoints';
import { useProductionPoll } from '../useProductionPoll';

const queueSize = 3;
const severity: Record<string, string> = { Critical: '严重', Error: '错误', Warning: '警告', Info: '信息' };

async function readPendingAlarms(signal: AbortSignal) {
  const response = await apiFetch(alarms.page(0, queueSize, 'pending'), { signal });
  if (!response.ok) throw new Error(await responseErrorMessage(response, '待确认告警读取失败'));
  const data: unknown = await response.json();
  if (!Array.isArray(data) || !data.every((record) => record && typeof record.id === 'string'
    && typeof record.message === 'string' && typeof record.severity === 'string' && typeof record.code === 'string'
    && typeof record.raisedAt === 'string' && record.active === true && record.acknowledged === false)) {
    throw new Error('待确认告警响应无效');
  }
  return (data as AlarmRecord[]).slice(0, queueSize);
}

export default function OperatorAlarmQueue({ onAcknowledge, busy }: {
  onAcknowledge: (alarmId: string) => Promise<{ ok: boolean }>;
  busy: ReadonlySet<string>;
}) {
  const { data, error, refreshing, refresh } = useProductionPoll(readPendingAlarms);
  const [hidden, setHidden] = useState<string[]>([]);
  const visible = data?.filter((alarm) => (!refreshing && !error) || !hidden.includes(alarm.id)) ?? [];
  if (visible.length === 0 && !error) return null;

  return <section className="operator-alarm-queue" aria-label="最近待确认告警">
    <h3>最近待确认告警</h3>
    {error && <Alert type="warning" showIcon title="待确认告警尚未核实" description={error}
      action={<Button size="small" loading={refreshing} onClick={refresh}>重试读取</Button>} />}
    {visible.length > 0 && <ul>
      {visible.map((alarm) => <li key={alarm.id}>
        <div>
          <div className="operator-alarm-message">
            <Tag color={alarm.severity === 'Critical' || alarm.severity === 'Error' ? 'red' : 'orange'}>{severity[alarm.severity] ?? alarm.severity}</Tag>
            <strong>{alarm.message}</strong>
          </div>
          <span className="station-secondary">{alarm.code} · {new Date(alarm.lastRaisedAt ?? alarm.raisedAt).toLocaleString('zh-CN')}</span>
        </div>
        <Button aria-label={'确认告警 ' + alarm.message} disabled={Boolean(error) || refreshing}
          loading={busy.has('ack:' + alarm.id)} onClick={() => {
            void onAcknowledge(alarm.id).then((result) => {
              if (result.ok) setHidden((previous) => [...previous, alarm.id].slice(-queueSize));
              refresh();
            });
          }}>确认告警</Button>
      </li>)}
    </ul>}
  </section>;
}
