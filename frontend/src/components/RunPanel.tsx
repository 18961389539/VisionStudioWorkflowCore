import { useMemo, useState } from 'react';
import { Alert, Button, Empty, Segmented, Table, Tag, Typography } from 'antd';
import type { NodeRunReport, RunResult } from '../types';
import { localizeStatus } from '../i18n';
import { summarizeScalars } from '../summaryText';

type Props = {
  result?: RunResult;
  /** 画布节点 id → 显示名称，用于把运行结果对应回具体节点 */
  nodeLabels?: Record<string, string>;
  onSelectNode?: (nodeId: string) => void;
  /** 画布 payload 与提交时不一致：结果对应提交时的流程版本，提醒用户别把旧结果当成新参数的验证 */
  stale?: boolean;
  /** 嵌入底部标签页时隐藏自带面板标题（外层已有 Tab 名） */
  embedded?: boolean;
};

type FilterMode = 'all' | 'failed' | 'warmup';
type SortMode = 'sequence' | 'duration';

const INLINE_SCALARS = 3;

/** 摘要单元格：标量压成一行（规则见 summaryText），完整 JSON 放在可展开的详情里 */
function SummaryCell({ summary, typeKey }: { summary?: Record<string, unknown>; typeKey?: string }) {
  const [expanded, setExpanded] = useState(false);
  const entries = Object.entries(summary ?? {});
  // 与节点 footer 同一套“主结果字段”排序（measure.blob 先出面积，而不是 x/y）
  const line = summarizeScalars(summary, INLINE_SCALARS, undefined, typeKey) || '—';
  return (
    <div className="run-summary">
      <div className="run-summary-line" title={line}>{line}</div>
      {entries.length > 0 && (
        <button type="button" className="text-button" onClick={() => setExpanded((current) => !current)}>
          {expanded ? '收起详情' : `展开详情（${entries.length}）`}
        </button>
      )}
      {expanded && <pre className="debug-json run-summary-json">{JSON.stringify(summary ?? {}, null, 2)}</pre>}
    </div>
  );
}

export default function RunPanel({ result, nodeLabels = {}, onSelectNode, stale = false, embedded = false }: Props) {
  if (!result) {
    return (
      <div className="run-panel">
        {!embedded && <div className="panel-title">运行 / 调试结果</div>}
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="点击“运行”“运行节点”或“调试”来执行工作流" />
      </div>
    );
  }
  return <RunPanelContent result={result} nodeLabels={nodeLabels} onSelectNode={onSelectNode} stale={stale} embedded={embedded} />;
}

type ReportRow = NodeRunReport & { key: string };

function RunPanelContent({ result, nodeLabels = {}, onSelectNode, stale = false, embedded = false }: Props & { result: RunResult }) {
  const [filter, setFilter] = useState<FilterMode>('all');
  const [sort, setSort] = useState<SortMode>('sequence');

  const isPaused = result.debugState === 'Breakpoint' || result.debugState === 'Stopped';
  const decisions = result.controlFlowDecisions ?? [];
  const nameOf = (nodeId: string) => nodeLabels[nodeId] ?? nodeId;

  // 失败节点与最早执行的失败节点：运行完成后先回答“判定、耗时、哪个节点失败”，一键定位无需先筛再点
  const failedCount = useMemo(() => result.nodeReports.filter((row) => !row.success).length, [result]);
  const firstFailed = useMemo(() => result.nodeReports.reduce<NodeRunReport | undefined>((best, row) => {
    if (row.success) return best;
    if (!best || (row.executionSequence ?? Number.MAX_SAFE_INTEGER) < (best.executionSequence ?? Number.MAX_SAFE_INTEGER)) return row;
    return best;
  }, undefined), [result]);

  // 筛选 + 排序（耗时降序时以执行序号做稳定次键）
  const rows = useMemo<ReportRow[]>(() => {
    let list: ReportRow[] = result.nodeReports.map((report, index) => ({ ...report, key: `${report.nodeId}-${index}` }));
    if (filter === 'failed') list = list.filter((row) => !row.success);
    if (filter === 'warmup') list = list.filter((row) => row.phase === 'Warmup');
    return [...list].sort(sort === 'duration'
      ? (a, b) => b.durationMs - a.durationMs || (a.executionSequence ?? 0) - (b.executionSequence ?? 0)
      : (a, b) => (a.executionSequence ?? 0) - (b.executionSequence ?? 0));
  }, [filter, result, sort]);

  // 最慢节点（在当前筛选内计算，让筛选后的“最慢”始终可见）
  const slowest = useMemo<ReportRow | undefined>(() => {
    if (rows.length === 0) return undefined;
    return rows.reduce((current, row) => (row.durationMs > current.durationMs ? row : current), rows[0]);
  }, [rows]);

  // 执行时间线：基于既有 startOffsetMs/endOffsetMs；比例尺用全部报告（筛选不改变刻度，便于对比）
  const timeline = useMemo(() => {
    const total = result.nodeReports.reduce((max, report) => Math.max(max, report.endOffsetMs ?? 0), 0);
    if (total <= 0) return undefined;
    const bars = rows
      .filter((row) => (row.endOffsetMs ?? 0) > 0)
      .map((row) => {
        const start = Math.max(0, row.startOffsetMs ?? 0);
        const end = Math.max(start, row.endOffsetMs ?? start);
        return { key: row.key, nodeId: row.nodeId, start, span: Math.max(end - start, 0), phase: row.phase, success: row.success, durationMs: row.durationMs };
      });
    return bars.length > 0 ? { total, bars } : undefined;
  }, [result, rows]);

  return (
    <div className="run-panel">
      {!embedded && <div className="panel-title">运行 / 调试结果</div>}
      <Alert
        type={!result.success || result.qualityDisposition === 'NG' ? 'error' : (isPaused ? 'info' : 'success')}
        showIcon
        message={
          <span>
            {result.success
              ? `${localizeStatus(result.debugState ?? 'Complete')} · ${result.qualityDisposition ? localizeStatus(result.qualityDisposition) : '执行成功'} · ${result.totalDurationMs.toFixed(2)} 毫秒`
              : '执行失败'}
            {stale && (
              <Tag color="gold" style={{ marginInlineStart: 8 }} title="画布在本次运行提交后已被修改：下方状态与摘要对应提交时的流程版本，不代表当前参数的运行结果">
                流程已修改 · 结果对应提交时的版本
              </Tag>
            )}
          </span>
        }
        description={result.error ?? result.haltReason ?? (result.qualityDisposition ? `检测判定：${localizeStatus(result.qualityDisposition)}` : undefined)}
        action={firstFailed && onSelectNode ? (
          <Button
            size="small"
            danger
            onClick={() => onSelectNode(firstFailed.nodeId)}
            title={failedCount > 1 ? `共 ${failedCount} 个失败节点，定位最早执行的：${nameOf(firstFailed.nodeId)}` : undefined}
          >
            定位首个失败节点
          </Button>
        ) : undefined}
      />
      {decisions.length > 0 && (
        <div className="control-decisions">
          {decisions.map((decision) => (
            <Tag key={decision.nodeId}>
              {nameOf(decision.nodeId)}: {decision.selectedBranch ?? decision.activeBranches.join(' + ')}
            </Tag>
          ))}
        </div>
      )}

      <div className="run-toolbar">
        <Segmented
          size="small"
          value={filter}
          onChange={(value) => setFilter(value as FilterMode)}
          options={[
            { label: '全部', value: 'all' },
            { label: '仅失败', value: 'failed' },
            { label: '仅预热', value: 'warmup' }
          ]}
        />
        <span className="run-toolbar-label">排序</span>
        <Segmented
          size="small"
          value={sort}
          onChange={(value) => setSort(value as SortMode)}
          options={[
            { label: '执行顺序', value: 'sequence' },
            { label: '耗时降序', value: 'duration' }
          ]}
        />
        {slowest && (
          <Tag color="gold" title="当前筛选内耗时最长的节点">
            最慢 · {nameOf(slowest.nodeId)} · {slowest.durationMs.toFixed(2)} 毫秒
          </Tag>
        )}
        <span className="run-toolbar-spacer" />
        <Typography.Text type="secondary" className="run-toolbar-hint">
          共 {result.nodeReports.length} 条报告，当前显示 {rows.length} 条
        </Typography.Text>
      </div>

      {timeline && (
        <div className="run-result-timeline">
          <div className="run-result-timeline-caption">
            执行时间线（刻度 0 – {timeline.total.toFixed(1)} 毫秒 · 金色描边为最慢节点 · 条带重叠表示并行执行；点击定位节点）
          </div>
          {timeline.bars.map((bar) => (
            <div
              key={bar.key}
              className="timeline-row"
              onClick={() => onSelectNode?.(bar.nodeId)}
              title={`${nameOf(bar.nodeId)} · 开始 ${bar.start.toFixed(1)} 毫秒 · 耗时 ${bar.durationMs.toFixed(2)} 毫秒（点击定位到画布）`}
            >
              <span className={`timeline-label${bar.key === slowest?.key ? ' slowest' : ''}`}>
                {nameOf(bar.nodeId)}{bar.key === slowest?.key ? '（最慢）' : ''}
              </span>
              <div className="timeline-track">
                <div
                  className={`timeline-bar ${bar.success ? (bar.phase === 'Warmup' ? 'bar-warmup' : 'bar-ok') : 'bar-error'}${bar.key === slowest?.key ? ' bar-slowest' : ''}`}
                  style={{ left: `${(bar.start / timeline.total) * 100}%`, width: `${Math.max((bar.span / timeline.total) * 100, 0.4)}%` }}
                />
              </div>
              <span className="timeline-duration">{bar.durationMs.toFixed(2)} 毫秒</span>
            </div>
          ))}
        </div>
      )}

      {rows.length === 0 ? (
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="当前筛选没有匹配的节点" />
      ) : (
        <Table
          size="small"
          pagination={false}
          rowKey="key"
          dataSource={rows}
          rowClassName={(record) => `${record.success ? '' : 'run-row-failed'}${record.key === slowest?.key ? ' run-row-slowest' : ''}`}
          onRow={(record) => ({
            onClick: () => onSelectNode?.(record.nodeId),
            title: `${nameOf(record.nodeId)} · ${record.nodeId}（点击定位到画布）`
          })}
          columns={[
            { title: '阶段', dataIndex: 'phase', width: 76, render: (v: string) => <Tag>{v === 'Warmup' ? '预热' : '运行'}</Tag> },
            {
              title: '节点',
              dataIndex: 'nodeId',
              width: 200,
              render: (nodeId: string, record) => (
                <div className="run-node-cell">
                  <span className="run-node-name">{nameOf(nodeId)}</span>
                  <Typography.Text type="secondary" className="run-node-type">{record.nodeType}</Typography.Text>
                </div>
              )
            },
            {
              title: '状态',
              dataIndex: 'success',
              width: 78,
              render: (success: boolean, record) => (
                <Tag color={success ? (record.phase === 'Warmup' ? 'blue' : 'green') : 'red'} title={record.error ?? undefined}>
                  {success ? '成功' : '失败'}
                </Tag>
              )
            },
            { title: '开始', dataIndex: 'startOffsetMs', width: 88, render: (v?: number) => (v === undefined ? '—' : `${v.toFixed(1)} 毫秒`) },
            {
              title: '耗时',
              dataIndex: 'durationMs',
              width: 128,
              render: (v: number, record) => (
                <span>
                  {v.toFixed(2)} 毫秒
                  {record.key === slowest?.key && <Tag color="gold" style={{ marginLeft: 6 }}>最慢</Tag>}
                </span>
              )
            },
            {
              title: '摘要',
              dataIndex: 'summary',
              render: (summary: Record<string, unknown>, record) => <SummaryCell summary={summary} typeKey={record.nodeType} />
            }
          ]}
        />
      )}
    </div>
  );
}
