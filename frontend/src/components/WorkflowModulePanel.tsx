import { useEffect, useMemo, useState } from 'react';
import { Button, Drawer, Form, Input, InputNumber, message, Select, Space, Table, Tag, Typography } from 'antd';
import type { ExtractWorkflowModuleResult, WorkflowModuleDescriptor, WorkflowModuleVersion, WorkflowPayload } from '../types';

type Props = {
  open: boolean;
  onClose: () => void;
  currentWorkflow: WorkflowPayload;
  selectedNodeId?: string;
  selectedNodeType?: string;
  onApplyWorkflow: (workflow: WorkflowPayload) => void;
  onLoadWorkflow: (workflow: WorkflowPayload) => void;
  onInsertModule: (version: WorkflowModuleVersion) => void;
  onUpgradeSelected: (version: WorkflowModuleVersion) => void;
};

export default function WorkflowModulePanel(props: Props) {
  const [modules, setModules] = useState<WorkflowModuleDescriptor[]>([]);
  const [selectedNodes, setSelectedNodes] = useState<string[]>([]);
  const [moduleId, setModuleId] = useState('locate-part');
  const [name, setName] = useState('Locate Part');
  const [description, setDescription] = useState('Reusable locating subflow');
  const [note, setNote] = useState('Initial reusable module');
  const [activeModuleId, setActiveModuleId] = useState<string>();
  const [activeVersion, setActiveVersion] = useState<number>();
  const [editingModuleId, setEditingModuleId] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [messageApi, contextHolder] = message.useMessage();

  const refresh = async () => {
    const response = await fetch('/api/modules');
    if (!response.ok) return;
    const data: WorkflowModuleDescriptor[] = await response.json();
    setModules(data);
    if (!activeModuleId && data.length) {
      setActiveModuleId(data[0].id);
      setActiveVersion(data[0].latestVersion);
    }
  };

  useEffect(() => { if (props.open) { refresh(); if (props.selectedNodeId) setSelectedNodes([props.selectedNodeId]); } }, [props.open]);

  const activeModule = modules.find((x) => x.id === activeModuleId);
  const version = activeModule?.versions.find((x) => x.version === activeVersion) ?? activeModule?.versions[0];
  const selectedCallParameters = useMemo(() => {
    const selected = props.currentWorkflow.nodes.find((x) => x.id === props.selectedNodeId);
    return selected?.type === 'module.call' ? selected.parameters : undefined;
  }, [props.currentWorkflow, props.selectedNodeId]);

  const extract = async () => {
    if (!selectedNodes.length) return messageApi.warning('Select nodes to extract');
    setSaving(true);
    try {
      const response = await fetch('/api/modules/extract', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ workflow: props.currentWorkflow, selectedNodeIds: selectedNodes, id: moduleId, name, description, note })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.detail ?? data.error ?? 'Module extraction failed');
      const result = data as ExtractWorkflowModuleResult;
      props.onApplyWorkflow(result.replacementWorkflow);
      setActiveModuleId(result.module.id); setActiveVersion(result.version.version);
      await refresh();
      messageApi.success(`Created ${result.module.name} V${result.version.version} and replaced selected nodes`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Module extraction failed'); }
    finally { setSaving(false); }
  };

  const saveVersion = async () => {
    if (!editingModuleId) return messageApi.warning('Load a module version into Designer first');
    setSaving(true);
    try {
      const response = await fetch(`/api/modules/${encodeURIComponent(editingModuleId)}/versions`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ workflow: props.currentWorkflow, note })
      });
      const data = await response.json();
      if (!response.ok) throw new Error(data.detail ?? data.error ?? 'Save module version failed');
      await refresh();
      setActiveModuleId(editingModuleId); setActiveVersion(data.version);
      messageApi.success(`Saved ${editingModuleId} V${data.version}`);
    } catch (error) { messageApi.error(error instanceof Error ? error.message : 'Save module version failed'); }
    finally { setSaving(false); }
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
            { title: 'Node', dataIndex: 'name', render: (v: unknown, row: WorkflowPayload['nodes'][number]) => String(v ?? row.id) },
            { title: 'Type', dataIndex: 'type', width: 190 },
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
          <Table size="small" pagination={false} rowKey="name" dataSource={version.inputs} columns={[{title:'Input',dataIndex:'name'},{title:'Type',dataIndex:'dataType',width:110},{title:'Maps to',render:(_,r)=>`${r.internalNodeId}.${r.internalPort}`}]} />
          <Table size="small" pagination={false} rowKey="name" dataSource={version.outputs} columns={[{title:'Output',dataIndex:'name'},{title:'Type',dataIndex:'dataType',width:110},{title:'Maps from',render:(_,r)=>`${r.internalNodeId}.${r.internalPort}`}]} />
          <Space wrap style={{ marginTop: 12 }}>
            <Button type="primary" onClick={() => props.onInsertModule(version)}>插入 V{version.version}</Button>
            <Button onClick={() => { props.onLoadWorkflow(version.workflow); setEditingModuleId(version.moduleId); setNote(`从 V${version.version} 更新 ${version.moduleId}`); }}>在设计器中打开模块内部</Button>
            <Button disabled={editingModuleId !== version.moduleId} loading={saving} onClick={saveVersion}>将设计器内容另存为新版本</Button>
            <Button
              disabled={props.selectedNodeType !== 'module.call' || selectedCallParameters?.moduleId !== version.moduleId || Number(selectedCallParameters?.moduleVersion) === version.version}
              onClick={() => props.onUpgradeSelected(version)}
            >将所选模块调用升级到 V{version.version}</Button>
          </Space>
          <div className="module-immutability-note">Existing module calls remain pinned. Upgrade is an explicit edit and therefore changes the parent Workflow semantic hash.</div>
        </> : <Typography.Text type="secondary">暂无可复用模块。</Typography.Text>}
      </section>
    </div>
  </Drawer>;
}
