/**
 * 「从模板新建」弹窗：分组/用途/预览，替换当前流程前显式确认。
 * 从 App.tsx 拆出（巨型组件拆分，2026-10-23）。
 * 查询/分类/选中状态内聚于本组件；打开时重置为入口语义
 * （清空搜索与分类、预选当前模板），与原 App.showTemplates 行为一致。
 */
import { useEffect, useMemo, useState } from 'react';
import type { Edge, Node } from '@xyflow/react';
import { Button, Empty, Input, Modal, Select } from 'antd';
import TemplateFlowPreview from './TemplateFlowPreview';
import { DEMO_GROUPS, DEMO_LABELS } from '../appEditorShared';
import { demoDescriptions, demos, type DemoKey } from '../demos';
import type { NodeCatalogItem } from '../types';

type Props = {
  open: boolean;
  onClose: () => void;
  /** 当前画布使用的模板（卡片上标“当前”） */
  currentDemoKey: DemoKey;
  /** 节点目录：卡片节点芯片与预览标签用目录显示名做本地化 */
  catalog: NodeCatalogItem[];
  /** 应用模板；宿主侧包装未保存守卫（guardUnsaved） */
  onApply: (key: DemoKey) => void;
  /** 转去「打开已有流程」 */
  onOpenSaved: () => void;
};

export default function TemplateGalleryModal({ open, onClose, currentDemoKey, catalog, onApply, onOpenSaved }: Props) {
  const [templateQuery, setTemplateQuery] = useState('');
  const [templateGroup, setTemplateGroup] = useState('all');
  const [templateSelected, setTemplateSelected] = useState<DemoKey | null>(null);

  useEffect(() => {
    if (open) {
      setTemplateQuery('');
      setTemplateGroup('all');
      setTemplateSelected(currentDemoKey);
    }
  }, [open, currentDemoKey]);

  const visibleTemplateGroups = useMemo(() => {
    const keyword = templateQuery.trim().toLowerCase();
    return DEMO_GROUPS.filter((group) => templateGroup === 'all' || templateGroup === group.group)
      .map((group) => ({ ...group, keys: group.keys.filter((key) => `${DEMO_LABELS[key]} ${demoDescriptions[key]}`.toLowerCase().includes(keyword)) }))
      .filter((group) => group.keys.length > 0);
  }, [templateQuery, templateGroup]);

  const templatePreviews = useMemo(() => {
    const previews = new Map<DemoKey, { nodeCount: number; nodeChips: string[]; nodes: Node[]; edges: Edge[] }>();
    for (const [key, build] of Object.entries(demos)) {
      const graph = (build as () => { nodes: Node[]; edges: Edge[] })();
      const labelOf = (type: string) => catalog.find((item) => item.type === type)?.displayName ?? type;
      previews.set(key as DemoKey, {
        nodeCount: graph.nodes.length, nodes: graph.nodes, edges: graph.edges,
        nodeChips: graph.nodes.slice(0, 5).map((item) => labelOf(String(item.data.typeKey)))
      });
    }
    return previews;
  }, [catalog]);

  const templateLabels = useMemo(() => Object.fromEntries(catalog.map((item) => [item.type, item.displayName])), [catalog]);
  const selectedTemplatePreview = templateSelected ? templatePreviews.get(templateSelected) : undefined;

  return (
    <Modal
      open={open}
      title="从模板新建"
      width={780}
      onCancel={onClose}
      footer={[
        <Button key="open" onClick={() => { onClose(); onOpenSaved(); }}>打开已有流程…</Button>,
        <Button key="cancel" onClick={onClose}>取消</Button>,
        <Button
          key="apply"
          type="primary"
          disabled={!templateSelected}
          onClick={() => { if (!templateSelected) return; onClose(); onApply(templateSelected); }}
        >
          {templateSelected ? `使用「${DEMO_LABELS[templateSelected]}」` : '使用此模板'}
        </Button>
      ]}
    >
      <div className="template-filters">
        <Input allowClear value={templateQuery} onChange={(event) => { setTemplateQuery(event.target.value); setTemplateSelected(null); }}
          aria-label="搜索流程模板" placeholder="搜索模板名称或用途" />
        <Select value={templateGroup} onChange={(value) => { setTemplateGroup(value); setTemplateSelected(null); }} aria-label="模板分类" options={[
          { value: 'all', label: '全部分类' }, ...DEMO_GROUPS.map((group) => ({ value: group.group, label: group.group }))
        ]} />
      </div>
      {templateSelected && selectedTemplatePreview && <section className="template-selected-preview" aria-label="所选模板流程预览">
        <div className="template-preview-heading"><strong>{DEMO_LABELS[templateSelected]}</strong><span>{selectedTemplatePreview.nodeCount} 个节点 · {selectedTemplatePreview.edges.length} 条连线</span></div>
        <div className="template-preview-legend"><span>虚线：执行顺序</span><span>实线：数据传递</span><span>滚动查看完整流程</span></div>
        <div className="template-preview-scroll" tabIndex={0} aria-label="完整流程图，可滚动查看">
          <TemplateFlowPreview nodes={selectedTemplatePreview.nodes} edges={selectedTemplatePreview.edges}
            labels={templateLabels} name={DEMO_LABELS[templateSelected]} detailed />
        </div>
      </section>}
      <div className="template-results">
        {visibleTemplateGroups.map((group) => (
          <div key={group.group} className="template-group">
            <div className="template-group-title">{group.group}</div>
            <div className="template-cards">
              {group.keys.map((key) => {
                const preview = templatePreviews.get(key);
                return (
                  <button
                    key={key}
                    type="button"
                    className={`template-card${templateSelected === key ? ' active' : ''}`}
                    aria-pressed={templateSelected === key}
                    onClick={() => setTemplateSelected(key)}
                    title={demoDescriptions[key]}
                  >
                    <div className="template-card-name">
                      {DEMO_LABELS[key]}
                      {currentDemoKey === key && <span className="template-card-current">当前</span>}
                    </div>
                    <div className="template-card-desc">{demoDescriptions[key]}</div>
                    {preview && <TemplateFlowPreview nodes={preview.nodes} edges={preview.edges} labels={templateLabels} name={DEMO_LABELS[key]} />}
                    <div className="template-card-meta">
                      {preview?.nodeCount ?? '—'} 节点
                      {(preview?.nodeChips.length ?? 0) > 0 && <span className="template-card-chips">{preview?.nodeChips.join(' · ')}</span>}
                    </div>
                  </button>
                );
              })}
            </div>
          </div>
        ))}
        {visibleTemplateGroups.length === 0 && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="没有匹配的模板，请调整搜索或分类" />}
      </div>
    </Modal>
  );
}
