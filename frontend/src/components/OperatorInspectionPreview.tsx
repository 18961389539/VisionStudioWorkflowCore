import { useCallback, useState } from 'react';
import { Radio } from 'antd';
import type { ContractDto, ProductionRuntimeStatus } from '../types';
import { apiFetch } from '../apiClient';
import { production, traces } from '../endpoints';
import { localizeStatus } from '../i18n';
import { useProductionPoll } from '../useProductionPoll';
import { demoInspectionPreviews } from '../productionDemoData';

type Preview = ContractDto<'ProductionOperatorPreview'>;
type View = 'latest' | 'ng';

export default function OperatorInspectionPreview({ status, simulation = false, stationId }: {
  status?: ProductionRuntimeStatus;
  simulation?: boolean;
  stationId?: string;
}) {
  const [view, setView] = useState<View>('latest');
  const [selectedDemo, setSelectedDemo] = useState(0);
  if (simulation) {
    const previews = demoInspectionPreviews();
    const current = previews[selectedDemo]!;
    return <div className="operator-demo-inspection">
      <div className="operator-demo-toolbar">
        <span>开发仿真 · 3 帧</span>
        <strong data-result={current.disposition}>{current.disposition}</strong>
      </div>
      <div className="operator-demo-preview-stage">
        <div className="operator-demo-main-frame">
          <img className="operator-inspection-image" src={current.imageUrl} alt={current.label} />
          <div className="operator-demo-frame-meta">
            <strong>{current.label}</strong>
            <span>{current.disposition} · {current.durationMs.toFixed(1)} ms</span>
          </div>
        </div>
        <div className="operator-demo-thumbnails" role="group" aria-label="仿真检验帧">
          {previews.map((preview, index) => <button key={preview.id} type="button"
            className={index === selectedDemo ? 'selected' : ''} aria-pressed={index === selectedDemo}
            aria-label={`${preview.label} ${preview.disposition}`} onClick={() => setSelectedDemo(index)}>
            <img src={preview.imageUrl} alt="" />
            <span>{index + 1} · {preview.disposition}</span>
          </button>)}
        </div>
      </div>
    </div>;
  }
  const version = status?.productionLocked ? status.lockedJobVersion : undefined;
  if (version == null || !status?.startedAt) return <span>生产会话启动后显示本次检验留图。</span>;
  const key = [status.startedAt, status.jobId, version, view].join('/');
  return <>
    <Radio.Group aria-label="检验留图筛选" value={view} onChange={(event) => setView(event.target.value)} size="small">
      <Radio.Button value="latest">最近留图</Radio.Button>
      <Radio.Button value="ng">最近不合格</Radio.Button>
    </Radio.Group>
    <InspectionImage key={key} version={version} sessionStart={status.startedAt} ngOnly={view === 'ng'} stationId={stationId} />
  </>;
}

function InspectionImage({ version, sessionStart, ngOnly, stationId }: { version: number; sessionStart: string; ngOnly: boolean; stationId?: string }) {
  const [unavailableImage, setUnavailableImage] = useState<string>();
  const read = useCallback(async (signal: AbortSignal) => {
    const response = await apiFetch(production.operatorPreview(ngOnly, stationId), { signal });
    if (!response.ok) throw new Error('读取检验留图失败');
    if (response.status === 204) return null;
    const latest: Preview | null = await response.json();
    if (!latest || typeof latest.runId !== 'string' || latest.jobVersion !== version ||
      !(Date.parse(latest.startedAt) >= Date.parse(sessionStart)) || (ngOnly && latest.disposition !== 'NG')) {
      throw new Error('检验留图与当前会话或筛选条件不符');
    }
    return latest;
  }, [ngOnly, sessionStart, stationId, version]);
  const { data: preview, error, refreshing } = useProductionPoll(read, true, 4_000);

  if (!preview) return <span role="status">{error ? '检验留图暂不可用' : refreshing ? '正在读取检验留图…'
    : ngOnly ? '本次会话暂无不合格留图' : '本次会话暂无留图'}</span>;
  const url = traces.preview(preview.runId);
  const description = ngOnly ? '最近不合格留图' : '最近检验留图';
  return <>
    {error && <span role="status">正在显示上次读取的留图</span>}
    {unavailableImage === preview.runId ? <span>该留图已不可用</span> :
      <a href={url} target="_blank" rel="noopener noreferrer" aria-label={'打开' + description}>
        <img className="operator-inspection-image" src={url} alt={description} onError={() => setUnavailableImage(preview.runId)} />
      </a>}
    <span>{new Date(preview.startedAt).toLocaleTimeString('zh-CN')} · {localizeStatus(preview.disposition)} · V{version}</span>
  </>;
}
