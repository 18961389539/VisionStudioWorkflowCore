import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Collapse, Divider, Dropdown, Empty, Input, InputNumber, Menu, Modal, Segmented, Select, Space, Switch, Table, Tabs, Tag, Typography, message } from 'antd';
import type { MenuProps } from 'antd';
import type { JobDescriptor, JobVersionDiff, JobVersionSnapshot, ParameterValueSet, ProductDescriptor, ResolvedRecipeParameterization, RunResult, RuntimeDependencyValidation, ValidationCandidateInfo, WorkflowPayload } from '../types';

type Props = { open: boolean; onClose: () => void; workflow: WorkflowPayload; onLoadWorkflow: (workflow: WorkflowPayload) => void; onRunResult: (result: RunResult) => void; };

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

export default function JobPanel({ open, onClose, workflow, onLoadWorkflow, onRunResult }: Props) {
  const [jobs, setJobs] = useState<JobDescriptor[]>([]);
  const [products, setProducts] = useState<ProductDescriptor[]>([]);
  const [selectedId, setSelectedId] = useState<string>();
  const [selectedProductId, setSelectedProductId] = useState<string>();
  const [productId, setProductId] = useState('product-a');
  const [productName, setProductName] = useState('产品 A');
  const [jobId, setJobId] = useState('product-a-main');
  const [recipeCode, setRecipeCode] = useState('MAIN');
  const [name, setName] = useState('产品 A 检测');
  const [description, setDescription] = useState('');
  const [note, setNote] = useState('已验证的参数更新');
  const [busy, setBusy] = useState(false);
  const [productParams, setProductParams] = useState('{\n  "NominalWidth": 10.0\n}');
  const [recipeParams, setRecipeParams] = useState('{\n  "MatchThreshold": 0.8\n}');
  const [bindings, setBindings] = useState<Record<string,string>>({});
  const [bindingTarget, setBindingTarget] = useState<string>();
  const [bindingScope, setBindingScope] = useState<'product'|'recipe'>('recipe');
  const [bindingKey, setBindingKey] = useState('MatchThreshold');
  const [resolved, setResolved] = useState<ResolvedRecipeParameterization>();
  const [candidates, setCandidates] = useState<ValidationCandidateInfo[]>([]);
  const [candidatesVersion, setCandidatesVersion] = useState<number>();
  const [dependency, setDependency] = useState<RuntimeDependencyValidation>();
  const [tab, setTab] = useState('parameters');
  const [messageApi, contextHolder] = message.useMessage();

  const selected = useMemo(() => jobs.find((x) => x.id === selectedId), [jobs, selectedId]);
  const productJobs = useMemo(() => jobs.filter((x) => x.productId), [jobs]);
  const legacyJobs = useMemo(() => jobs.filter((x) => !x.productId), [jobs]);
  const bindingTargets = useMemo(() => workflow.nodes.flatMap(n => Object.keys(n.parameters ?? {}).filter(key => !key.startsWith('__') && !['moduleId','moduleVersion','moduleHash'].includes(key)).map(key => ({ value:`${n.id}.${key}`, label:`${n.name ?? n.id} · ${key}` }))), [workflow]);

  const refresh = async () => {
    const [jobsRes, productsRes] = await Promise.all([fetch('/api/jobs'), fetch('/api/products')]);
    if (jobsRes.ok) {
      const data: JobDescriptor[] = await jobsRes.json(); setJobs(data);
      if (!selectedId && data.length) setSelectedId(data[0].id);
    }
    if (productsRes.ok) {
      const data: ProductDescriptor[] = await productsRes.json(); setProducts(data);
      if (!selectedProductId && data.length) setSelectedProductId(data[0].id);
    }
  };
  useEffect(() => { if (open) void refresh(); }, [open]);
  const invoke = async (action: () => Promise<void>) => { setBusy(true); try { await action(); await refresh(); } catch (error) { messageApi.error(error instanceof Error ? error.message : '操作失败'); } finally { setBusy(false); } };


  const parseValues = (text:string) => { const value=JSON.parse(text); if(!value || Array.isArray(value) || typeof value!=='object') throw new Error('参数必须是 JSON 对象。'); return value as Record<string,unknown>; };
  const loadParameterDrafts = async () => {
    if (selectedProductId) { const response=await fetch(`/api/products/${encodeURIComponent(selectedProductId)}/parameters`); if(response.ok){const data:ParameterValueSet=await response.json(); setProductParams(JSON.stringify(data.values,null,2));} }
    if (selected) {
      const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/parameters`); if(response.ok){const data:ParameterValueSet=await response.json(); setRecipeParams(JSON.stringify(data.values,null,2));}
      const version=selected.latestVersion; const param=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}/parameterization`);
      if(param.ok){const data=await param.json(); setBindings(data.parameterBindings ?? {});}
    }
  };
  useEffect(()=>{ if(open) void loadParameterDrafts(); },[open,selectedId,selectedProductId]);
  const saveProductParameters = () => invoke(async()=>{ if(!selectedProductId) throw new Error('请先选择产品。'); const response=await fetch(`/api/products/${encodeURIComponent(selectedProductId)}/parameters`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({values:parseValues(productParams)})}); const data=await response.json(); if(!response.ok) throw new Error(data.detail??'保存产品参数失败'); setProductParams(JSON.stringify(data.values,null,2)); messageApi.success('产品参数草稿已保存'); });
  const saveRecipeParameters = () => selected && invoke(async()=>{ const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/parameters`,{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({values:parseValues(recipeParams)})}); const data=await response.json(); if(!response.ok) throw new Error(data.detail??'保存配方参数失败'); setRecipeParams(JSON.stringify(data.values,null,2)); messageApi.success('配方参数草稿已保存'); });
  const resolveBindings = () => selected && invoke(async()=>{ const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/parameterization/resolve`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({workflow,bindings})}); const data:ResolvedRecipeParameterization & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'解析参数绑定失败'); setResolved(data); messageApi.success(`已解析 ${data.bindingCount} 个绑定`); });
  const addBinding = () => { if(!bindingTarget || !bindingKey.trim()) return; setBindings(x=>({...x,[bindingTarget]:`${bindingScope}.${bindingKey.trim()}`})); };

  const createProduct = () => invoke(async () => {
    const response = await fetch('/api/products', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ id:productId.trim(), name:productName.trim(), description:'' }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.detail ?? data.error ?? '创建产品失败');
    setSelectedProductId(data.id); messageApi.success('产品已创建');
  });
  const createRecipe = () => invoke(async () => {
    if (!selectedProductId) throw new Error('请先选择或创建产品。');
    const response = await fetch(`/api/products/${encodeURIComponent(selectedProductId)}/recipes`, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ id:jobId.trim(), recipeCode:recipeCode.trim(), name:name.trim(), description, workflow, changeNote:'Initial recipe version' }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.detail ?? data.error ?? '创建配方失败'); setSelectedId(data.id); messageApi.success('配方已创建为不可变的 V1 版本');
  });
  const saveVersion = () => selected && invoke(async () => {
    const response = await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions`, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ workflow, note, parameterBindings:bindings }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.detail ?? data.error ?? '保存版本失败'); messageApi.success(`草稿 V${data.version} 已创建`);
  });
  const loadCandidates = (version:number) => selected && invoke(async () => {
    const response = await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}/validation-candidates`);
    const data: ValidationCandidateInfo[] = await response.json();
    if (!response.ok) throw new Error('查询验证记录失败');
    setCandidates(data); setCandidatesVersion(version);
    if (!data.length) messageApi.info('没有与该版本工作流哈希匹配的已完成数据集验证记录');
  });
  const linkCandidate = (version:number, validationRunId:string) => selected && invoke(async () => {
    const response = await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}/validation`, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ validationRunId }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.detail ?? data.error ?? '关联验证记录失败');
    if (!data.accepted) throw new Error(`验证策略未通过：${data.reason}`); messageApi.success(`V${version} 已通过验证 · ${validationRunId.slice(0,12)}`);
  });
  const doPublish = (version:number, rollback=false) => selected && invoke(async () => {
    const url = rollback ? `/api/jobs/${encodeURIComponent(selected.id)}/rollback/${version}` : `/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}/publish`;
    const response=await fetch(url,{method:'POST'}); const data=await response.json(); if(!response.ok) throw new Error(data.detail ?? data.error ?? '发布失败'); messageApi.success(`${rollback?'已回滚':'已发布'} → V${version}`);
  });
  // 发布/回滚前先展示与上一版本的差异和验证结论，避免盲发
  const requestPublish = async (version:number, rollback=false) => {
    if (!selected) return;
    setBusy(true);
    let diffText = '';
    let diffLoaded = false;
    try {
      const from = Math.max(1, version - 1);
      const response = await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${from}/diff/${version}`);
      if (response.ok) {
        const data: JobVersionDiff = await response.json();
        diffLoaded = true;
        diffText = data.changes.length
          ? data.changes.map((x) => `${x.kind}  ${x.path}\n  ${x.before ?? '∅'} → ${x.after ?? '∅'}`).join('\n\n')
          : '没有影响执行语义的变更。';
      }
    } catch { /* 差异为可选信息，取不到不影响发布 */ }
    finally { setBusy(false); }
    const info = selected.versions.find((x) => x.version === version);
    const validationText = info?.validation
      ? `${info.validation.accepted ? '通过' : '未通过'}${info.validation.summary ? ` · 准确率 ${(info.validation.summary.accuracy * 100).toFixed(2)}%` : ''}`
      : '未关联验证记录';
    Modal.confirm({
      title: `${rollback ? '回滚' : '发布'} ${selected.id} V${version}？`,
      width: 720,
      okText: rollback ? '确认回滚' : '确认发布',
      okButtonProps: rollback ? { danger: true } : undefined,
      content: (
        <div>
          <div><Typography.Text strong>验证结论：</Typography.Text>{validationText}</div>
          <div style={{ marginTop: 8 }}><Typography.Text strong>{diffLoaded ? `与 V${Math.max(1, version - 1)} 的差异：` : '版本差异：未获取到'}</Typography.Text></div>
          <pre className="job-diff-pre">{diffLoaded ? diffText : '—'}</pre>
        </div>
      ),
      onOk: async () => { await doPublish(version, rollback); }
    });
  };
  const loadVersion = (version:number) => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}`); const data:JobVersionSnapshot & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'加载失败'); onLoadWorkflow(data.workflow); setBindings(data.parameterBindings ?? {}); setResolved(undefined); messageApi.success(`已加载 ${selected.id} V${version} 的有效工作流，可用于验证和调试`); });
  const loadBaseVersion = (version:number) => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}`); const data:JobVersionSnapshot & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'加载失败'); onLoadWorkflow(data.baseWorkflow ?? data.workflow); setBindings(data.parameterBindings ?? {}); setResolved(undefined); messageApi.success(`已加载 ${selected.id} V${version} 的基础工作流，可编辑参数`); });
  const validateDependencies = () => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/dependencies/validate`); const data:RuntimeDependencyValidation & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'依赖校验失败'); setDependency(data); if(!data.compatible) throw new Error(data.drifts[0]?.message??'运行时依赖发生变化'); messageApi.success('运行时依赖一致'); });
  const runPublished = () => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/run`,{method:'POST'}); const data:RunResult=await response.json(); onRunResult(data); if(!response.ok) throw new Error(data.error??'已发布版本运行失败'); messageApi.success('当前生效配方已运行'); });
  const cloneRecipe = () => selected && invoke(async () => {
    if (!selected.productId) throw new Error('旧版作业需要先在产品下重新创建。');
    const newId = window.prompt('新配方 ID', `${selected.id}-copy`); if (!newId) return;
    const newCode = window.prompt('新配方编码', `${selected.recipeCode ?? 'RECIPE'}-COPY`); if (!newCode) return;
    const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/clone`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({newId,productId:selected.productId,recipeCode:newCode,name:`${selected.name} Copy`})});
    const data=await response.json(); if(!response.ok) throw new Error(data.detail??data.error??'复制配方失败'); setSelectedId(data.id); messageApi.success('配方已复制');
  });
  const showDiff = (from:number,to:number) => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${from}/diff/${to}`); const data:JobVersionDiff & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'版本差异比较失败'); Modal.info({title:`${selected.id} V${from} → V${to}`,width:900,content:<pre className="job-diff-pre">{data.changes.length?data.changes.map(x=>`${x.kind}  ${x.path}\n  ${x.before??'∅'} → ${x.after??'∅'}`).join('\n\n'):'没有影响执行语义的变更。'}</pre>}); });

  const lifecycleTag = (job: JobDescriptor) => {
    const latest = job.versions.find((v) => v.version === job.latestVersion);
    if (job.publishedVersion === job.latestVersion) return <Tag color="green">生效 V{job.latestVersion}</Tag>;
    if (latest?.validation?.accepted) return <Tag color="blue">已验证</Tag>;
    return <Tag>草稿</Tag>;
  };

  // 左列导航：产品 → 配方（旧版作业单独一组）
  const navItems: MenuProps['items'] = [
    ...products.map((product) => ({
      key: `p:${product.id}`,
      label: <span className="job-nav-product">{product.name}<span className="job-nav-count">{product.recipes.length}</span></span>,
      children: product.recipes.length
        ? product.recipes.map((recipe) => {
          const job = jobs.find((x) => x.id === recipe.id);
          return { key: recipe.id, label: <span className="job-nav-recipe">{recipe.name}{job ? lifecycleTag(job) : null}</span> };
        })
        : [{ key: `p:${product.id}:empty`, label: <span className="job-nav-empty">该产品暂无配方</span>, disabled: true }]
    })),
    ...(legacyJobs.length ? [{ type: 'divider' as const }, {
      key: 'legacy',
      label: <span className="job-nav-product">旧版作业<span className="job-nav-count">{legacyJobs.length}</span></span>,
      children: legacyJobs.map((job) => ({ key: job.id, label: <span className="job-nav-recipe">{job.name}</span> }))
    }] : [])
  ];

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
    { key: 'rollback', label: '回滚到此版本', danger: true, disabled: published || (!!selected?.productId && !validationAccepted), onClick: () => void requestPublish(version, true) },
    { key: 'diff', label: '与上一版本对比', disabled: version <= 1, onClick: () => showDiff(version - 1, version) }
  ];

  const headingMenu: MenuProps['items'] = [
    { key: 'clone', label: '复制配方', onClick: cloneRecipe },
    { key: 'load-effective', label: `载入当前生效版本${selected?.publishedVersion ? ` V${selected.publishedVersion}` : ''}`, disabled: !selected?.publishedVersion, onClick: () => selected?.publishedVersion && loadVersion(selected.publishedVersion) },
    { key: 'diff-latest', label: '对比最近两个版本', disabled: !selected || selected.versions.length < 2, onClick: () => selected && showDiff(Math.max(1, selected.latestVersion - 1), selected.latestVersion) }
  ];

  return <Modal open={open} width={1380} title="产品 / 配方参数 · V0.51" footer={null} onCancel={onClose} destroyOnHidden>
    {contextHolder}
    <Alert style={{marginBottom:12}} type="info" showIcon message="一套标准工作流 + 产品 / 配方参数集" description="参数草稿可随时编辑。保存草稿版本时会将绑定映射和参数快照固化到不可变的有效工作流中。验证和生产运行只使用该固化版本。" />
    <div className="job-grid">
      {/* 左列：产品 → 配方 导航 + 新建入口 */}
      <section className="job-nav-column">
        <Typography.Title level={5} style={{ marginTop: 0 }}>产品 / 配方</Typography.Title>
        <Menu
          mode="inline"
          className="job-nav"
          selectedKeys={selectedId ? [selectedId] : []}
          defaultOpenKeys={selectedProductId ? [`p:${selectedProductId}`] : []}
          items={navItems}
          onSelect={({ key }) => {
            if (key.startsWith('p:') || key === 'legacy') return;
            const job = jobs.find((x) => x.id === key);
            if (!job) return;
            setSelectedId(job.id);
            if (job.productId) setSelectedProductId(job.productId);
          }}
          onOpenChange={(keys) => {
            const last = keys[keys.length - 1];
            if (last?.startsWith('p:')) setSelectedProductId(last.slice(2));
          }}
        />
        <Collapse
          ghost
          className="job-create-collapse"
          items={[
            {
              key: 'product',
              label: '新建产品',
              children: (
                <Space direction="vertical" size={6} style={{ width: '100%' }}>
                  <Input value={productId} onChange={e=>setProductId(e.target.value)} addonBefore="产品 ID" />
                  <Input value={productName} onChange={e=>setProductName(e.target.value)} addonBefore="名称" />
                  <Button block onClick={createProduct} loading={busy}>创建产品</Button>
                </Space>
              )
            },
            {
              key: 'recipe',
              label: '从设计器创建配方',
              children: (
                <div className="job-create-grid">
                  <Input value={jobId} onChange={e=>setJobId(e.target.value)} addonBefore="配方 ID" />
                  <Input value={recipeCode} onChange={e=>setRecipeCode(e.target.value)} addonBefore="编码" />
                  <Input value={name} onChange={e=>setName(e.target.value)} addonBefore="名称" />
                  <Input value={description} onChange={e=>setDescription(e.target.value)} addonBefore="说明" />
                  <Button type="primary" disabled={!selectedProductId} onClick={createRecipe} loading={busy}>创建配方</Button>
                </div>
              )
            }
          ]}
        />
      </section>

      {/* 右列：草稿/生效状态 + 参数 / 版本 / 验证 / 发布记录 */}
      <section className="job-detail">
        {!selected?<div className="job-empty">请在左侧选择配方</div>:<>
          <div className="job-heading">
            <div>
              <strong>{selected.name}</strong>
              <div>{selected.productId?`${selected.productId} / ${selected.recipeCode}`:'旧版作业'}</div>
              <Space wrap size={6} style={{ marginTop: 6 }}>
                <Tag color={selected.publishedVersion ? 'green' : 'default'}>当前生效 {selected.publishedVersion ? `V${selected.publishedVersion}` : '无'}</Tag>
                <Tag color="gold">当前草稿 V{selected.latestVersion}</Tag>
                {selected.publishedVersion !== selected.latestVersion && <Tag color="orange">草稿尚未生效</Tag>}
              </Space>
            </div>
            <Space wrap>
              <Button type="primary" onClick={saveVersion} loading={busy}>保存草稿版本</Button>
              <Button onClick={runPublished} disabled={!selected.publishedVersion||!selected.publishedDependencyManifestHash}>运行当前版本</Button>
              <Dropdown menu={{ items: headingMenu }} trigger={['click']}><Button>更多 ▾</Button></Dropdown>
            </Space>
          </div>
          <Input value={note} onChange={e=>setNote(e.target.value)} addonBefore="变更说明" style={{ marginBottom: 10 }} />

          <Tabs
            activeKey={tab}
            onChange={setTab}
            items={[
              {
                key: 'parameters',
                label: '参数',
                children: (
                  <Space direction="vertical" size="middle" style={{ width: '100%' }}>
                    <Alert type="warning" showIcon message="草稿参数不会直接修改已发布版本" description="先保存参数草稿，再将节点参数映射到 product.* / recipe.*，解析预览后保存草稿版本。不可变版本会固化具体参数值和有效工作流哈希。" />
                    <div className="job-draft-grid">
                      <div>
                        <ParameterDraft title="产品参数草稿" text={productParams} onChange={setProductParams} disabled={busy || !selectedProductId} />
                        <Button style={{marginTop:6}} disabled={!selectedProductId} onClick={saveProductParameters}>保存产品参数</Button>
                      </div>
                      <div>
                        <ParameterDraft title="配方参数草稿" text={recipeParams} onChange={setRecipeParams} disabled={busy} />
                        <Button style={{marginTop:6}} onClick={saveRecipeParameters}>保存配方参数</Button>
                      </div>
                    </div>
                    <Divider titlePlacement="left" style={{ margin: 0 }}>配方参数绑定</Divider>
                    <Space wrap>
                      <Select showSearch placeholder="节点参数" style={{width:280}} value={bindingTarget} onChange={setBindingTarget} options={bindingTargets}/><Select style={{width:110}} value={bindingScope} onChange={setBindingScope} options={[{value:'product',label:'产品'},{value:'recipe',label:'配方'}]}/><Input style={{width:190}} value={bindingKey} onChange={e=>setBindingKey(e.target.value)} placeholder="参数键"/><Button onClick={addBinding}>添加绑定</Button><Button type="primary" onClick={resolveBindings}>解析预览</Button>{resolved&&<Button onClick={()=>onLoadWorkflow(resolved.effectiveWorkflow)}>加载有效版本到设计器</Button>}
                    </Space>
                    <Table size="small" pagination={false} rowKey="target" dataSource={bindingsRows} locale={{ emptyText: '尚未配置绑定' }} columns={[
                      {title:'工作流参数',dataIndex:'target'},
                      {title:'参数来源',dataIndex:'source',render:(v:string)=><Tag color={v.startsWith('product.')?'geekblue':'purple'}>{v}</Tag>},
                      {title:'解析后的有效值',render:(_,r)=><code className="job-draft-nested">{r.resolved === undefined ? '—' : JSON.stringify(r.resolved)}</code>},
                      {title:'',width:80,render:(_,r)=><Button size="small" danger onClick={()=>setBindings(x=>{const n={...x};delete n[r.target];return n})}>移除</Button>}
                    ]} />
                    {resolved&&<Alert type="success" showIcon message={`Resolved effective hash ${resolved.effectiveWorkflowHash.slice(0,16)}…`} description={<span>Base {resolved.baseWorkflowHash.slice(0,12)}… · {resolved.bindingCount} bindings · Frozen snapshot: <code>{JSON.stringify(resolved.snapshot)}</code></span>}/>}
                  </Space>
                )
              },
              {
                key: 'versions',
                label: '版本',
                children: (
                  <Table size="small" pagination={false} rowKey="version" dataSource={versions} locale={{ emptyText: '暂无版本' }} columns={[
                    {title:'版本',width:110,render:(_,r)=>r.published?<Tag color="green">V{r.version} 生效</Tag>:r.version===selected.latestVersion?<Tag color="gold">V{r.version} 草稿</Tag>:<Tag>V{r.version}</Tag>},
                    {title:'生命周期',width:110,render:(_,r)=>r.published?<Tag color="green">已发布</Tag>:r.validation?.accepted?<Tag color="blue">已验证</Tag>:r.validation?<Tag color="red">未通过</Tag>:<Tag>草稿</Tag>},
                    {title:'验证',width:190,render:(_,r)=>r.validation?<span>{r.validation.accepted?'通过':'失败'}{r.validation.summary?` · ${(r.validation.summary.accuracy*100).toFixed(2)}%`:''}</span>:'未关联'},
                    {title:'创建时间',dataIndex:'createdAt',width:165,render:(v:string)=>new Date(v).toLocaleString('zh-CN')},
                    {title:'绑定数',width:80,render:(_,r)=>r.bindingCount?<Tag color="purple">{r.bindingCount}</Tag>:'—'},
                    {title:'变更说明',dataIndex:'note'},
                    {title:'操作',width:210,render:(_,r)=><Space size={4}>
                      <Button size="small" onClick={()=>{setTab('validation'); loadCandidates(r.version);}} disabled={!!r.validation?.accepted}>验证</Button>
                      <Button size="small" type="primary" onClick={()=>void requestPublish(r.version)} disabled={r.published||!!selected.productId&&!r.validation?.accepted}>发布</Button>
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
  </Modal>;
}
