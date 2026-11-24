/**
 * SPC 看板（2026-11-23）。
 *
 * 数据源是结果记录表（`spc/datasets` → `spc/series`），后端完成
 * 控制限、Western Electric 规则与过程能力计算；这里只负责选数据、呈现与刷新。
 * 同一个组件既作为编辑器抽屉面板，也作为 `?view=spc` 独立页面（embedded）。
 */
import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Drawer, Empty, InputNumber, Select, Space, Spin, Statistic, Table, Tag, Typography } from 'antd';
import type { SpcAnalysis, SpcDatasetSummary } from '../types';
import { apiJson, isAbortError } from '../apiClient';
import { spc } from '../endpoints';
import { useI18n } from '../i18n/provider';
import type { MessageKey } from '../i18n';
import SpcChart from './SpcChart';

type Props = { open: boolean; onClose: () => void; embedded?: boolean };

type ChartType = 'auto' | 'individuals' | 'xbarR' | 'xbarS';
type Grouping = 'sequential' | 'record';
type WindowKey = '1d' | '7d' | '30d' | 'all';

const WINDOW_HOURS: Record<WindowKey, number | null> = { '1d': 24, '7d': 24 * 7, '30d': 24 * 30, all: null };
const RULE_KEYS: Record<string, MessageKey> = {
  rule1: 'spc.rule.rule1',
  rule2: 'spc.rule.rule2',
  rule3: 'spc.rule.rule3',
  rule4: 'spc.rule.rule4'
};
const RULE_COLORS: Record<string, string> = { rule1: 'red', rule2: 'orange', rule3: 'gold', rule4: 'blue' };

function formatNumber(value: number | null | undefined, digits = 3): string {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '—';
  if (Number.isInteger(value)) return String(value);
  return value.toFixed(digits);
}

export default function SpcDashboard({ open, onClose, embedded = false }: Props) {
  const { t, dateLocale } = useI18n();
  const [datasets, setDatasets] = useState<SpcDatasetSummary[]>([]);
  const [datasetsLoading, setDatasetsLoading] = useState(false);
  const [datasetsError, setDatasetsError] = useState<string>();
  const [dataset, setDataset] = useState<string>();
  const [chartType, setChartType] = useState<ChartType>('auto');
  const [subgroupSize, setSubgroupSize] = useState(5);
  const [grouping, setGrouping] = useState<Grouping>('sequential');
  const [windowKey, setWindowKey] = useState<WindowKey>('7d');
  const [lsl, setLsl] = useState<number | null>(null);
  const [usl, setUsl] = useState<number | null>(null);
  const [analysis, setAnalysis] = useState<SpcAnalysis>();
  const [analysisLoading, setAnalysisLoading] = useState(false);
  const [analysisError, setAnalysisError] = useState<string>();
  const [refreshNonce, setRefreshNonce] = useState(0);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setDatasetsLoading(true);
    setDatasetsError(undefined);
    apiJson<SpcDatasetSummary[]>(spc.datasets, { signal: controller.signal, errorLabel: t('spc.error.datasets') })
      .then((list) => {
        if (controller.signal.aborted) return;
        setDatasets(list);
        setDataset((current) => current && list.some((item) => item.dataset === current) ? current : list[0]?.dataset);
      })
      .catch((error: unknown) => {
        if (!isAbortError(error)) setDatasetsError(error instanceof Error ? error.message : t('spc.error.datasets'));
      })
      .finally(() => { if (!controller.signal.aborted) setDatasetsLoading(false); });
    return () => controller.abort();
  }, [open, refreshNonce, t]);

  useEffect(() => {
    if (!open || !dataset) { setAnalysis(undefined); setAnalysisError(undefined); return; }
    const controller = new AbortController();
    const query = new URLSearchParams({ dataset, grouping, subgroupSize: String(subgroupSize), limit: '5000' });
    if (chartType !== 'auto') query.set('chartType', chartType);
    const hours = WINDOW_HOURS[windowKey];
    if (hours !== null) query.set('from', new Date(Date.now() - hours * 3_600_000).toISOString());
    if (lsl !== null) query.set('lsl', String(lsl));
    if (usl !== null) query.set('usl', String(usl));
    setAnalysisLoading(true);
    setAnalysisError(undefined);
    apiJson<SpcAnalysis>(spc.series(query.toString()), { signal: controller.signal, errorLabel: t('spc.error.analysis') })
      .then((result) => { if (!controller.signal.aborted) setAnalysis(result); })
      .catch((error: unknown) => {
        if (!isAbortError(error)) setAnalysisError(error instanceof Error ? error.message : t('spc.error.analysis'));
      })
      .finally(() => { if (!controller.signal.aborted) setAnalysisLoading(false); });
    return () => controller.abort();
  }, [open, dataset, chartType, subgroupSize, grouping, windowKey, lsl, usl, refreshNonce, t]);

  const changeChartType = (value: ChartType) => {
    setChartType(value);
    if (value === 'individuals') setSubgroupSize(1);
    else if ((value === 'xbarR' || value === 'xbarS') && subgroupSize < 2) setSubgroupSize(5);
  };

  const violations = useMemo(() => {
    if (!analysis) return [];
    const charts = analysis.secondaryChart ? [analysis.primaryChart, analysis.secondaryChart] : [analysis.primaryChart];
    return charts.flatMap((chart) => chart.violations.map((violation) => ({
      key: `${chart.kind}-${violation.rule}-${violation.startIndex}-${violation.endIndex}`,
      chart: chart.label,
      rule: violation.rule,
      startIndex: violation.startIndex,
      endIndex: violation.endIndex,
      description: violation.description
    })));
  }, [analysis]);

  const datasetOptions = datasets.map((item) => ({
    value: item.dataset,
    label: `${item.dataset} · ${t('spc.dataset.records', { count: item.recordCount })}`
  }));

  const refresh = () => setRefreshNonce((value) => value + 1);

  const controls = <div className="spc-controls">
    <label className="spc-control">
      <span>{t('spc.dataset')}</span>
      <Select showSearch style={{ width: 230 }} value={dataset} options={datasetOptions} loading={datasetsLoading}
        placeholder={t('spc.dataset')} onChange={setDataset}
        filterOption={(input, option) => String(option?.value ?? '').toLowerCase().includes(input.toLowerCase())} />
    </label>
    <label className="spc-control">
      <span>{t('spc.chartType')}</span>
      <Select<ChartType> style={{ width: 190 }} value={chartType} onChange={changeChartType} options={[
        { value: 'auto', label: t('spc.chartType.auto') },
        { value: 'individuals', label: t('spc.chartType.individuals') },
        { value: 'xbarR', label: t('spc.chartType.xbarR') },
        { value: 'xbarS', label: t('spc.chartType.xbarS') }
      ]} />
    </label>
    <label className="spc-control">
      <span>{t('spc.subgroupSize')}</span>
      <InputNumber style={{ width: 110 }} min={1} max={25} precision={0} value={subgroupSize}
        disabled={chartType === 'individuals'} onChange={(value) => setSubgroupSize(typeof value === 'number' ? value : 1)} />
    </label>
    <label className="spc-control">
      <span>{t('spc.grouping')}</span>
      <Select<Grouping> style={{ width: 190 }} value={grouping} onChange={setGrouping} options={[
        { value: 'sequential', label: t('spc.grouping.sequential') },
        { value: 'record', label: t('spc.grouping.record') }
      ]} />
    </label>
    <label className="spc-control">
      <span>{t('spc.window')}</span>
      <Select<WindowKey> style={{ width: 130 }} value={windowKey} onChange={setWindowKey} options={[
        { value: '1d', label: t('spc.window.1d') },
        { value: '7d', label: t('spc.window.7d') },
        { value: '30d', label: t('spc.window.30d') },
        { value: 'all', label: t('spc.window.all') }
      ]} />
    </label>
    <label className="spc-control">
      <span>{t('spc.lsl')}</span>
      <InputNumber style={{ width: 140 }} value={lsl} placeholder="LSL" onChange={(value) => setLsl(typeof value === 'number' ? value : null)} />
    </label>
    <label className="spc-control">
      <span>{t('spc.usl')}</span>
      <InputNumber style={{ width: 140 }} value={usl} placeholder="USL" onChange={(value) => setUsl(typeof value === 'number' ? value : null)} />
    </label>
    <Button onClick={refresh} loading={datasetsLoading || analysisLoading}>{t('spc.refresh')}</Button>
  </div>;

  const notes = analysis?.warnings.length
    ? <Alert type="warning" showIcon title={t('spc.warnings')}
        description={<ul className="spc-warning-list">{analysis.warnings.map((warning) => <li key={warning}>{warning}</li>)}</ul>} />
    : null;

  const result = analysis && <>
    <div className="spc-stat-grid">
      <Statistic title={t('spc.stat.samples')} value={analysis.samplesUsed} />
      <Statistic title={t('spc.stat.subgroups')} value={analysis.subgroupCount} />
      <Statistic title={t('spc.stat.mean')} value={formatNumber(analysis.mean)} />
      <Statistic title={t('spc.stat.sigmaWithin')} value={formatNumber(analysis.capability?.withinSigma)} />
      <Statistic title={t('spc.stat.cp')} value={formatNumber(analysis.capability?.cp)} />
      <Statistic title={t('spc.stat.cpk')} value={formatNumber(analysis.capability?.cpk)} />
      <Statistic title={t('spc.stat.violations')} value={violations.length} />
    </div>
    {analysis.truncated && <Alert type="info" showIcon title={t('spc.truncated')} />}
    <div className="spc-chart-grid">
      <section className="spc-chart-card">
        <Typography.Title level={5} style={{ margin: 0 }}>{t('spc.chart.primary')} · {analysis.primaryChart.label}</Typography.Title>
        <SpcChart chart={analysis.primaryChart} label={analysis.primaryChart.label} locale={dateLocale} emptyText={t('spc.empty')}
          specLimits={{ lsl: analysis.lowerSpecificationLimit, usl: analysis.upperSpecificationLimit }} />
        <div className="spc-legend">
          <span><i className="spc-legend-swatch swatch-center" />{t('spc.legend.center')}</span>
          <span><i className="spc-legend-swatch swatch-limit" />UCL / LCL</span>
          {(analysis.lowerSpecificationLimit !== null || analysis.upperSpecificationLimit !== null) &&
            <span><i className="spc-legend-swatch swatch-spec" />{t('spc.legend.spec')}</span>}
          <span><i className="spc-legend-swatch swatch-violation" />{t('spc.legend.violation')}</span>
          <span><i className="spc-legend-swatch swatch-beyond" />{t('spc.legend.outOfLimits')}</span>
        </div>
        {analysis.primaryChart.points.length > 0 && analysis.primaryChart.upperControlLimit === null &&
          <Typography.Text type="secondary">{t('spc.chart.noLimits')}</Typography.Text>}
      </section>
      {analysis.secondaryChart && <section className="spc-chart-card">
        <Typography.Title level={5} style={{ margin: 0 }}>{t('spc.chart.secondary')} · {analysis.secondaryChart.label}</Typography.Title>
        <SpcChart chart={analysis.secondaryChart} label={analysis.secondaryChart.label} locale={dateLocale} emptyText={t('spc.empty')} />
        <div className="spc-legend">
          <span><i className="spc-legend-swatch swatch-center" />{t('spc.legend.center')}</span>
          <span><i className="spc-legend-swatch swatch-limit" />UCL / LCL</span>
          <span><i className="spc-legend-swatch swatch-violation" />{t('spc.legend.violation')}</span>
        </div>
      </section>}
    </div>
    <div>
      <Typography.Title level={5} style={{ margin: '4px 0 8px' }}>{t('spc.violations.title')}</Typography.Title>
      {violations.length === 0
        ? <Typography.Text type="secondary">{t('spc.violations.none')}</Typography.Text>
        : <Table size="small" rowKey="key" dataSource={violations} pagination={false} columns={[
            { title: t('spc.violations.chart'), dataIndex: 'chart', width: 170 },
            { title: t('spc.violations.rule'), dataIndex: 'rule', width: 230, render: (rule: string) =>
              <Tag color={RULE_COLORS[rule] ?? 'default'}>{RULE_KEYS[rule] ? t(RULE_KEYS[rule]) : rule}</Tag> },
            { title: t('spc.violations.range'), width: 120, render: (_: unknown, row: { startIndex: number; endIndex: number }) =>
              `#${row.startIndex + 1} – #${row.endIndex + 1}` },
            { title: t('spc.violations.description'), dataIndex: 'description' }
          ]} />}
    </div>
    {(analysis.capability || analysis.lowerSpecificationLimit !== null || analysis.upperSpecificationLimit !== null) &&
      <Typography.Text type="secondary">{t('spc.capability.note')}</Typography.Text>}
    <Typography.Text type="secondary">
      {t('spc.quality', {
        read: analysis.recordsRead, used: analysis.recordsUsed, ignored: analysis.recordsIgnored,
        dropped: analysis.samplesDropped, nonFinite: analysis.nonFiniteSamples
      })}
    </Typography.Text>
  </>;

  const body = <Space direction="vertical" size={12} style={{ width: '100%' }}>
    {datasetsError && <Alert type="error" showIcon title={t('spc.error.datasets')} description={datasetsError}
      action={<Button size="small" onClick={refresh}>{t('spc.refresh')}</Button>} />}
    {datasetOptions.length === 0 && !datasetsLoading && !datasetsError && <Empty description={t('spc.dataset.empty')} />}
    {datasetOptions.length > 0 && controls}
    {analysisError && <Alert type="error" showIcon title={t('spc.error.analysis')} description={analysisError} />}
    {analysisLoading && !analysis && <div className="spc-loading"><Spin size="small" /> {t('spc.loading')}</div>}
    {notes}
    {analysis && (analysis.primaryChart.points.length === 0 ? <Empty description={t('spc.empty')} /> : result)}
    {!analysis && !analysisLoading && !analysisError && datasetOptions.length > 0 && <Empty description={t('spc.empty')} />}
  </Space>;

  if (embedded) return <section className="spc-page">
    <div className="spc-page-header">
      <div>
        <Typography.Title level={4} style={{ margin: 0 }}>{t('spc.title')}</Typography.Title>
        <div className="spc-page-subtitle">{t('spc.subtitle')}</div>
      </div>
    </div>
    {body}
  </section>;

  return <Drawer open={open} onClose={onClose} width="min(1280px, 96vw)" title={t('spc.title')} destroyOnHidden>
    <div className="spc-page-subtitle" style={{ marginBottom: 12 }}>{t('spc.subtitle')}</div>
    {body}
  </Drawer>;
}