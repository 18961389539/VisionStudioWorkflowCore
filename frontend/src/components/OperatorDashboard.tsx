import { lazy, Suspense, useEffect, useState } from 'react';
import { Alert, Button } from 'antd';
import type { ContractDto, ProductionRuntimeStatus } from '../types';
import { apiFetch } from '../apiClient';
import { responseErrorMessage } from '../apiErrors';
import { production } from '../endpoints';
import { localizeStatus } from '../i18n';
import { productionDemoEnabled } from '../productionDemoData';
const OperatorInspectionPreview = lazy(() => import('./OperatorInspectionPreview'));

export type Layout = ContractDto<'OperatorLayout'>;
export type Widget = ContractDto<'OperatorWidget'>;
// 与后端 OperatorLayout.Default 保持一致（后端才是默认值的真相源，这里只做读取失败时的兜底）
const defaultLayout: Layout = { title: null, widgets: ['Result', 'Counts', 'Timing', 'Alarms'] };
const demoLayout: Layout = { title: '操作员工作台 · 仿真', widgets: ['Result', 'Counts', 'Timing', 'Alarms', 'Preview'] };
export const widgetLabels: Record<Widget, string> = {
  Result: '最近判定', Counts: '产量与良率', Timing: '检测节拍', Alarms: '活动告警', Preview: '最近检验留图',
};

export function isLayout(value: unknown): value is Layout {
  if (typeof value !== 'object' || value === null) return false;
  const layout = value as Partial<Layout>;
  return (layout.title === null || typeof layout.title === 'string')
    && Array.isArray(layout.widgets) && layout.widgets.length > 0
    && layout.widgets.every((widget) => Object.hasOwn(widgetLabels, widget))
    && new Set(layout.widgets).size === layout.widgets.length;
}

export function useOperatorLayout(jobId?: string) {
  const [snapshot, setSnapshot] = useState<{ layout: Layout; revision: string }>();
  const [error, setError] = useState<string>();
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!jobId) return;
    const controller = new AbortController();
    const timeout = window.setTimeout(() => {
      setError('操作员布局读取超时');
      controller.abort();
    }, 5_000);
    setSnapshot(undefined);
    setError(undefined);
    void (async () => {
      try {
        const response = await apiFetch(production.operatorLayout(jobId), { signal: controller.signal });
        if (!response.ok) throw new Error(await responseErrorMessage(response, '操作员布局读取失败'));
        const layout: unknown = await response.json();
        const revision = response.headers.get('ETag');
        if (!revision || !isLayout(layout)) throw new Error('操作员布局响应无效');
        if (!controller.signal.aborted) setSnapshot({ layout, revision });
      } catch (reason) {
        if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : '操作员布局读取失败');
      } finally { window.clearTimeout(timeout); }
    })();
    return () => { window.clearTimeout(timeout); controller.abort(); };
  }, [jobId, attempt]);
  return { snapshot, error, reload: () => setAttempt((value) => value + 1) };
}

type DashboardProps = {
  jobId?: string;
  stationId?: string;
  status?: ProductionRuntimeStatus;
  statusFresh: boolean;
  alarmSummary?: ContractDto<'AlarmSummary'>;
  alarmFresh: boolean;
  onEdit?: () => void;
  /** 车间大屏形态：布局与字号由 `.is-kiosk` 样式接管，组件本身只负责打标。 */
  kiosk?: boolean;
  /** 最后一次成功读取生产状态的时间；过期时用来回答"这些数字是几点钟的"。 */
  lastUpdatedAt?: Date;
};

export function OperatorDashboard({ jobId, stationId, status, statusFresh, alarmSummary, alarmFresh, onEdit, kiosk = false, lastUpdatedAt }: DashboardProps) {
  const { snapshot, error, reload } = useOperatorLayout(jobId);
  const simulation = productionDemoEnabled && !jobId;
  const layout = snapshot?.layout ?? (simulation ? demoLayout : defaultLayout);
  // status 是最后一次成功读取的值（失败不会清空它）。过期时保留数字并明示"这是几点钟的数据"，
  // 而不是把它们抹成 —：车间里一条破折号会被读成"今天零产量"，比显示旧值危险得多。
  const live = status;
  const stale = Boolean(status) && !statusFresh;
  const counts = (live?.okCount ?? 0) + (live?.ngCount ?? 0);
  const lastDisposition = localizeStatus(live?.lastDisposition);
  const previousSession = live && !live.productionLocked && live.cycleCount > 0;
  // 告警卡：数量之外还要回答"是哪一条、多严重"。统计未更新时不给严重级颜色，
  // 否则上一轮的红色边框会继续冒充"当前有严重告警"。
  const alarmActive = alarmFresh ? alarmSummary?.activeCount ?? 0 : undefined;
  const alarmCritical = alarmFresh ? alarmSummary?.criticalCount ?? 0 : 0;
  const alarmPending = alarmFresh ? alarmSummary?.pendingCount ?? 0 : 0;
  const alarmSeverity = alarmActive === undefined ? undefined
    : alarmCritical > 0 ? 'critical' : alarmActive > 0 ? 'warning' : undefined;
  return <section className="operator-dashboard" aria-label="操作员工作台" data-kiosk={kiosk || undefined}
    data-stale={stale || undefined}>
    <div className="workstation-heading">
      <h2>{layout.title ?? '操作员工作台'}</h2>
      {onEdit && <Button onClick={onEdit}>配置操作员界面</Button>}
    </div>
    {stale && <Alert type="error" showIcon
      title={<span style={kiosk ? { fontSize: 'clamp(16px, 2vmin, 34px)' } : undefined}>数据已过期</span>}
      description={<span style={kiosk ? { fontSize: 'clamp(14px, 1.7vmin, 28px)' } : undefined}>
        数字为 {lastUpdatedAt ? lastUpdatedAt.toLocaleTimeString('zh-CN') : '未知时间'} 的最后一次读取值，不是实时值
      </span>} />}
    {/* kiosk 是车间大屏：布局读取失败时静默回落默认布局，不把技术性错误挂到操作员面前。 */}
    {error && !kiosk && <Alert type="warning" showIcon title="当前使用默认布局" description={error}
      action={<Button size="small" onClick={reload}>重新加载</Button>} />}
    {previousSession && <div className="station-secondary">判定、产量和节拍来自上次生产会话。</div>}
    {/* 过期时整体降饱和：数字仍是"最后一次读数"，不是当前值 */}
    <div className="operator-widgets" style={stale ? { opacity: .6 } : undefined}>
      {layout.widgets.map((widget) => <section className={widget === 'Preview' ? 'operator-widget operator-preview-widget' : 'operator-widget'} key={widget}
        data-widget={widget} data-severity={widget === 'Alarms' ? alarmSeverity : undefined} aria-label={widgetLabels[widget]}>
        <h3>{widgetLabels[widget]}</h3>
        {widget === 'Result' && <strong data-result={live?.lastDisposition}>{lastDisposition ?? '—'}</strong>}
        {widget === 'Counts' && <>
          <strong>{live ? live.cycleCount.toLocaleString('zh-CN') : '—'} <small>周期</small></strong>
          <span>合格 {live?.okCount ?? '—'}　不合格 {live?.ngCount ?? '—'}</span>
          <span title="合格数 ÷（合格数 + 不合格数）">判定良率 {live && counts > 0 ? new Intl.NumberFormat('zh-CN', { style: 'percent', maximumFractionDigits: 1 }).format(live.okCount / counts) : '—'}</span>
        </>}
        {widget === 'Timing' && <>
          <strong>{live?.lastCycleAt ? live.lastDurationMs.toFixed(1) : '—'} <small>毫秒</small></strong>
          <span>{live?.lastCycleAt ? new Date(live.lastCycleAt).toLocaleTimeString('zh-CN') : '暂无已完成周期'}</span>
        </>}
        {widget === 'Preview' && <Suspense fallback={<span role="status">正在加载检验留图…</span>}>
          <OperatorInspectionPreview status={live} simulation={simulation} stationId={stationId} />
        </Suspense>}
        {widget === 'Alarms' && <>
          <strong>{alarmActive ?? '—'} <small>条活动告警</small></strong>
          <span>{alarmActive === undefined ? '告警统计未确认'
            : alarmActive === 0 ? '无活动告警'
              : `待确认 ${alarmPending}${alarmCritical > 0 ? ` · 严重 ${alarmCritical}` : ''}`}</span>
          {/* 车间里“3 条活动告警”没有意义：要知道是哪一条才谈得上处置 */}
          {alarmSummary?.latestActiveMessage && <span className="operator-alarm-latest" title={alarmSummary.latestActiveMessage}>
            {alarmSummary.latestActiveMessage}
          </span>}
        </>}
      </section>)}
    </div>
  </section>;
}
