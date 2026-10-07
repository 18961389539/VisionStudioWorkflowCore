import { useEffect, useMemo, useState } from 'react';
import { Alert, Button, Divider, Input, Modal, Select, Space, Table, Tag, Typography, message } from 'antd';
import type { JobDescriptor, JobVersionDiff, JobVersionSnapshot, ParameterValueSet, ProductDescriptor, ResolvedRecipeParameterization, RunResult, RuntimeDependencyValidation, ValidationCandidateInfo, WorkflowPayload } from '../types';

type Props = { open: boolean; onClose: () => void; workflow: WorkflowPayload; onLoadWorkflow: (workflow: WorkflowPayload) => void; onRunResult: (result: RunResult) => void; };

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
  const [messageApi, contextHolder] = message.useMessage();

  const selected = useMemo(() => jobs.find((x) => x.id === selectedId), [jobs, selectedId]);
  const visibleJobs = useMemo(() => selectedProductId ? jobs.filter(x => x.productId === selectedProductId) : jobs, [jobs, selectedProductId]);
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
  const approveFromValidation = (version:number) => selected && invoke(async () => {
    const candidatesRes = await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}/validation-candidates`);
    const candidates: ValidationCandidateInfo[] = await candidatesRes.json();
    if (!candidatesRes.ok || !candidates.length) throw new Error('没有与此不可变工作流哈希匹配的已完成数据集验证记录。请先运行数据集验证。');
    const best = candidates[0];
    const response = await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}/validation`, { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({ validationRunId:best.validationRunId }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.detail ?? data.error ?? '关联验证记录失败');
    if (!data.accepted) throw new Error(`验证策略未通过：${data.reason}`); messageApi.success(`V${version} 已通过验证 · ${best.validationRunId.slice(0,12)}`);
  });
  const publish = (version:number, rollback=false) => selected && invoke(async () => {
    const url = rollback ? `/api/jobs/${encodeURIComponent(selected.id)}/rollback/${version}` : `/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}/publish`;
    const response=await fetch(url,{method:'POST'}); const data=await response.json(); if(!response.ok) throw new Error(data.detail ?? data.error ?? '发布失败'); messageApi.success(`${rollback?'已回滚':'已发布'} → V${version}`);
  });
  const loadVersion = (version:number) => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}`); const data:JobVersionSnapshot & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'加载失败'); onLoadWorkflow(data.workflow); setBindings(data.parameterBindings ?? {}); setResolved(undefined); messageApi.success(`已加载 ${selected.id} V${version} 的有效工作流，可用于验证和调试`); });
  const loadBaseVersion = (version:number) => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${version}`); const data:JobVersionSnapshot & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'加载失败'); onLoadWorkflow(data.baseWorkflow ?? data.workflow); setBindings(data.parameterBindings ?? {}); setResolved(undefined); messageApi.success(`已加载 ${selected.id} V${version} 的基础工作流，可编辑参数`); });
  const validateDependencies = () => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/dependencies/validate`); const data:RuntimeDependencyValidation & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'依赖校验失败'); if(!data.compatible) throw new Error(data.drifts[0]?.message??'运行时依赖发生变化'); messageApi.success('运行时依赖一致'); });
  const runPublished = () => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/run`,{method:'POST'}); const data:RunResult=await response.json(); onRunResult(data); if(!response.ok) throw new Error(data.error??'已发布版本运行失败'); messageApi.success('当前生效配方已运行'); });
  const cloneRecipe = () => selected && invoke(async () => {
    if (!selected.productId) throw new Error('旧版作业需要先在产品下重新创建。');
    const newId = window.prompt('新配方 ID', `${selected.id}-copy`); if (!newId) return;
    const newCode = window.prompt('新配方编码', `${selected.recipeCode ?? 'RECIPE'}-COPY`); if (!newCode) return;
    const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/clone`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({newId,productId:selected.productId,recipeCode:newCode,name:`${selected.name} Copy`})});
    const data=await response.json(); if(!response.ok) throw new Error(data.detail??data.error??'复制配方失败'); setSelectedId(data.id); messageApi.success('配方已复制');
  });
  const showDiff = (from:number,to:number) => selected && invoke(async () => { const response=await fetch(`/api/jobs/${encodeURIComponent(selected.id)}/versions/${from}/diff/${to}`); const data:JobVersionDiff & {detail?:string}=await response.json(); if(!response.ok) throw new Error(data.detail??'版本差异比较失败'); Modal.info({title:`${selected.id} V${from} → V${to}`,width:900,content:<pre style={{maxHeight:480,overflow:'auto',whiteSpace:'pre-wrap'}}>{data.changes.length?data.changes.map(x=>`${x.kind}  ${x.path}\n  ${x.before??'∅'} → ${x.after??'∅'}`).join('\n\n'):'没有影响执行语义的变更。'}</pre>}); });

  return <Modal open={open} width={1380} title="产品 / 配方参数 · V0.51" footer={null} onCancel={onClose} destroyOnHidden>
    {contextHolder}
    <Alert style={{marginBottom:12}} type="info" showIcon message="一套标准工作流 + 产品 / 配方参数集" description="参数草稿可随时编辑。保存草稿版本时会将绑定映射和参数快照固化到不可变的有效工作流中。验证和生产运行只使用该固化版本。" />
    <div className="job-grid">
      <section>
        <Typography.Title level={5}>产品 / 配方</Typography.Title>
        <Space.Compact block style={{marginBottom:8}}><Input value={productId} onChange={e=>setProductId(e.target.value)} addonBefore="产品 ID"/><Input value={productName} onChange={e=>setProductName(e.target.value)} addonBefore="名称"/><Button onClick={createProduct} loading={busy}>创建产品</Button></Space.Compact>
        <Select allowClear placeholder="全部 / 旧版作业" style={{width:'100%',marginBottom:8}} value={selectedProductId} onChange={setSelectedProductId} options={products.map(p=>({value:p.id,label:`${p.name} · ${p.recipes.length} 个配方`}))}/>
        <Table size="small" pagination={false} rowKey="id" dataSource={visibleJobs} onRow={row=>({onClick:()=>setSelectedId(row.id)})} rowClassName={row=>row.id===selectedId?'job-selected-row':''} columns={[
          {title:'配方',render:(_,r)=><><strong>{r.name}</strong><div style={{fontSize:11,opacity:.65}}>{r.recipeCode??'旧版'} · {r.id}</div></>},
          {title:'状态',width:105,render:(_,r)=>{const latest=r.versions.find(v=>v.version===r.latestVersion); return r.publishedVersion===r.latestVersion?<Tag color="green">生效 V{r.latestVersion}</Tag>:latest?.validation?.accepted?<Tag color="blue">已验证</Tag>:<Tag>草稿</Tag>}}
        ]}/>
        <Divider titlePlacement="left">从设计器创建配方</Divider>
        <div className="job-create-grid"><Input value={jobId} onChange={e=>setJobId(e.target.value)} addonBefore="配方 ID"/><Input value={recipeCode} onChange={e=>setRecipeCode(e.target.value)} addonBefore="编码"/><Input value={name} onChange={e=>setName(e.target.value)} addonBefore="名称"/><Input value={description} onChange={e=>setDescription(e.target.value)} addonBefore="说明"/><Button type="primary" disabled={!selectedProductId} onClick={createRecipe} loading={busy}>创建配方</Button></div>
      </section>
      <section className="job-detail">
        {!selected?<div className="job-empty">请选择配方</div>:<>
          <div className="job-heading"><div><strong>{selected.name}</strong><div>{selected.productId?`${selected.productId} / ${selected.recipeCode}`:'旧版作业'} · 当前生效 {selected.publishedVersion?`V${selected.publishedVersion}`:'无'}</div></div><Space wrap><Button onClick={cloneRecipe}>复制</Button><Button onClick={validateDependencies} disabled={!selected.publishedVersion||!selected.publishedDependencyManifestHash}>校验依赖</Button><Button onClick={runPublished} disabled={!selected.publishedVersion||!selected.publishedDependencyManifestHash}>运行当前版本</Button><Button type="primary" onClick={saveVersion} loading={busy}>保存草稿版本</Button></Space></div>
          <Input value={note} onChange={e=>setNote(e.target.value)} addonBefore="变更说明"/>
          <Table size="small" pagination={false} rowKey="version" dataSource={[...selected.versions].reverse()} columns={[
            {title:'版本',width:90,render:(_,r)=>r.published?<Tag color="green">V{r.version} 生效</Tag>:`V${r.version}`},
            {title:'生命周期',width:115,render:(_,r)=>r.published?<Tag color="green">已发布</Tag>:r.validation?.accepted?<Tag color="blue">已验证</Tag>:r.validation?<Tag color="red">未通过</Tag>:<Tag>草稿</Tag>},
            {title:'验证',width:190,render:(_,r)=>r.validation?<span>{r.validation.accepted?'通过':'失败'}{r.validation.summary?` · ${(r.validation.summary.accuracy*100).toFixed(2)}%`:''}</span>:'未关联'},
            {title:'创建时间',dataIndex:'createdAt',width:165,render:(v:string)=>new Date(v).toLocaleString('zh-CN')},
            {title:'绑定数',width:80,render:(_,r)=>r.bindingCount?<Tag color="purple">{r.bindingCount}</Tag>:'—'},
            {title:'变更说明',dataIndex:'note'},
            {title:'操作',width:370,render:(_,r)=><Space size={4}><Button size="small" onClick={()=>loadBaseVersion(r.version)}>基础版</Button><Button size="small" onClick={()=>loadVersion(r.version)}>有效版</Button><Button size="small" onClick={()=>approveFromValidation(r.version)} disabled={r.validation?.accepted}>验证</Button><Button size="small" type="primary" onClick={()=>publish(r.version)} disabled={r.published||!!selected.productId&&!r.validation?.accepted}>发布</Button><Button size="small" danger onClick={()=>publish(r.version,true)} disabled={r.published||!!selected.productId&&!r.validation?.accepted}>回滚</Button>{r.version>1&&<Button size="small" onClick={()=>showDiff(r.version-1,r.version)}>对比</Button>}</Space>}
          ]}/>
          <Divider titlePlacement="left">配方参数绑定</Divider>
          <Alert type="warning" showIcon style={{marginBottom:10}} message="草稿参数不会直接修改已发布版本" description="先保存参数草稿，再将节点参数映射到 product.* / recipe.*，解析预览后保存草稿版本。不可变版本会固化具体参数值和有效工作流哈希。"/>
          <div style={{display:'grid',gridTemplateColumns:'1fr 1fr',gap:12}}>
            <div><Typography.Text strong>产品参数草稿</Typography.Text><Input.TextArea rows={6} value={productParams} onChange={e=>setProductParams(e.target.value)} style={{fontFamily:'monospace',marginTop:6}}/><Button style={{marginTop:6}} disabled={!selectedProductId} onClick={saveProductParameters}>保存产品参数</Button></div>
            <div><Typography.Text strong>配方参数草稿</Typography.Text><Input.TextArea rows={6} value={recipeParams} onChange={e=>setRecipeParams(e.target.value)} style={{fontFamily:'monospace',marginTop:6}}/><Button style={{marginTop:6}} onClick={saveRecipeParameters}>保存配方参数</Button></div>
          </div>
          <Space wrap style={{marginTop:12}}>
            <Select showSearch placeholder="节点参数" style={{width:280}} value={bindingTarget} onChange={setBindingTarget} options={bindingTargets}/><Select style={{width:110}} value={bindingScope} onChange={setBindingScope} options={[{value:'product',label:'产品'},{value:'recipe',label:'配方'}]}/><Input style={{width:190}} value={bindingKey} onChange={e=>setBindingKey(e.target.value)} placeholder="参数键"/><Button onClick={addBinding}>添加绑定</Button><Button type="primary" onClick={resolveBindings}>解析预览</Button>{resolved&&<Button onClick={()=>onLoadWorkflow(resolved.effectiveWorkflow)}>加载有效版本到设计器</Button>}
          </Space>
          <Table size="small" style={{marginTop:8}} pagination={false} rowKey="target" dataSource={Object.entries(bindings).map(([target,source])=>({target,source}))} columns={[{title:'工作流参数',dataIndex:'target'},{title:'参数来源',dataIndex:'source',render:(v:string)=><Tag color={v.startsWith('product.')?'geekblue':'purple'}>{v}</Tag>},{title:'',width:80,render:(_,r)=><Button size="small" danger onClick={()=>setBindings(x=>{const n={...x};delete n[r.target];return n})}>移除</Button>}]} />
          {resolved&&<Alert style={{marginTop:8}} type="success" showIcon message={`Resolved effective hash ${resolved.effectiveWorkflowHash.slice(0,16)}…`} description={<span>Base {resolved.baseWorkflowHash.slice(0,12)}… · {resolved.bindingCount} bindings · Frozen snapshot: <code>{JSON.stringify(resolved.snapshot)}</code></span>}/>}
          <Divider titlePlacement="left">发布历史</Divider><div className="publication-history">{[...selected.publicationHistory].reverse().map((e,i)=><Tag key={`${e.at}-${i}`} color={e.action==='Rollback'?'orange':'blue'}>{e.action==='Rollback'?'回滚':'发布'} V{e.version} · {new Date(e.at).toLocaleString('zh-CN')}</Tag>)}</div>
        </>}
      </section>
    </div>
  </Modal>;
}
