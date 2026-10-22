import { useEffect, useMemo, useRef, useState } from 'react';
import { Button, Drawer, Form, Input, InputNumber, message, Select, Space, Table, Tag, Typography } from 'antd';
import { responseErrorMessage } from '../apiErrors';
import type { PrepareWorkflowChange, PreparedWorkflowChange } from '../useWorkflowChanges';
import type { ExtractWorkflowModuleResult, WorkflowModuleDescriptor, WorkflowModuleVersion, WorkflowPayload } from '../types';
import { apiFetch } from '../apiClient';
import { apiUrl, modules as moduleEndpoints } from '../endpoints';

type Props = {
  open: boolean;
  onClose: () => void;
  currentWorkflow: WorkflowPayload;
  selectedNodeId?: string;
  selectedNodeType?: string;
  prepareWorkflowChange: PrepareWorkflowChange;
  onInsertModule: (version: WorkflowModuleVersion) => void;
  onUpgradeSelected: (version: WorkflowModuleVersion) => void;
};

export default function WorkflowModulePanel(props: Props) {
  const [modules, setModules] = useState<WorkflowModuleDescriptor[]>([]);
  // R12（2026-10-12 复审）：列表接口只返回摘要（每模块仅最新版本）——完整版本集合按需拉取。
  const [detailCache, setDetailCache] = useState<Record<string, WorkflowModuleDescriptor>>({});
  const [selectedNodes, setSelectedNodes] = useState<string[]>([]);
  const [moduleId, setModuleId] = useState('locate-part');
  const [name, setName] = useState('定位工件');
  const [description, setDescription] = useState('可复用定位子流程');
  const [note, setNote] = useState('初始可复用模块');
  const [activeModuleId, setActiveModuleId] = useState<string>();
  const [activeVersion, setActiveVersion] = useState<number>();
  const [editingModuleId, setEditingModuleId] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [messageApi, contextHolder] = message.useMessage();
  const active = useRef(false);
  const operation = useRef<{ controller: AbortController; change?: PreparedWorkflowChange } | undefined>(undefined);
  const editingOwner = useRef<PreparedWorkflowChange | undefined>(undefined);
  const catalogGeneration = useRef(0);
  const catalogAbort = useRef<AbortController | undefined>(undefined);
  useEffect(() => {
    active.current = props.open;
    setSaving(false);
    return () => {
      active.current = false;
      catalogGeneration.current += 1; catalogAbort.current?.abort();
      operation.current?.controller.abort(); operation.current?.change?.cancel(); operation.current = undefined;
      editingOwner.current?.cancel(); editingOwner.current = undefined;
    };
  }, [props.open]);

  const refresh = async () => {
    const generation = ++catalogGeneration.current;
    catalogAbort.current?.abort();
    const controller = new AbortController(); catalogAbort.current = controller;
    const timeout = setTimeout(() => controller.abort(), 15_000);
    try {
      const response = await apiFetch(apiUrl('modules'), { signal: controller.signal });
      if (!response.ok) throw new Error(await responseErrorMessage(response, '模块列表加载失败'));
      const data: WorkflowModuleDescriptor[] = await response.json();
      if (!active.current || generation !== catalogGeneration.current || controller.signal.aborted) return;
      if (!Array.isArray(data)) throw new Error('模块列表响应无效');
      setModules(data); setDetailCache({});
      setActiveModuleId((current) => data.some((item) => item.id === current) ? current : data[0]?.id);
      if (!activeModuleId && data.length) setActiveVersion(data[0].latestVersion);
    } catch (error) {
      if (active.current && generation === catalogGeneration.current) void messageApi.error(`模块列表刷新失败：${controller.signal.aborted ? '读取超时' : error instanceof Error ? error.message : '网络不可达'}`);
    } finally { clearTimeout(timeout); }
  };

  useEffect(() => { if (props.open) { refresh(); if (props.selectedNodeId) setSelectedNodes([props.selectedNodeId]); } }, [props.open]);

  // R12：选中模块后按需拉取详情（完整版本集合、端口与哈希）；摘要先渲染（仅最新版本），
  // 详情到达后无缝替换——列表接口因此不必为每个模块反序列化全部历史版本。
  useEffect(() => {
    if (!props.open || !activeModuleId || detailCache[activeModuleId]) return;
    let cancelled = false;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 15_000);
    void (async () => {
      try {
        const response = await apiFetch(moduleEndpoints.byId(activeModuleId), { signal: controller.signal });
        if (!response.ok) return;
        const detail: WorkflowModuleDescriptor = await response.json();
        if (!cancelled && !controller.signal.aborted && detail?.id === activeModuleId && Array.isArray(detail.versions)) setDetailCache((current) => ({ ...current, [activeModuleId]: detail }));
      } catch { /* 详情加载失败：退回摘要（仅最新版本），下次打开/切换重试 */ }
      finally { clearTimeout(timeout); }
    })();
    return () => { cancelled = true; controller.abort(); clearTimeout(timeout); };
  }, [props.open, activeModuleId, detailCache]);

  const activeModule = (activeModuleId ? detailCache[activeModuleId] : undefined) ?? modules.find((x) => x.id === activeModuleId);
  const version = activeModule?.versions.find((x) => x.version === activeVersion) ?? activeModule?.versions[0];
  const selectedCallParameters = useMemo(() => {
    const selected = props.currentWorkflow.nodes.find((x) => x.id === props.selectedNodeId);
    return selected?.type === 'module.call' ? selected.parameters : undefined;
  }, [props.currentWorkflow, props.selectedNodeId]);

  const begin = (change?: PreparedWorkflowChange) => {
    if (!active.current || operation.current) return undefined;
    const value = { controller: new AbortController(), change };
    operation.current = value; setSaving(true);
    return value;
  };
  const finish = (value: NonNullable<typeof operation.current>) => {
    value.change?.cancel();
    if (operation.current === value) { operation.current = undefined; if (active.current) setSaving(false); }
  };
  const openVersion = async (value: WorkflowModuleVersion) => {
    const action = begin(props.prepareWorkflowChange('打开模块内部工作流'));
    if (!action) return;
    try {
      const applied = await action.change!.apply(value.workflow);
      if (!active.current || operation.current !== action || !applied) return;
      editingOwner.current?.cancel();
      editingOwner.current = props.prepareWorkflowChange('保存模块版本');
      setEditingModuleId(value.moduleId); setNote(`从 V${value.version} 更新 ${value.moduleId}`);
      void messageApi.success('已将模块内部节点加载到设计器');
    } finally { finish(action); }
  };
  const extract = async () => {
    if (!selectedNodes.length) return messageApi.warning('请先选择要提取的节点');
    const action = begin(props.prepareWorkflowChange('提取模块并替换节点', 'edit'));
    if (!action) return;
    let rejected = false;
    const timeout = setTimeout(() => action.controller.abort(), 20_000);
    try {
      const response = await apiFetch(apiUrl('modules/extract'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: action.controller.signal,
        body: JSON.stringify({ workflow: props.currentWorkflow, selectedNodeIds: selectedNodes, id: moduleId, name, description, note })
      });
      if (!active.current || operation.current !== action) return;
      if (!response.ok) { rejected = response.status < 500; throw new Error(await responseErrorMessage(response, '提取模块失败')); }
      const result = await response.json() as ExtractWorkflowModuleResult;
      if (!active.current || operation.current !== action || action.controller.signal.aborted) return;
      if (result?.module?.id !== moduleId || result.version?.moduleId !== moduleId || !Number.isInteger(result.version.version)) throw new Error('模块提取响应与提交对象不匹配');
      const applied = await action.change!.apply(result.replacementWorkflow);
      if (!active.current || operation.current !== action) return;
      setActiveModuleId(result.module.id); setActiveVersion(result.version.version);
      await refresh();
      if (!active.current || operation.current !== action) return;
      if (applied) void messageApi.success(`已创建 ${result.module.name} V${result.version.version} 并替换所选节点`);
      else void messageApi.warning('模块已创建，但设计器未应用替换；当前编辑已保留，可从模块库查看新模块。');
    } catch (error) {
      if (active.current && operation.current === action) void messageApi.error(`${rejected ? '提取失败：' : '提取结果未确认，请核对模块库后再操作。'}${error instanceof Error ? error.message : ''}`);
    } finally { clearTimeout(timeout); finish(action); }
  };
  const saveVersion = async () => {
    if (!editingModuleId || !editingOwner.current?.isSameDocument()) return messageApi.warning('设计器文档已切换，请先重新打开要编辑的模块版本');
    const action = begin();
    if (!action) return;
    let rejected = false;
    const timeout = setTimeout(() => action.controller.abort(), 20_000);
    try {
      const response = await apiFetch(moduleEndpoints.versions(editingModuleId), {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: action.controller.signal,
        body: JSON.stringify({ workflow: props.currentWorkflow, note })
      });
      if (!active.current || operation.current !== action) return;
      if (!response.ok) { rejected = response.status < 500; throw new Error(await responseErrorMessage(response, '保存模块版本失败')); }
      const data = await response.json() as WorkflowModuleVersion;
      if (!active.current || operation.current !== action || action.controller.signal.aborted) return;
      if (data?.moduleId !== editingModuleId || !Number.isInteger(data.version)) throw new Error('模块保存响应与提交对象不匹配');
      await refresh();
      if (!active.current || operation.current !== action) return;
      setActiveModuleId(editingModuleId); setActiveVersion(data.version);
      void messageApi.success(`已保存 ${editingModuleId} V${data.version}`);
    } catch (error) {
      if (active.current && operation.current === action) void messageApi.error(`${rejected ? '模块版本保存失败：' : '模块版本保存未确认，请核对版本列表后再操作。'}${error instanceof Error ? error.message : ''}`);
    } finally { clearTimeout(timeout); finish(action); }
  };

  return <Drawer open={props.open} onClose={props.onClose} width={1080} title="可复用模块 · V0.52">
    {contextHolder}
    <Typography.Paragraph type="secondary">
      将稳定的节点组封装为带类型端口的模块。模块调用会固定到不可变的版本和哈希；修改模块不会自动升级现有配方。
    </Typography.Paragraph>
    <div className="module-grid">
      <section className="module-card">
        <h3>从当前工作流提取</h3>
        <Form layout="vertical" size="small">
          <Form.Item label="模块 ID"><Input value={moduleId} onChange={(e) => setModuleId(e.target.value)} /></Form.Item>
          <Form.Item label="名称"><Input value={name} onChange={(e) => setName(e.target.value)} /></Form.Item>
          <Form.Item label="说明"><Input value={description} onChange={(e) => setDescription(e.target.value)} /></Form.Item>
          <Form.Item label="变更说明"><Input value={note} onChange={(e) => setNote(e.target.value)} /></Form.Item>
        </Form>
        <Table
          size="small" rowKey="id" pagination={false} dataSource={props.currentWorkflow.nodes}
          rowSelection={{ selectedRowKeys: selectedNodes, onChange: (keys) => setSelectedNodes(keys.map(String)) }}
          columns={[
            { title: '节点', dataIndex: 'name', render: (v: unknown, row: WorkflowPayload['nodes'][number]) => String(v ?? row.id) },
            { title: '类型', dataIndex: 'type', width: 190 },
          ]}
        />
        <Button type="primary" style={{ marginTop: 12 }} loading={saving} onClick={extract}>提取模块并替换所选节点</Button>
      </section>

      <section className="module-card">
        <h3>模块库</h3>
        <Space wrap>
          <Select style={{ width: 240 }} value={activeModuleId} onChange={(id) => { setActiveModuleId(id); const m=modules.find(x=>x.id===id); setActiveVersion(m?.latestVersion); }} options={modules.map(x => ({ value:x.id,label:`${x.name} · ${x.id}` }))} />
          <Select style={{ width: 110 }} value={version?.version} onChange={setActiveVersion} options={(activeModule?.versions ?? []).map(x => ({ value:x.version,label:`V${x.version}` }))} />
          <Button onClick={refresh}>刷新</Button>
        </Space>
        {version ? <>
          <div className="module-summary">
            <Tag color="blue">V{version.version}</Tag><Tag>{version.inputs.length} 个输入</Tag><Tag>{version.outputs.length} 个输出</Tag><Tag color="purple">{version.parameters.length} 个参数</Tag>
            <code title={version.moduleHash}>{version.moduleHash.slice(0, 12)}…</code>
          </div>
          <Typography.Paragraph>{activeModule?.description || '—'}</Typography.Paragraph>
          <Table size="small" pagination={false} rowKey="name" dataSource={version.inputs} columns={[{title:'输入',dataIndex:'name'},{title:'类型',dataIndex:'dataType',width:110},{title:'映射到',render:(_,r)=>`${r.internalNodeId}.${r.internalPort}`}]} />
          <Table size="small" pagination={false} rowKey="name" dataSource={version.outputs} columns={[{title:'输出',dataIndex:'name'},{title:'类型',dataIndex:'dataType',width:110},{title:'映射自',render:(_,r)=>`${r.internalNodeId}.${r.internalPort}`}]} />
          <Space wrap style={{ marginTop: 12 }}>
            <Button type="primary" onClick={() => props.onInsertModule(version)}>插入 V{version.version}</Button>
            <Button disabled={saving} onClick={() => void openVersion(version)}>在设计器中打开模块内部</Button>
            <Button disabled={editingModuleId !== version.moduleId || !editingOwner.current?.isSameDocument()} loading={saving} onClick={saveVersion}>将设计器内容另存为新版本</Button>
            <Button
              disabled={props.selectedNodeType !== 'module.call' || selectedCallParameters?.moduleId !== version.moduleId || Number(selectedCallParameters?.moduleVersion) === version.version}
              onClick={() => props.onUpgradeSelected(version)}
            >将所选模块调用升级到 V{version.version}</Button>
          </Space>
          <div className="module-immutability-note">现有模块调用保持版本固定；升级是一次显式编辑，会改变父工作流的语义哈希。</div>
        </> : <Typography.Text type="secondary">暂无可复用模块。</Typography.Text>}
      </section>
    </div>
  </Drawer>;
}
