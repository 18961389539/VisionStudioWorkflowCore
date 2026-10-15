import type { ReactNode } from 'react';

const states: Record<string, { label: string; icon: ReactNode }> = {
  idle: { label: '空闲', icon: <circle cx="12" cy="12" r="7" /> },
  pending: { label: '等待', icon: <><circle cx="12" cy="12" r="8" /><path d="M12 7v5l3 2" /></> },
  running: { label: '执行中', icon: <><path d="M20 12a8 8 0 1 1-5-7.4" /><path d="m16 3 1 4 4-1" /></> },
  ok: { label: '成功', icon: <><circle cx="12" cy="12" r="9" /><path d="m7 12 3 3 7-7" /></> },
  error: { label: '失败', icon: <><circle cx="12" cy="12" r="9" /><path d="m9 9 6 6m0-6-6 6" /></> },
  warmup: { label: '预热', icon: <><path d="m13 3-7 11h5l-1 7 8-12h-5z" /></> },
  breakpoint: { label: '暂停', icon: <><circle cx="12" cy="12" r="9" /><path d="M9 8v8m6-8v8" /></> },
  skipped: { label: '未走到', icon: <><path d="m5 5 10 7-10 7zM19 5v14" /></> },
  stale: { label: '待运行', icon: <><path d="M19 8a8 8 0 1 0 1 7M20 3v6h-6" /></> }
};

export function executionStatusLabel(status: string) {
  return (states[status] ?? states.idle).label;
}

/** 画布与报告共用图形及文字语义，状态无需依赖颜色辨认。 */
export default function ExecutionStatus({ status, label, title }: { status: string; label?: string; title?: string }) {
  const resolved = states[status] ? status : 'idle';
  return <span className={`execution-status execution-status-${resolved}`} title={title}>
    <svg className="execution-status-icon" viewBox="0 0 24 24" width="14" height="14" fill="none"
      stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{states[resolved].icon}</svg>
    <span>{label ?? states[resolved].label}</span>
  </span>;
}
