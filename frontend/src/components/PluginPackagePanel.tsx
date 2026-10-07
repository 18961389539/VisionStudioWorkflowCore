import { useCallback, useEffect, useState } from 'react';
import { Alert, Button, Divider, InputNumber, message, Modal, Select, Space, Table, Tag, Typography, Upload } from 'antd';
import type { UploadFile } from 'antd/es/upload/interface';
import type { PluginBenchmarkRun, PluginLoadInfo, PluginPackagePreflightResult, PluginPackageVersionInfo, PluginWorkerPerformanceProfile, PluginWorkerStatus, TrustedPluginPublisher, ValidationDatasetSummary, ValidationRunRecord } from '../types';
import { localizeStatus } from '../i18n';

type Props = { open: boolean; onClose: () => void; onCatalogRefresh: () => Promise<void> };

async function readError(response: Response): Promise<string> {
  const body = await response.json().catch(() => undefined);
  return body?.detail ?? body?.error ?? `${response.status} ${response.statusText}`;
}

export default function PluginPackagePanel({ open, onClose, onCatalogRefresh }: Props) {
  const [runtime, setRuntime] = useState<PluginLoadInfo[]>([]);
  const [packages, setPackages] = useState<PluginPackageVersionInfo[]>([]);
  const [publishers, setPublishers] = useState<TrustedPluginPublisher[]>([]);
  const [workers, setWorkers] = useState<PluginWorkerStatus[]>([]);
  const [performance, setPerformance] = useState<PluginWorkerPerformanceProfile[]>([]);
  const [benchmarks, setBenchmarks] = useState<PluginBenchmarkRun[]>([]);
  const [datasets, setDatasets] = useState<ValidationDatasetSummary[]>([]);
  const [validationRuns, setValidationRuns] = useState<ValidationRunRecord[]>([]);
  const [benchmarkDatasetId, setBenchmarkDatasetId] = useState<string>();
  const [benchmarkValidationRunId, setBenchmarkValidationRunId] = useState<string>();
  const [benchmarkPluginId, setBenchmarkPluginId] = useState<string>();
  const [benchmarkBaselineRunId, setBenchmarkBaselineRunId] = useState<string>();
  const [benchmarkItems, setBenchmarkItems] = useState<number>(50);
  const [packageFile, setPackageFile] = useState<UploadFile>();
  const [publisherFile, setPublisherFile] = useState<UploadFile>();
  const [preflight, setPreflight] = useState<PluginPackagePreflightResult>();
  const [busy, setBusy] = useState(false);
  const [messageApi, contextHolder] = message.useMessage();

  const refresh = useCallback(async () => {
    const [runtimeResponse, packageResponse, publisherResponse, workerResponse, performanceResponse, benchmarkResponse, datasetResponse] = await Promise.all([
      fetch('/api/plugins'), fetch('/api/plugin-packages'), fetch('/api/plugin-publishers'), fetch('/api/plugin-workers'), fetch('/api/plugin-workers/performance'), fetch('/api/plugin-benchmarks'), fetch('/api/validation/datasets')
    ]);
    setRuntime(runtimeResponse.ok ? await runtimeResponse.json() : []);
    setPackages(packageResponse.ok ? await packageResponse.json() : []);
    setPublishers(publisherResponse.ok ? await publisherResponse.json() : []);
    setWorkers(workerResponse.ok ? await workerResponse.json() : []);
    setPerformance(performanceResponse.ok ? await performanceResponse.json() : []);
    setBenchmarks(benchmarkResponse.ok ? await benchmarkResponse.json() : []);
    setDatasets(datasetResponse.ok ? await datasetResponse.json() : []);
  }, []);

  useEffect(() => { if (open) refresh().catch(() => undefined); }, [open, refresh]);

  const submitPackage = async (mode: 'preflight' | 'install') => {
    const native = packageFile?.originFileObj;
    if (!native) return messageApi.warning('Select a .vspkg first');
    setBusy(true);
    try {
      const form = new FormData(); form.append('file', native, native.name);
      const response = await fetch(`/api/plugin-packages/${mode}`, { method: 'POST', body: form });
      if (!response.ok) throw new Error(await readError(response));
      const body = await response.json();
      if (mode === 'preflight') {
        setPreflight(body);
        messageApi.success(body.valid ? 'Package preflight passed' : 'Package preflight failed');
      } else {
        setPreflight(undefined);
        messageApi.success(body.message ?? 'Package installed');
        await refresh();
        await onCatalogRefresh();
      }
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Plugin package operation failed'); }
    finally { setBusy(false); }
  };

  const selectVersion = async (id: string, version: string) => {
    setBusy(true);
    try {
      const response = await fetch(`/api/plugin-packages/${encodeURIComponent(id)}/versions/${encodeURIComponent(version)}/select`, { method: 'POST' });
      if (!response.ok) throw new Error(await readError(response));
      const body = await response.json(); messageApi.success(body.message ?? '已选择版本'); await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '选择版本失败'); }
    finally { setBusy(false); }
  };

  const rollback = async (id: string) => {
    setBusy(true);
    try {
      const response = await fetch(`/api/plugin-packages/${encodeURIComponent(id)}/rollback`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ targetVersion: null })
      });
      if (!response.ok) throw new Error(await readError(response));
      const body = await response.json(); messageApi.success(body.message ?? '已提交回滚操作'); await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '回滚失败'); }
    finally { setBusy(false); }
  };

  const removeVersion = async (id: string, version: string) => {
    setBusy(true);
    try {
      const response = await fetch(`/api/plugin-packages/${encodeURIComponent(id)}/versions/${encodeURIComponent(version)}`, { method: 'DELETE' });
      if (!response.ok) throw new Error(await readError(response));
      messageApi.success('已移除已安装版本'); await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '移除失败'); }
    finally { setBusy(false); }
  };

  const trustPublisher = async () => {
    const native = publisherFile?.originFileObj;
    if (!native) return messageApi.warning('请先选择发布者证书');
    setBusy(true);
    try {
      const form = new FormData(); form.append('file', native, native.name);
      const response = await fetch('/api/plugin-publishers/trust', { method: 'POST', body: form });
      if (!response.ok) throw new Error(await readError(response));
      messageApi.success('已信任该发布者'); setPublisherFile(undefined); await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '信任发布者失败'); }
    finally { setBusy(false); }
  };

  const untrustPublisher = async (thumbprint: string) => {
    setBusy(true);
    try {
      const response = await fetch(`/api/plugin-publishers/${encodeURIComponent(thumbprint)}`, { method: 'DELETE' });
      if (!response.ok) throw new Error(await readError(response));
      messageApi.success('已从信任库移除发布者'); await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '移除发布者失败'); }
    finally { setBusy(false); }
  };

  const restartWorker = async (pluginId: string) => {
    setBusy(true);
    try {
      const response = await fetch(`/api/plugin-workers/${encodeURIComponent(pluginId)}/restart`, { method: 'POST' });
      if (!response.ok) throw new Error(await readError(response));
      messageApi.success(`插件 ${pluginId} 的工作进程已重启`); await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '工作进程重启失败'); }
    finally { setBusy(false); }
  };

  const resetPerformance = async (pluginId: string) => {
    setBusy(true);
    try {
      const response = await fetch(`/api/plugin-workers/${encodeURIComponent(pluginId)}/performance/reset`, { method: 'POST' });
      if (!response.ok) throw new Error(await readError(response));
      messageApi.success(`已重置插件 ${pluginId} 的性能统计窗口`); await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '重置性能统计失败'); }
    finally { setBusy(false); }
  };


  const chooseBenchmarkDataset = async (datasetId?: string) => {
    setBenchmarkDatasetId(datasetId);
    setBenchmarkValidationRunId(undefined);
    setValidationRuns([]);
    if (!datasetId) return;
    const response = await fetch(`/api/validation/datasets/${encodeURIComponent(datasetId)}/runs`);
    if (!response.ok) return messageApi.error(await readError(response));
    const runs: ValidationRunRecord[] = await response.json();
    setValidationRuns(runs.filter(x => x.status === 'Completed'));
  };

  const startBenchmark = async () => {
    if (!benchmarkDatasetId || !benchmarkValidationRunId || !benchmarkPluginId) return messageApi.warning('请选择数据集、已完成的验证运行和 WorkerProcess 插件');
    setBusy(true);
    try {
      const response = await fetch('/api/plugin-benchmarks', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          datasetId: benchmarkDatasetId,
          validationRunId: benchmarkValidationRunId,
          pluginId: benchmarkPluginId,
          poolSizes: [1, 2, 4],
          maxItems: benchmarkItems,
          warmupItems: Math.min(5, benchmarkItems),
          workloadConcurrency: 4,
          baselineRunId: benchmarkBaselineRunId ?? null
        })
      });
      if (!response.ok) throw new Error(await readError(response));
      const body: PluginBenchmarkRun = await response.json();
      messageApi.success(`基准测试 ${body.runId} 已启动`);
      await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '启动基准测试失败'); }
    finally { setBusy(false); }
  };

  const cancelBenchmark = async (runId: string) => {
    setBusy(true);
    try {
      const response = await fetch(`/api/plugin-benchmarks/${encodeURIComponent(runId)}/cancel`, { method: 'POST' });
      if (!response.ok) throw new Error(await readError(response));
      messageApi.success('已请求取消基准测试');
      await refresh();
    } catch (error) { messageApi.error(error instanceof Error ? error.message : '取消基准测试失败'); }
    finally { setBusy(false); }
  };

  const downloadCiSpec = async (runId: string) => {
    setBusy(true);
    try {
      const response = await fetch(`/api/plugin-benchmarks/${encodeURIComponent(runId)}/ci-spec`);
      if (!response.ok) throw new Error(await readError(response));
      const text = JSON.stringify(await response.json(), null, 2);
      const blob = new Blob([text], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = url; anchor.download = `${runId}-ci-spec.json`; document.body.appendChild(anchor); anchor.click(); anchor.remove();
      URL.revokeObjectURL(url);
      messageApi.success('便携式 CI 基准测试规范已导出');
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'CI 规范导出失败'); }
    finally { setBusy(false); }
  };

  return <Modal open={open} title="插件包管理器 · V0.63" width={1280} footer={null} onCancel={onClose}>
    {contextHolder}
    <Alert type="info" showIcon message="签名插件包与 CI 性能回归门禁" description="V0.59 保留可重复的 V0.58 进程池 1/2/4 基准测试，并增加便携式基线 CI 规范和无界面运行器。导出已完成的基线并提交规范，GitHub Actions/Jenkins 会在 P95/P99、吞吐量、内存或失败率超出预算时使构建失败。" />

    <Divider titlePlacement="left">安装插件包</Divider>
    <Space wrap>
      <Upload accept=".vspkg,.zip" maxCount={1} fileList={packageFile ? [packageFile] : []} beforeUpload={() => false} onChange={(info) => { setPackageFile(info.fileList.at(-1)); setPreflight(undefined); }} onRemove={() => { setPackageFile(undefined); setPreflight(undefined); return true; }}>
        <Button>选择 .vspkg 文件</Button>
      </Upload>
      <Button loading={busy} disabled={!packageFile} onClick={() => submitPackage('preflight')}>预检</Button>
      <Button type="primary" loading={busy} disabled={!packageFile || (preflight ? !preflight.valid : false)} onClick={() => submitPackage('install')}>安装</Button>
    </Space>
    {preflight && <Alert style={{ marginTop: 10 }} type={preflight.valid ? (preflight.publisherTrusted ? 'success' : 'warning') : 'error'} showIcon
      message={`${preflight.id ?? '插件包'} ${preflight.version ?? ''} · ${preflight.signed ? (preflight.signatureValid ? '签名有效' : '签名无效') : '未签名'}`}
      description={<div><div>发布者：{preflight.publisherSubject ?? '—'} {preflight.publisherTrusted ? '（可信）' : '（不可信）'}</div><div>内容 SHA-256：<code>{preflight.contentSha256 || '—'}</code></div>{preflight.warnings?.map((x) => <div key={x}>{x}</div>)}</div>} />}

    <Divider titlePlacement="left">已安装版本</Divider>
    <Table size="small" pagination={false} rowKey={(x) => `${x.id}@${x.version}`} dataSource={packages} columns={[
      { title: '插件', render: (_: unknown, row: PluginPackageVersionInfo) => <><b>{row.name}</b><br/><code>{row.id}</code></> },
      { title: '版本', dataIndex: 'version', width: 100 },
      { title: '状态', width: 160, render: (_: unknown, row: PluginPackageVersionInfo) => <Space wrap>{row.active && <Tag color="green">生效中</Tag>}{row.pending && <Tag color="orange">待启用</Tag>}{!row.active && !row.pending && <Tag>已安装</Tag>}{row.restartRequired && <Tag color="volcano">需重启</Tag>}</Space> },
      { title: '发布者', render: (_: unknown, row: PluginPackageVersionInfo) => <div>{row.publisherSubject ?? '未签名'}<br/><Tag color={row.publisherTrusted ? 'green' : 'default'}>{row.publisherTrusted ? '可信' : '不可信'}</Tag></div> },
      { title: '插件包 SHA', render: (_: unknown, row: PluginPackageVersionInfo) => <code>{row.packageSha256.slice(0, 12)}…</code> },
      { title: '操作', width: 260, render: (_: unknown, row: PluginPackageVersionInfo) => <Space><Button size="small" disabled={row.active} onClick={() => selectVersion(row.id, row.version)}>选择</Button><Button size="small" disabled={!row.active} onClick={() => rollback(row.id)}>回滚</Button><Button size="small" danger disabled={row.active || row.pending} onClick={() => removeVersion(row.id, row.version)}>移除</Button></Space> }
    ]} />

    <Divider titlePlacement="left">受信任的发布者</Divider>
    <Space wrap style={{ marginBottom: 8 }}>
      <Upload accept=".cer,.crt" maxCount={1} fileList={publisherFile ? [publisherFile] : []} beforeUpload={() => false} onChange={(info) => setPublisherFile(info.fileList.at(-1))} onRemove={() => { setPublisherFile(undefined); return true; }}><Button>选择证书</Button></Upload>
      <Button type="primary" loading={busy} disabled={!publisherFile} onClick={trustPublisher}>信任发布者</Button>
    </Space>
    <Table size="small" pagination={false} rowKey="thumbprint" dataSource={publishers} columns={[
      { title: '发布者', render: (_: unknown, row: TrustedPluginPublisher) => <><b>{row.displayName ?? row.subject}</b><br/><Typography.Text type="secondary">{row.subject}</Typography.Text></> },
      { title: '证书指纹', render: (_: unknown, row: TrustedPluginPublisher) => <code>{row.thumbprint}</code> },
      { title: '有效期', render: (_: unknown, row: TrustedPluginPublisher) => `${row.notBefore?.slice(0,10) ?? '—'} → ${row.notAfter?.slice(0,10) ?? '—'}` },
      { title: '操作', width: 100, render: (_: unknown, row: TrustedPluginPublisher) => <Button danger size="small" onClick={() => untrustPublisher(row.thumbprint)}>移除</Button> }
    ]} />

    <Divider titlePlacement="left">运行时注册表</Divider>
    <Table size="small" pagination={false} rowKey={(p) => `${p.id}-${p.assemblyPath}`} dataSource={runtime} columns={[
      { title: '状态', width: 150, render: (_: unknown, row: PluginLoadInfo) => <Space wrap><Tag color={row.loaded ? 'green' : 'red'}>{row.loaded ? '已加载' : '失败'}</Tag>{row.restartRequired && <Tag color="orange">需要重启</Tag>}</Space> },
      { title: '插件', render: (_: unknown, row: PluginLoadInfo) => <><b>{row.name}</b> <code>{row.id}</code></> },
      { title: '版本', dataIndex: 'version', width: 100 },
      { title: '工具数', dataIndex: 'nodeCount', width: 70 },
      { title: '隔离模式', width: 220, render: (_: unknown, row: PluginLoadInfo) => <Space wrap><Tag color={row.isolationMode === 'WorkerProcess' ? 'blue' : 'default'}>{row.isolationMode === 'WorkerProcess' ? '独立进程' : '进程内'}</Tag>{row.workerPoolSize ? <Tag>进程池 {row.workerPoolSize}</Tag> : null}</Space> },
      { title: '程序集', render: (_: unknown, row: PluginLoadInfo) => row.error ? <Typography.Text type="danger">{row.error}</Typography.Text> : <code>{row.assemblyPath}</code> }
    ]} />
    <Divider titlePlacement="left">插件工作进程池</Divider>
    <Table size="small" pagination={false} rowKey="pluginId" dataSource={workers} expandable={{ expandedRowRender: (row) => <Table size="small" pagination={false} rowKey="workerIndex" dataSource={row.workers ?? []} columns={[
      { title: '#', dataIndex: 'workerIndex', width: 50 },
      { title: '状态', render: (_: unknown, worker) => <Space><Tag color={worker.state === 'Running' ? 'green' : worker.circuitOpen ? 'red' : 'default'}>{worker.state === 'Running' ? '运行中' : localizeStatus(worker.state)}</Tag>{worker.busy ? <Tag color="blue">忙碌</Tag> : null}</Space> },
      { title: '进程 ID', dataIndex: 'processId', width: 90, render: (value?: number) => value ?? '—' },
      { title: '重启次数', dataIndex: 'restartCount', width: 90 },
      { title: '工作集', render: (_: unknown, worker) => worker.workingSetBytes ? `${(worker.workingSetBytes / 1024 / 1024).toFixed(1)} MB` : '—' },
      { title: '最近错误', render: (_: unknown, worker) => worker.lastError ? <Typography.Text type="danger">{worker.lastError}</Typography.Text> : '—' }
    ]} /> }} columns={[
      { title: '插件', dataIndex: 'pluginId' },
      { title: '状态', width: 120, render: (_: unknown, row: PluginWorkerStatus) => <Tag color={row.state === 'Running' ? 'green' : row.circuitOpen ? 'red' : 'default'}>{row.state === 'Running' ? '运行中' : localizeStatus(row.state)}</Tag> },
      { title: '进程池', width: 110, render: (_: unknown, row: PluginWorkerStatus) => `${row.runningWorkers}/${row.poolSize} 个运行中` },
      { title: '忙碌数', width: 70, dataIndex: 'busyWorkers' },
      { title: '请求数', width: 90, dataIndex: 'requestCount' },
      { title: '图像传输', width: 230, render: (_: unknown, row: PluginWorkerStatus) => <><div>{row.imageTransport}</div><Typography.Text type="secondary">阈值 {(row.sharedMemoryThresholdBytes / 1024).toFixed(0)} KB · 共享传输 {row.sharedMemoryTransfers} 次</Typography.Text></> },
      { title: '工作集', width: 120, render: (_: unknown, row: PluginWorkerStatus) => row.workingSetBytes ? `${(row.workingSetBytes / 1024 / 1024).toFixed(1)} MB` : '—' },
      { title: '最近错误', render: (_: unknown, row: PluginWorkerStatus) => row.lastError ? <Typography.Text type="danger">{row.lastError}</Typography.Text> : '—' },
      { title: '操作', width: 100, render: (_: unknown, row: PluginWorkerStatus) => <Button size="small" loading={busy} onClick={() => restartWorker(row.pluginId)}>重启进程池</Button> }
    ]} />

    <Divider titlePlacement="left">工作进程性能分析</Divider>
    <Table size="small" pagination={false} rowKey="pluginId" dataSource={performance} expandable={{ expandedRowRender: (row) => <Table size="small" pagination={false} rowKey="nodeType" dataSource={row.tools ?? []} columns={[
      { title: '工具', dataIndex: 'nodeType' },
      { title: '样本数', dataIndex: 'sampleCount', width: 90 },
      { title: '失败数', dataIndex: 'failureCount', width: 90 },
      { title: '队列 P95', width: 100, render: (_: unknown, tool) => `${tool.queueWait.p95Ms.toFixed(2)} 毫秒` },
      { title: '执行 P95', width: 110, render: (_: unknown, tool) => `${tool.pluginExecute.p95Ms.toFixed(2)} 毫秒` },
      { title: '总耗时 P95', width: 100, render: (_: unknown, tool) => `${tool.total.p95Ms.toFixed(2)} 毫秒` }
    ]} /> }} columns={[
      { title: '插件', dataIndex: 'pluginId' },
      { title: '样本数', width: 90, render: (_: unknown, row: PluginWorkerPerformanceProfile) => `${row.successCount}/${row.sampleCount}` },
      { title: '队列 P95', width: 100, render: (_: unknown, row: PluginWorkerPerformanceProfile) => `${row.queueWait.p95Ms.toFixed(2)} 毫秒` },
      { title: '执行 P95', width: 110, render: (_: unknown, row: PluginWorkerPerformanceProfile) => `${row.pluginExecute.p95Ms.toFixed(2)} 毫秒` },
      { title: 'IPC P95', width: 100, render: (_: unknown, row: PluginWorkerPerformanceProfile) => `${row.ipcOverhead.p95Ms.toFixed(2)} 毫秒` },
      { title: '总耗时 P95 / P99', width: 150, render: (_: unknown, row: PluginWorkerPerformanceProfile) => `${row.total.p95Ms.toFixed(2)} / ${row.total.p99Ms.toFixed(2)} 毫秒` },
      { title: '主要阶段', width: 150, dataIndex: 'dominantStage' },
      { title: '自适应进程池', width: 130, render: (_: unknown, row: PluginWorkerPerformanceProfile) => <Tag color={!row.recommendationReady ? 'default' : row.recommendedPoolSize > row.poolSize ? 'orange' : row.recommendedPoolSize < row.poolSize ? 'blue' : 'green'}>{row.recommendationReady ? `${row.poolSize} → ${row.recommendedPoolSize}` : `正在收集 ${row.sampleCount} 个样本`}</Tag> },
      { title: '建议', render: (_: unknown, row: PluginWorkerPerformanceProfile) => <Typography.Text type="secondary">{row.recommendation}</Typography.Text> },
      { title: '操作', width: 90, render: (_: unknown, row: PluginWorkerPerformanceProfile) => <Button size="small" loading={busy} onClick={() => resetPerformance(row.pluginId)}>重置</Button> }
    ]} />


    <Divider titlePlacement="left">性能基准测试与回归门禁</Divider>
    <Alert type="info" showIcon style={{ marginBottom: 10 }} message="基准测试使用临时进程池" description="同一数据集和已完成的验证工作流将分别在临时的 1/2/4 个工作进程中回放。不会修改当前插件配置、已发布配方或生产依赖来源记录。" />
    <Space wrap style={{ marginBottom: 10 }}>
      <Select style={{ width: 220 }} placeholder="选择数据集" value={benchmarkDatasetId} onChange={chooseBenchmarkDataset} options={datasets.map(x => ({ value: x.id, label: `${x.name} (${x.itemCount})` }))} />
      <Select style={{ width: 250 }} placeholder="选择已完成的验证运行" value={benchmarkValidationRunId} onChange={setBenchmarkValidationRunId} options={validationRuns.map(x => ({ value: x.runId, label: `${x.runId.slice(0, 16)}… · ${x.workflowHash.slice(0, 8)}` }))} />
      <Select style={{ width: 220 }} placeholder="选择 WorkerProcess 插件" value={benchmarkPluginId} onChange={setBenchmarkPluginId} options={runtime.filter(x => x.loaded && x.isolationMode === 'WorkerProcess').map(x => ({ value: x.id, label: `${x.name} ${x.version}` }))} />
      <InputNumber min={1} max={1000} value={benchmarkItems} onChange={(value) => setBenchmarkItems(value ?? 50)} addonBefore="样本数" />
      <Select allowClear style={{ width: 260 }} placeholder="可选：选择基线基准测试" value={benchmarkBaselineRunId} onChange={setBenchmarkBaselineRunId} options={benchmarks.filter(x => x.status === 'Completed' && (!benchmarkPluginId || x.pluginId === benchmarkPluginId) && (!benchmarkDatasetId || x.datasetId === benchmarkDatasetId)).map(x => ({ value: x.runId, label: `${x.pluginVersion} · ${x.runId.slice(0, 12)}…` }))} />
      <Button type="primary" loading={busy} onClick={startBenchmark}>运行进程池 1 / 2 / 4 对比</Button>
    </Space>
    <Table size="small" pagination={{ pageSize: 8 }} rowKey="runId" dataSource={benchmarks} expandable={{ expandedRowRender: (row) => <Table size="small" pagination={false} rowKey="poolSize" dataSource={row.results ?? []} columns={[
      { title: '进程池', dataIndex: 'poolSize', width: 70 },
      { title: '吞吐量', render: (_: unknown, x) => `${x.throughputPerSecond.toFixed(2)} 项/秒` },
      { title: '工作流 P95 / P99', render: (_: unknown, x) => `${x.workflowDuration.p95Ms.toFixed(2)} / ${x.workflowDuration.p99Ms.toFixed(2)} 毫秒` },
      { title: '插件执行 P95', render: (_: unknown, x) => `${x.workerPerformance.pluginExecute.p95Ms.toFixed(2)} 毫秒` },
      { title: '队列 P95', render: (_: unknown, x) => `${x.workerPerformance.queueWait.p95Ms.toFixed(2)} 毫秒` },
      { title: '内存', render: (_: unknown, x) => `${(x.observedWorkingSetBytes / 1024 / 1024).toFixed(1)} MB` },
      { title: '失败数', render: (_: unknown, x) => `${x.failureCount}/${x.itemCount}` }
    ]} /> }} columns={[
      { title: '插件', render: (_: unknown, row: PluginBenchmarkRun) => <><b>{row.pluginId}</b><br/><Typography.Text type="secondary">v{row.pluginVersion}</Typography.Text></> },
      { title: '数据集 / 工作流', render: (_: unknown, row: PluginBenchmarkRun) => <><code>{row.datasetId}</code><br/><code>{row.workflowHash.slice(0, 10)}…</code></> },
      { title: '状态', width: 120, render: (_: unknown, row: PluginBenchmarkRun) => <Tag color={row.status === 'Completed' ? 'green' : row.status === 'Failed' ? 'red' : row.status === 'Cancelled' ? 'default' : 'blue'}>{localizeStatus(row.status)}</Tag> },
      { title: '建议进程池', width: 110, render: (_: unknown, row: PluginBenchmarkRun) => row.recommendation ? <Tag color="blue">{row.recommendation.recommendedPoolSize} 个工作进程</Tag> : '—' },
      { title: '回归门禁', width: 130, render: (_: unknown, row: PluginBenchmarkRun) => row.regressionGate ? <Tag color={row.regressionGate.status === 'PASS' ? 'green' : row.regressionGate.status === 'FAIL' ? 'red' : 'default'}>{{ PASS: '通过', FAIL: '失败' }[row.regressionGate.status] ?? row.regressionGate.status}</Tag> : '—' },
      { title: '建议 / 原因', render: (_: unknown, row: PluginBenchmarkRun) => <Typography.Text type={row.regressionGate?.status === 'FAIL' ? 'danger' : 'secondary'}>{row.regressionGate?.reasons?.length ? row.regressionGate.reasons.join(' ') : row.recommendation?.reason ?? row.error ?? '基准测试运行中…'}</Typography.Text> },
      { title: '操作', width: 170, render: (_: unknown, row: PluginBenchmarkRun) => <Space>{row.status === 'Running' ? <Button size="small" danger loading={busy} onClick={() => cancelBenchmark(row.runId)}>取消</Button> : row.status === 'Completed' ? <Button size="small" loading={busy} onClick={() => downloadCiSpec(row.runId)}>CI 规范</Button> : null}</Space> }
    ]} />

    <div className="modal-actions"><Button onClick={() => refresh()}>刷新</Button><Button onClick={onCatalogRefresh}>刷新节点目录</Button></div>
  </Modal>;
}
