import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Divider, Dropdown, Empty, Form, Input, InputNumber, Menu, Modal, Segmented, Select, Space, Spin, Steps, Switch, Table, Tabs, Tag, Typography, message } from 'antd';
import type { MenuProps } from 'antd';
import type { JobDescriptor, JobVersionDiff, JobVersionSnapshot, ResolvedRecipeParameterization, RunResult, RuntimeDependencyValidation, ValidationCandidateInfo, WorkflowPayload } from '../types';
import RecipePublicationDialog, { type PublicationTarget } from './RecipePublicationDialog';
import { responseErrorMessage } from '../apiErrors';
import { useWorkflowImport } from '../useWorkflowImport';
import type { PrepareWorkflowChange } from '../useWorkflowChanges';
import { apiFetch } from '../apiClient';
import { jobs as jobEndpoints, apiUrl } from '../endpoints';

type Props = { open: boolean; onClose: () => void; workflow: WorkflowPayload; prepareWorkflowChange: PrepareWorkflowChange; onRunResult: (result: RunResult) => void; };

type DraftEntry = { key: string; value: unknown };

function parseDraft(text: string): { entries?: DraftEntry[]; error?: string } {
  try {
    const parsed = JSON.parse(text);
    if (!parsed || Array.isArray(parsed) || typeof parsed !== 'object') return { error: '参数必须是 JSON 对象。' };
    return { entries: Object.entries(parsed).map(([key, value]) => ({ key, value })) };
  } catch {
    return { error: 'JSON 解析失败：修正后再切回表单模式。' };
  }
}

function stringifyEntries(entries: DraftEntry[]) {
  const obj: Record<string, unknown> = {};
  entries.forEach((entry) => { if (entry.key.trim()) obj[entry.key.trim()] = entry.value; });
  return JSON.stringify(obj, null, 2);
}

/** 参数草稿编辑：默认按值类型表单化，JSON 作为高级模式（嵌套值仍需在 JSON 中编辑） */
function ParameterDraft({ title, text, onChange, disabled }: { title: string; text: string; onChange: (next: string) => void; disabled?: boolean }) {
  const [mode, setMode] = useState<'form' | 'json'>('form');
  const [newKey, setNewKey] = useState('');
  const parsed = useMemo(() => parseDraft(text), [text]);
  const entries = parsed.entries ?? [];

  const commit = (rows: DraftEntry[]) => onChange(stringifyEntries(rows));
  const setValue = (key: string, value: unknown) => commit(entries.map((entry) => (entry.key === key ? { ...entry, value } : entry)));
  const removeKey = (key: string) => commit(entries.filter((entry) => entry.key !== key));
  const addKey = () => {
    const key = newKey.trim();
    if (!key || entries.some((entry) => entry.key === key)) return;
    commit([...entries, { key, value: '' }]);
    setNewKey('');
  };

  return (
    <div className="job-draft">
      <div className="job-draft-head">
        <Typography.Text strong>{title}</Typography.Text>
        <Segmented
          size="small"
          value={mode}
          onChange={(value) => setMode(value as 'form' | 'json')}
          options={[{ value: 'form', label: '表单' }, { value: 'json', label: 'JSON' }]}
        />
      </div>
      {mode === 'json' || parsed.error ? (
        <>
          <Input.TextArea rows={7} value={text} disabled={disabled} onChange={(event) => onChange(event.target.value)} style={{ fontFamily: 'monospace', marginTop: 6 }} />
          {parsed.error && <div className="job-draft-error">{parsed.error}</div>}
        </>
      ) : (
        <div className="job-draft-rows">
          {entries.length === 0 && <div className="job-draft-empty">暂无参数，可在下方新增。</div>}
          {entries.map((entry) => (
            <div className="job-draft-row" key={entry.key}>
              <span className="job-draft-key" title={entry.key}>{entry.key}</span>
              {typeof entry.value === 'number'
                ? <InputNumber size="small" disabled={disabled} value={entry.value} onChange={(value) => setValue(entry.key, Number(value ?? 0))} />
                : typeof entry.value === 'boolean'
                  ? <Switch size="small" disabled={disabled} checked={entry.value} onChange={(value) => setValue(entry.key, value)} />
                  : typeof entry.value === 'string'
                    ? <Input size="small" disabled={disabled} value={entry.value} onChange={(event) => setValue(entry.key, event.target.value)} />
                    : <Typography.Text code className="job-draft-nested" title={JSON.stringify(entry.value)}>{JSON.stringify(entry.value)}</Typography.Text>}
              <Button size="small" type="text" danger disabled={disabled} onClick={() => removeKey(entry.key)}>移除</Button>
            </div>
          ))}
          <div className="job-draft-add">
            <Input size="small" disabled={disabled} value={newKey} placeholder="新增参数键" onChange={(event) => setNewKey(event.target.value)} onPressEnter={addKey} />
            <Button size="small" disabled={disabled} onClick={addKey}>添加</Button>
          </div>
        </div>
      )}
    </div>
  );
}

export default function JobPanel({ open, onClose, workflow, prepareWorkflowChange, onRunResult }: Props) {
  const [jobs, setJobs] = useState<JobDescriptor[]>([]);
  const [selectedId, setSelectedId] = useState<string>();
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [note, setNote] = useState('');
  const [operationBusy, setBusy] = useState(false);
  const [recipeParams, setRecipeParams] = useState('{}');
  const [bindings, setBindings] = useState<Record<string,string>>({});
  const [baseWorkflowId, setBaseWorkflowId] = useState<string>();
  const [baseWorkflowRecipeId, setBaseWorkflowRecipeId] = useState<string>();
  const [baseWorkflowVersion, setBaseWorkflowVersion] = useState<number>();
  const [bindingTarget, setBindingTarget] = useState<string>();
  const [bindingKey, setBindingKey] = useState('MatchThreshold');
  const [resolved, setResolved] = useState<ResolvedRecipeParameterization>();
  const [candidates, setCandidates] = useState<ValidationCandidateInfo[]>([]);
  const [candidatesVersion, setCandidatesVersion] = useState<number>();
  const [dependency, setDependency] = useState<RuntimeDependencyValidation>();
  const [tab, setTab] = useState('versions');
  const [query, setQuery] = useState('');
  const [statusFilter, setStatusFilter] = useState<'all' | 'draft' | 'published'>('all');
  const [recipeCreateOpen, setRecipeCreateOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [loadError, setLoadError] = useState<string>();
  const [parameterLoading, setParameterLoading] = useState(false);
  const [parameterError, setParameterError] = useState<string>();
  const [parameterAttempt, setParameterAttempt] = useState(0);
  const [publicationTarget, setPublicationTarget] = useState<PublicationTarget>();
  const listGenerationRef = useRef(0);
  const listAbortRef = useRef<AbortController | undefined>(undefined);
  const activeRef = useRef(open);
  const [messageApi, contextHolder] = message.useMessage();

  const selected = useMemo(() => jobs.find((x) => x.id === selectedId), [jobs, selectedId]);
  const workflowImport = useWorkflowImport({ active: open, contextKey: selectedId, prepare: prepareWorkflowChange,
    error: (text) => { void messageApi.error(text); } });
  const busy = operationBusy || workflowImport.loading;
  const bindingTargets = useMemo(() => workflow.nodes.flatMap(n => Object.keys(n.parameters ?? {}).filter(key => !key.startsWith('__') && !['moduleId','moduleVersion','moduleHash'].includes(key)).map(key => ({ value:`${n.id}.${key}`, label:`${n.name ?? n.id} · ${key}` }))), [workflow]);

  const refresh = async () => {
    if (!activeRef.current) return;
    const generation = ++listGenerationRef.current;
    listAbortRef.current?.abort();
    const controller = new AbortController();
    listAbortRef.current = controller;
    const timeout = setTimeout(() => controller.abort(), 15_000);
    setLoading(true);
    setLoadError(undefined);
    try {
      const jobsRes = await apiFetch(jobEndpoints.list, { signal: controller.signal });
      if (!jobsRes.ok) throw new Error(await responseErrorMessage(jobsRes, '配方列表加载失败，请重试。'));
      const nextJobs: JobDescriptor[] = await jobsRes.json();
      if (!activeRef.current || generation !== listGenerationRef.current || controller.signal.aborted) return;
      if (!Array.isArray(nextJobs)) throw new Error('配方列表响应无效，请重试。');
      setJobs(nextJobs);
      setSelectedId((current) => nextJobs.some((job) => job.id === current) ? current : nextJobs[0]?.id);
    } catch (error) {
      if (activeRef.current && generation === listGenerationRef.current) setLoadError(controller.signal.aborted ? '配方列表读取超时，请重试。' : error instanceof Error ? error.message : '列表加载失败');
    } finally {
      clearTimeout(timeout);
      if (activeRef.current && generation === listGenerationRef.current) setLoading(false);
    }
  };
  useEffect(() => {
    activeRef.current = open;
    if (open) void refresh();
    else setPublicationTarget(undefined);
    return () => { activeRef.current = false; listGenerationRef.current += 1; listAbortRef.current?.abort(); };
  }, [open]);
  const invoke = async (action: () => Promise<void>) => { setBusy(true); try { await action(); await refresh(); } catch (error) { messageApi.error(error instanceof Error ? error.message : '操作失败'); } finally { setBusy(false); } };


  const parseValues = (text:string) => { const value=JSON.parse(text); if(!value || Array.isArray(value) || typeof value!=='object') throw new Error('参数必须是 JSON 对象。'); return value as Record<string,unknown>; };
  useEffect(() => {
    if (!open || !selected) return;
    let cancelled = false;
    const controller = new AbortController();
    setParameterLoading(true); setParameterError(undefined);
    setRecipeParams('{}'); setBindings({}); setResolved(undefined); setBaseWorkflowId(undefined); setBaseWorkflowRecipeId(undefined); setBaseWorkflowVersion(undefined);
    void (async () => {
      try {
        const requests = [
          apiFetch(jobEndpoints.parameters(selected.id), { signal: controller.signal }),
          apiFetch(apiUrl(`jobs/${encodeURIComponent(selected.id)}/versions/${selected.latestVersion}/parameterization`), { signal: controller.signal })
        ];
        const responses = await Promise.all(requests);
        if (responses.some((response) => !response.ok)) throw new Error('参数草稿加载失败，请重试。');
        const [recipe, parameterization] = await Promise.all(responses.map((response) => response.json()));
        if (cancelled) return;
        setRecipeParams(JSON.stringify(recipe.values ?? {}, null, 2));
        setBindings(parameterization.parameterBindings ?? {});
        setBaseWorkflowId(parameterization.baseWorkflow?.id ?? parameterization.effectiveWorkflow?.id);
        setBaseWorkflowRecipeId(selected.id);
        setBaseWorkflowVersion(selected.latestVersion);
      } catch (error) {
        if (!cancelled) setParameterError(error instanceof Error ? error.message : '参数加载失败');
      } finally { if (!cancelled) setParameterLoading(false); }
    })();
    return () => { cancelled = true; controller.abort(); };
  }, [open, selected?.id, selected?.latestVersion, parameterAttempt]);
  useEffect(() => {
    if (!selected) return;
    setCandidates([]); setCandidatesVersion(undefined); setDependency(undefined);
  }, [selected?.id]);
  const saveRecipeParameters = () => selected && invoke(async()=>{ const response=await apiFetch(jobEndpoints.parameters(selected.id),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({values:parseValues(recipeParams)})}); const data=await response.json(); if(!response.ok) throw new Error(data.detail??'保存配方参数失败'); setRecipeParams(JSON.stringify(data.values,null,2)); messageApi.success('配方参数草稿已保存'); });
  const workflowMatchesSelectedRecipe = Boolean(selected
    && baseWorkflowRecipeId === selected.id
    && baseWorkflowVersion === selected.latestVersion
    && baseWorkflowId
    && workflow.id === baseWorkflowId);
  const canSaveSelectedVersion = workflowMatchesSelectedRecipe && !parameterLoading && !parameterError;
  const resolveBindings = () => selected && workflowMatchesSelectedRecipe && invoke(async()=>{ const response=await apiFetch(jobEndpoints.resolve(selected.id),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({workflow,bindings})}); const data:ResolvedRecipeParameterization & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'解析参数绑定失败'); setResolved(data); messageApi.success(`已解析 ${data.bindingCount} 个绑定`); });
  const addBinding = () => { if(!bindingTarget || !bindingKey.trim()) return; setBindings(x=>({...x,[bindingTarget]:`recipe.${bindingKey.trim()}`})); };

  const createRecipe = () => invoke(async () => {
    const response = await apiFetch('/api/recipes', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ name:name.trim(), description, workflow, changeNote:'Initial recipe version' }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.detail ?? data.error ?? '创建配方失败'); setSelectedId(data.id); setRecipeCreateOpen(false); setQuery(''); setStatusFilter('all'); setTab('versions'); messageApi.success('配方 V1 草稿已创建，验证通过后即可发布');
  });
  const saveVersion = () => selected && workflowMatchesSelectedRecipe && invoke(async () => {
    const response = await apiFetch(jobEndpoints.versions(selected.id), { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ workflow, note, parameterBindings:bindings }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.detail ?? data.error ?? '保存版本失败'); messageApi.success(`草稿 V${data.version} 已创建`);
  });
  const loadCandidates = (version:number) => selected && invoke(async () => {
    const response = await apiFetch(jobEndpoints.validationCandidates(selected.id, version));
    const data: ValidationCandidateInfo[] = await response.json();
    if (!response.ok) throw new Error('查询验证记录失败');
    setCandidates(data); setCandidatesVersion(version);
    if (!data.length) messageApi.info('没有与该版本工作流哈希匹配的已完成数据集验证记录');
  });
  const linkCandidate = (version:number, validationRunId:string) => selected && invoke(async () => {
    const response = await apiFetch(jobEndpoints.validate(selected.id, version), { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ validationRunId }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.detail ?? data.error ?? '关联验证记录失败');
    if (!data.accepted) throw new Error(`验证策略未通过：${data.reason}`); messageApi.success(`V${version} 已通过验证 · ${validationRunId.slice(0,12)}`);
  });
  const requestPublish = (version: number, rollback = false) => {
    if (!selected || busy || publicationTarget) return;
    setPublicationTarget({ jobId: selected.id, name: selected.name, version, rollback });
  };
  const importVersion = (version: number, base: boolean) => {
    if (!selected || operationBusy) return;
    const jobId = selected.id;
    void workflowImport.load({ label: `载入 ${jobId} V${version} ${base ? '基础' : '有效'}工作流`,
      url: jobEndpoints.version(jobId, version),
      select: (payload) => {
        const data = payload as JobVersionSnapshot;
        if (data?.jobId !== jobId || data.version !== version) throw new Error('配方版本响应与所选版本不匹配');
        return base ? data.baseWorkflow ?? data.workflow : data.workflow;
      },
      onApplied: (payload) => {
        const data = payload as JobVersionSnapshot;
        setBindings(data.parameterBindings ?? {}); setResolved(undefined);
        void messageApi.success(`已加载 ${jobId} V${version} 的${base ? '基础' : '有效'}工作流`);
      },
    });
  };
  const loadVersion = (version: number) => importVersion(version, false);
  const loadBaseVersion = (version: number) => importVersion(version, true);
  const validateDependencies = () => selected && invoke(async () => { const response=await apiFetch(jobEndpoints.dependencies(selected.id)); const data:RuntimeDependencyValidation & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'依赖校验失败'); setDependency(data); if(!data.compatible) throw new Error(data.drifts[0]?.message??'运行时依赖发生变化'); messageApi.success('运行时依赖一致'); });
  const runPublished = () => selected && invoke(async () => { const response=await apiFetch(jobEndpoints.run(selected.id),{method:'POST'}); const data:RunResult=await response.json(); onRunResult(data); if(!response.ok) throw new Error(data.error??'已发布版本运行失败'); messageApi.success('当前生效配方已运行'); });
  const cloneRecipe = () => selected && invoke(async () => {
    const copyName = window.prompt('新配方名称', `${selected.name} 副本`); if (!copyName?.trim()) return;
    const response=await apiFetch(jobEndpoints.clone(selected.id),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:copyName.trim()})});
    const data=await response.json(); if(!response.ok) throw new Error(data.detail??data.error??'复制配方失败'); setSelectedId(data.id); messageApi.success('配方已复制');
  });
  const showDiff = (from:number,to:number) => selected && invoke(async () => { const response=await apiFetch(jobEndpoints.diff(selected.id, from, to)); const data:JobVersionDiff & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'版本差异比较失败'); Modal.info({title:`${selected.id} V${from} → V${to}`,width:900,content:<pre className="job-diff-pre">{data.changes.length?data.changes.map(x=>`${x.kind}  ${x.path}\n  ${x.before??'∅'} → ${x.after??'∅'}`).join('\n\n'):'没有影响执行语义的变更。'}</pre>}); });

  const lifecycleTag = (job: JobDescriptor) => {
    const latest = job.versions.find((v) => v.version === job.latestVersion);
    if (job.publishedVersion === job.latestVersion) return <Tag color="green">生效 V{job.latestVersion}</Tag>;
    if (latest?.validation?.accepted) return <Tag color="blue">已验证</Tag>;
    return <Tag>草稿</Tag>;
  };

  const keyword = query.trim().toLowerCase();
  const visibleJobs = jobs.filter((job) => {
    const matches = `${job.name} ${job.id}`.toLowerCase().includes(keyword);
    return matches && (statusFilter === 'all' || (statusFilter === 'published' ? job.publishedVersion != null : job.publishedVersion !== job.latestVersion));
  });
  const filtered = Boolean(keyword || statusFilter !== 'all');
  const navItems: MenuProps['items'] = visibleJobs.map((job) => ({
    key: job.id,
    label: <span className="job-nav-recipe"><span className="job-nav-recipe-copy" title={job.name}><span>{job.name}</span></span>{lifecycleTag(job)}</span>
  }));
  const latestVersion = selected?.versions.find((version) => version.version === selected.latestVersion);
  const latestPublished = Boolean(selected && selected.publishedVersion === selected.latestVersion);
  const validated = Boolean(latestVersion?.validation?.accepted);
  const needsValidation = Boolean(selected && !validated);
  const canCreateRecipe = Boolean(name.trim() && workflow.nodes.length);
  const openRecipeCreate = () => {
    setName(workflow.name ?? ''); setDescription(''); setRecipeCreateOpen(true);
  };

  const bindingsRows = Object.entries(bindings).map(([target, source]) => ({
    target,
    source,
    resolved: resolved?.snapshot?.[source]
  }));

  const versions = selected ? [...selected.versions].reverse() : [];

  const versionRowMenu = (version: number, published: boolean, validationAccepted: boolean): MenuProps['items'] => [
    { key: 'base', label: '载入基础版本到设计器', onClick: () => loadBaseVersion(version) },
    { key: 'effective', label: '载入有效版本到设计器', onClick: () => loadVersion(version) },
    { type: 'divider' },
    { key: 'rollback', label: '回滚到此版本', danger: true, disabled: published || !validationAccepted || !selected?.publishedVersion || version >= selected.publishedVersion, onClick: () => requestPublish(version, true) },
    { key: 'diff', label: '与上一版本对比', disabled: version <= 1, onClick: () => showDiff(version - 1, version) }
  ];

  const headingMenu: MenuProps['items'] = [
    { key: 'clone', label: '复制配方', onClick: cloneRecipe },
    { key: 'load-effective', label: `载入当前生效版本${selected?.publishedVersion ? ` V${selected.publishedVersion}` : ''}`, disabled: !selected?.publishedVersion, onClick: () => selected?.publishedVersion && loadVersion(selected.publishedVersion) },
    { key: 'diff-latest', label: '对比最近两个版本', disabled: !selected || selected.versions.length < 2, onClick: () => selected && showDiff(Math.max(1, selected.latestVersion - 1), selected.latestVersion) }
  ];

  return <Modal open={open} width={1380} title="配方管理" className="job-manager-modal" style={{ top: 24 }}
    styles={{ body: { maxHeight: 'calc(100dvh - 140px)', overflowY: 'auto' } }} footer={null} onCancel={onClose}
    closable={!operationBusy} mask={{ closable: !operationBusy }} keyboard={!operationBusy} destroyOnHidden>
    {contextHolder}
    <div className="job-manager-toolbar">
      <div><strong>配方工作台</strong><p>{jobs.length} 个配方 · {jobs.filter((job) => job.publishedVersion != null).length} 个已发布配方</p></div>
      <Space wrap><Button onClick={() => void refresh()} loading={loading} disabled={busy}>刷新列表</Button>
        <Button type="primary" onClick={openRecipeCreate} disabled={busy}>新建配方</Button></Space>
    </div>
    <div className="job-workflow-source"><span>当前设计器工作流</span><strong>{workflow.name || workflow.id}</strong><span>{workflow.nodes.length} 个节点 · {workflow.edges.length} 条连线</span>
      <small>新建配方、保存新版本均使用当前工作流快照。</small></div>
    {loadError && <Alert type="error" showIcon title={loadError} action={<Button size="small" onClick={() => void refresh()}>重试</Button>} />}
    <div className="job-grid">
      <section className="job-nav-column" aria-label="配方导航">
        <Input.Search allowClear value={query} onChange={(event) => setQuery(event.target.value)} placeholder="搜索配方名称" aria-label="搜索配方" />
        <Segmented block size="small" value={statusFilter} onChange={(value) => setStatusFilter(value as typeof statusFilter)} options={[
          { value: 'all', label: '全部' }, { value: 'draft', label: '待发布' }, { value: 'published', label: '已发布' }
        ]} />
        <div className="job-nav-summary">{filtered ? `找到 ${visibleJobs.length} 条记录` : `${jobs.length} 条配方记录`}{loading && <Spin size="small" />}</div>
        {filtered && selected && !visibleJobs.some((job) => job.id === selected.id) && <small className="job-nav-empty">当前查看的配方不在筛选结果中。</small>}
        <Menu mode="inline" className="job-nav" aria-label="配方列表" disabled={busy}
          selectedKeys={selectedId ? [selectedId] : []} items={navItems}
          onClick={({ key }) => {
            const job = jobs.find((item) => item.id === key);
            if (!job) return;
            setSelectedId(job.id); setTab('versions');
          }} />
        {!loading && navItems.length === 0 && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={filtered ? '没有匹配的配方' : '还没有配方'}>
          <Button onClick={filtered ? () => { setQuery(''); setStatusFilter('all'); } : openRecipeCreate}>{filtered ? '清空筛选' : '创建第一个配方'}</Button>
        </Empty>}
        <div className="job-nav-guide"><strong>创建到生产</strong><span>从当前工作流创建配方 → 数据集验证 → 发布 → 生产配置中选用</span></div>
      </section>

      {/* 右列：草稿/生效状态 + 参数 / 版本 / 验证 / 发布记录 */}
      <section className="job-detail">
        {!selected?<div className="job-empty"><Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="选择左侧配方查看版本，或从当前工作流创建新配方"><Button type="primary" onClick={openRecipeCreate}>从当前工作流创建配方</Button></Empty></div>:<>
          <div className="job-heading">
            <div>
              <strong>{selected.name}</strong>
              <Space wrap size={6} style={{ marginTop: 6 }}>
                <Tag color={selected.publishedVersion ? 'green' : 'default'}>当前生效 {selected.publishedVersion ? `V${selected.publishedVersion}` : '无'}</Tag>
                <Tag color={latestPublished ? 'green' : 'gold'}>{latestPublished ? '最新版本' : '待发布草稿'} V{selected.latestVersion}</Tag>
                {selected.publishedVersion !== selected.latestVersion && <Tag color="orange">草稿尚未生效</Tag>}
              </Space>
            </div>
            <Space wrap>
              <Button onClick={saveVersion} loading={busy} disabled={!canSaveSelectedVersion || !workflow.nodes.length} title={workflowMatchesSelectedRecipe ? '使用当前配方工作流创建新的不可变版本' : '先载入所选配方的基础版本，再编辑并保存'}>保存草稿版本</Button>
              <Button onClick={runPublished} disabled={!selected.publishedVersion||!selected.publishedDependencyManifestHash}>运行当前版本</Button>
              <Dropdown menu={{ items: headingMenu }} trigger={['click']}><Button>更多 ▾</Button></Dropdown>
            </Space>
          </div>
          {baseWorkflowRecipeId === selected.id && baseWorkflowVersion === selected.latestVersion && baseWorkflowId && workflow.id !== baseWorkflowId && <Alert
            type="warning"
            showIcon
            message="当前画布不属于所选配方，已阻止保存新版本"
            description={<Space wrap>配方基础工作流：<Typography.Text code>{baseWorkflowId}</Typography.Text>；当前画布：<Typography.Text code>{workflow.id}</Typography.Text><Button size="small" onClick={() => void loadBaseVersion(selected.latestVersion)} disabled={busy}>载入 V{selected.latestVersion} 基础工作流</Button></Space>}
            style={{ marginBottom: 10 }}
          />}
          <div className="job-lifecycle">
            <Steps size="small" items={[
              { title: `V${selected.latestVersion} 已保存`, status: 'finish' },
              { title: validated ? '验证通过' : '数据集验证', status: validated ? 'finish' : latestPublished ? 'wait' : 'process' },
              { title: latestPublished ? '已发布，可用于生产' : '发布生产', status: latestPublished ? 'finish' : validated ? 'process' : 'wait' }
            ]} />
            <div className="job-next-action"><span>{latestPublished ? `生产使用已发布的 V${selected.publishedVersion}。修改后请保存为新草稿版本。` : needsValidation ? `下一步：为 V${selected.latestVersion} 关联通过的数据集验证记录。` : `下一步：检查 V${selected.latestVersion} 的变更并确认发布。`}</span>
              {!latestPublished && <Button type="primary" disabled={busy} onClick={() => { if (needsValidation) { setTab('validation'); void loadCandidates(selected.latestVersion); } else void requestPublish(selected.latestVersion); }}>{needsValidation ? '去验证' : `发布 V${selected.latestVersion}`}</Button>}
            </div>
          </div>
          <Input value={note} onChange={e=>setNote(e.target.value)} addonBefore="变更说明" placeholder="记录本次参数或流程修改（可选）" style={{ marginBottom: 10 }} />

          <Tabs
            activeKey={tab}
            onChange={setTab}
            items={[
              {
                key: 'parameters',
                label: '参数',
                children: (
                  <Space direction="vertical" size="middle" style={{ width: '100%' }}>
                    <Alert type="warning" showIcon message="草稿参数不会直接修改已发布版本" description="先保存参数草稿，再将节点参数映射到 recipe.*，解析预览后保存草稿版本。不可变版本会固化具体参数值和有效工作流哈希。" />
                    {parameterError && <Alert type="error" showIcon title={parameterError}
                      action={<Button size="small" disabled={busy} onClick={() => setParameterAttempt((attempt) => attempt + 1)}>重试加载参数</Button>} />}
                    {parameterLoading && <span><Spin size="small" /> 正在加载参数…</span>}
                    <div className="job-draft-grid job-draft-grid-single">
                      <div>
                        <ParameterDraft title="配方参数草稿" text={recipeParams} onChange={setRecipeParams} disabled={busy || parameterLoading || Boolean(parameterError)} />
                        <Button style={{marginTop:6}} disabled={busy || parameterLoading || Boolean(parameterError)} onClick={saveRecipeParameters}>保存配方参数</Button>
                      </div>
                    </div>
                    <Divider titlePlacement="left" style={{ margin: 0 }}>配方参数绑定</Divider>
                    <Space wrap>
                      <Select disabled={busy || parameterLoading || Boolean(parameterError) || !workflowMatchesSelectedRecipe} showSearch placeholder="节点参数" style={{width:280}} value={bindingTarget} onChange={setBindingTarget} options={bindingTargets}/><Input disabled={busy || parameterLoading || Boolean(parameterError) || !workflowMatchesSelectedRecipe} style={{width:190}} value={bindingKey} onChange={e=>setBindingKey(e.target.value)} placeholder="参数键"/><Button disabled={busy || parameterLoading || Boolean(parameterError) || !workflowMatchesSelectedRecipe || !bindingTarget || !bindingKey.trim()} onClick={addBinding}>添加绑定</Button><Button type="primary" disabled={busy || parameterLoading || Boolean(parameterError) || !workflowMatchesSelectedRecipe} onClick={resolveBindings}>解析预览</Button>{resolved&&<Button disabled={busy} onClick={()=>void workflowImport.load({ label: '载入参数绑定后的有效工作流', workflow: resolved.effectiveWorkflow })}>加载有效版本到设计器</Button>}
                    </Space>
                    <Table size="small" pagination={false} rowKey="target" dataSource={bindingsRows} locale={{ emptyText: '尚未配置绑定' }} columns={[
                      {title:'工作流参数',dataIndex:'target'},
                      {title:'参数来源',dataIndex:'source',render:(v:string)=><Tag color="purple">{v}</Tag>},
                      {title:'解析后的有效值',render:(_,r)=><code className="job-draft-nested">{r.resolved === undefined ? '—' : JSON.stringify(r.resolved)}</code>},
                      {title:'',width:80,render:(_,r)=><Button size="small" danger disabled={busy || parameterLoading || Boolean(parameterError)} onClick={()=>setBindings(x=>{const n={...x};delete n[r.target];return n})}>移除</Button>}
                    ]} />
                    {resolved&&<Alert type="success" showIcon message={`Resolved effective hash ${resolved.effectiveWorkflowHash.slice(0,16)}…`} description={<span>Base {resolved.baseWorkflowHash.slice(0,12)}… · {resolved.bindingCount} bindings · Frozen snapshot: <code>{JSON.stringify(resolved.snapshot)}</code></span>}/>}
                  </Space>
                )
              },
              {
                key: 'versions',
                label: '版本',
                children: (
                  <Table size="small" scroll={{ x: 980 }} pagination={false} rowKey="version" dataSource={versions} locale={{ emptyText: '暂无版本' }} columns={[
                    {title:'版本',width:110,render:(_,r)=>r.published?<Tag color="green">V{r.version} 生效</Tag>:r.version===selected.latestVersion?<Tag color="gold">V{r.version} 草稿</Tag>:<Tag>V{r.version}</Tag>},
                    {title:'生命周期',width:110,render:(_,r)=>r.published?<Tag color="green">已发布</Tag>:r.validation?.accepted?<Tag color="blue">已验证</Tag>:r.validation?<Tag color="red">未通过</Tag>:<Tag>草稿</Tag>},
                    {title:'验证',width:190,render:(_,r)=>r.validation?<span>{r.validation.accepted?'通过':'失败'}{r.validation.summary?` · ${(r.validation.summary.accuracy*100).toFixed(2)}%`:''}</span>:'未关联'},
                    {title:'创建时间',dataIndex:'createdAt',width:165,render:(v:string)=>new Date(v).toLocaleString('zh-CN')},
                    {title:'绑定数',width:80,render:(_,r)=>r.bindingCount?<Tag color="purple">{r.bindingCount}</Tag>:'—'},
                    {title:'变更说明',dataIndex:'note'},
                    {title:'操作',width:210,fixed:'right',render:(_,r)=><Space size={4}>
                      <Button size="small" onClick={()=>{setTab('validation'); loadCandidates(r.version);}} disabled={!!r.validation?.accepted}>验证</Button>
                      <Button size="small" type="primary" onClick={()=>void requestPublish(r.version)} disabled={r.published||!r.validation?.accepted}>发布</Button>
                      <Dropdown menu={{ items: versionRowMenu(r.version, r.published, !!r.validation?.accepted) }} trigger={['click']}><Button size="small">更多 ▾</Button></Dropdown>
                    </Space>}
                  ]}/>
                )
              },
              {
                key: 'validation',
                label: '验证',
                children: (
                  <Space direction="vertical" size="middle" style={{ width: '100%' }}>
                    <Alert type="info" showIcon message="验证记录必须先于发布" description="发布前需要将该版本的不可变工作流哈希关联到一次已完成且通过策略的数据集验证。验证只认可哈希完全一致的记录。" />
                    <Space wrap>
                      <span>目标版本</span>
                      <Select
                        style={{ width: 150 }}
                        value={candidatesVersion ?? selected.latestVersion}
                        onChange={(value) => loadCandidates(value)}
                        options={versions.map((v) => ({ value: v.version, label: v.published ? `V${v.version}（生效）` : v.version === selected.latestVersion ? `V${v.version}（草稿）` : `V${v.version}` }))}
                      />
                      <Button onClick={() => loadCandidates(candidatesVersion ?? selected.latestVersion)} loading={busy}>查询可用验证记录</Button>
                      {!!candidates.length && <Button type="primary" onClick={() => linkCandidate(candidatesVersion ?? selected.latestVersion, candidates[0].validationRunId)} loading={busy}>关联最佳记录</Button>}
                    </Space>
                    <Table
                      size="small"
                      rowKey="validationRunId"
                      pagination={false}
                      dataSource={candidates}
                      locale={{ emptyText: candidatesVersion ? '没有与该版本哈希匹配的已完成验证记录' : '点击“查询可用验证记录”列出候选' }}
                      columns={[
                        { title: '验证运行', dataIndex: 'validationRunId', render: (v: string) => <Typography.Text code>{v.slice(0, 16)}…</Typography.Text> },
                        { title: '数据集', dataIndex: 'datasetId', width: 180 },
                        { title: '完成时间', dataIndex: 'completedAt', width: 170, render: (v: string | null) => v ? new Date(v).toLocaleString('zh-CN') : '—' },
                        { title: '准确率', width: 100, render: (_, r) => `${(r.summary.accuracy * 100).toFixed(2)}%` },
                        { title: '误检 / 漏检', width: 120, render: (_, r) => `${r.summary.falseOk} / ${r.summary.falseNg}` },
                        { title: '', width: 80, render: (_, r) => <Button size="small" type="primary" onClick={() => linkCandidate(candidatesVersion ?? selected.latestVersion, r.validationRunId)} loading={busy}>关联</Button> }
                      ]}
                    />
                    <Divider titlePlacement="left" style={{ margin: 0 }}>运行时依赖</Divider>
                    <Space wrap>
                      <Button onClick={validateDependencies} disabled={!selected.publishedVersion||!selected.publishedDependencyManifestHash} loading={busy}>校验依赖</Button>
                      {selected.publishedDependencyManifestHash && <Typography.Text type="secondary">生效清单 {selected.publishedDependencyManifestHash.slice(0, 16)}…</Typography.Text>}
                    </Space>
                    {dependency && <Alert
                      type={dependency.compatible ? 'success' : 'error'}
                      showIcon
                      message={dependency.compatible ? '运行时依赖一致' : '运行时依赖发生变化'}
                      description={dependency.compatible ? undefined : (
                        <ul style={{ margin: 0, paddingLeft: 18 }}>
                          {dependency.drifts.slice(0, 8).map((drift, index) => <li key={`${drift.id}-${index}`}>{drift.kind} · {drift.id}：{drift.message}</li>)}
                        </ul>
                      )}
                    />}
                  </Space>
                )
              },
              {
                key: 'publications',
                label: '发布记录',
                children: (
                  <Space direction="vertical" size="middle" style={{ width: '100%' }}>
                    <Space wrap>
                      <Button onClick={runPublished} disabled={!selected.publishedVersion||!selected.publishedDependencyManifestHash}>运行当前版本</Button>
                      <Typography.Text type="secondary">生产运行只使用当前生效版本（V{selected.publishedVersion ?? '—'}）。</Typography.Text>
                    </Space>
                    {selected.publicationHistory.length
                      ? <div className="publication-history">{[...selected.publicationHistory].reverse().map((e,i)=><Tag key={`${e.at}-${i}`} color={e.action==='Rollback'?'orange':'blue'}>{e.action==='Rollback'?'回滚':'发布'} V{e.version} · {new Date(e.at).toLocaleString('zh-CN')}</Tag>)}</div>
                      : <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="尚无发布记录" />}
                  </Space>
                )
              }
            ]}
          />
        </>}
      </section>
    </div>
    <Modal open={recipeCreateOpen} title="从当前工作流创建配方" okText="创建配方" cancelText="取消" width={620} confirmLoading={busy}
      okButtonProps={{ disabled: !canCreateRecipe }} closable={!busy} mask={{ closable: !busy }} keyboard={!busy}
      onCancel={() => { if (!busy) setRecipeCreateOpen(false); }} onOk={() => { if (canCreateRecipe && !busy) void createRecipe(); }} destroyOnHidden>
      <Alert type={workflow.nodes.length ? 'info' : 'warning'} showIcon
        title={workflow.nodes.length ? `工作流来源：${workflow.name || workflow.id}` : '当前工作流为空'}
        description={workflow.nodes.length ? `${workflow.nodes.length} 个节点 · ${workflow.edges.length} 条连线。创建后保存为 V1 草稿，验证并发布后才能用于生产。` : '请先在设计器添加节点或选择模板。'} />
      <Form layout="vertical" className="job-recipe-form">
        <Form.Item label="配方名称" required><Input aria-label="配方名称" value={name} disabled={busy} placeholder="例如：外观标准检测" onChange={(event) => setName(event.target.value)} /></Form.Item>
        <Form.Item label="说明"><Input.TextArea aria-label="配方说明" value={description} disabled={busy} rows={2} placeholder="说明适用工况或检测要求（可选）" onChange={(event) => setDescription(event.target.value)} /></Form.Item>
      </Form>
    </Modal>
    {open && publicationTarget && <RecipePublicationDialog key={`${publicationTarget.jobId}:${publicationTarget.version}:${publicationTarget.rollback}`}
      target={publicationTarget} onCancel={() => { setPublicationTarget(undefined); void refresh(); }} onPublished={(job) => {
        setPublicationTarget(undefined);
        void messageApi.success(`配方 ${job.name} 已生效 → V${job.publishedVersion}`);
        void refresh();
      }} />}
  </Modal>;
}
