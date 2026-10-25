import { useEffect, useMemo, useRef, useState } from 'react';
import { Empty, Input, Modal, Tag } from 'antd';
import type { InputRef } from 'antd';
import type { NodeCatalogItem } from '../types';
import { searchTools } from '../toolboxModel';
import { useI18n } from '../i18n/provider';

/**
 * 统一命令面板（Ctrl/Cmd+K，2026-10-25）。
 *
 * 背景：能力都散着——171 个节点在左侧工具箱、18 个面板在顶栏菜单、运行/校验/布局在按钮上，
 * 熟练用户记不住"某个功能在哪个菜单"。这里把四类目标（命令 / 面板 / 节点 / 最近工作流）
 * 收进一个键盘优先的入口，节点匹配直接复用工具箱那套 `searchTools`（含同义词与排序）。
 */

export type PaletteCommand = {
  /** 稳定标识，执行时回传 */
  key: string;
  title: string;
  hint?: string;
  /** 分组标题：命令 / 面板 / 节点 / 工作流 */
  group: string;
  /** 额外检索词（例如英文名、同义词） */
  keywords?: string;
};

type Props = {
  open: boolean;
  onClose: () => void;
  catalog: NodeCatalogItem[];
  /** 非节点类目标：命令与面板入口由调用方提供 */
  targets: PaletteCommand[];
  onRunCommand: (key: string) => void;
  onAddNode: (type: string) => void;
};

type Row = { key: string; title: string; hint?: string; group: string; type?: string };

const MAX_PER_GROUP = 8;
const GROUP_ORDER = ['命令', '面板', '节点'];

// 中英双语的默认分组名：命令面板本身要能在两种语言下用
const groupLabel = (group: string, lang: string) => {
  if (lang !== 'en') return group;
  return group === '命令' ? 'Commands' : group === '面板' ? 'Panels' : group === '节点' ? 'Nodes' : group;
};

export default function CommandPalette({ open, onClose, catalog, targets, onRunCommand, onAddNode }: Props) {
  const { lang } = useI18n();
  const [query, setQuery] = useState('');
  const [active, setActive] = useState(0);
  const inputRef = useRef<InputRef>(null);
  const listRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    setQuery('');
    setActive(0);
    // Modal 动画结束再聚焦，避免焦点被弹层抢走
    const timer = window.setTimeout(() => inputRef.current?.focus(), 30);
    return () => window.clearTimeout(timer);
  }, [open]);

  const rows = useMemo<Row[]>(() => {
    const keyword = query.trim().toLowerCase();
    const match = (text: string) => !keyword || text.toLowerCase().includes(keyword);
    const commandRows = targets
      .filter((item) => match(item.title) || (item.keywords ? item.keywords.toLowerCase().includes(keyword) : false))
      .map((item) => ({ key: item.key, title: item.title, hint: item.hint, group: item.group }));
    const nodeRows = (keyword ? searchTools(catalog, query) : catalog.slice(0, 30)).map((item) => ({
      key: `node:${item.type}`,
      title: item.displayName,
      hint: item.type,
      group: '节点',
      type: item.type
    }));
    const byGroup = new Map<string, Row[]>();
    for (const row of [...commandRows, ...nodeRows]) {
      const bucket = byGroup.get(row.group) ?? [];
      if (bucket.length < MAX_PER_GROUP) bucket.push(row);
      byGroup.set(row.group, bucket);
    }
    return [...GROUP_ORDER, ...byGroup.keys()].filter((group, index, all) => all.indexOf(group) === index)
      .flatMap((group) => byGroup.get(group) ?? []);
  }, [catalog, query, targets]);

  useEffect(() => { setActive(0); }, [query]);

  useEffect(() => {
    const node = listRef.current?.querySelector<HTMLElement>(`[data-index="${active}"]`);
    // jsdom 没有 scrollIntoView，可选用法保证单测环境不抛
    node?.scrollIntoView?.({ block: 'nearest' });
  }, [active]);

  const run = (row: Row | undefined) => {
    if (!row) return;
    if (row.type) onAddNode(row.type);
    else onRunCommand(row.key);
    onClose();
  };

  const onKeyDown = (event: React.KeyboardEvent) => {
    if (event.key === 'ArrowDown') { event.preventDefault(); setActive((current) => Math.min(current + 1, rows.length - 1)); return; }
    if (event.key === 'ArrowUp') { event.preventDefault(); setActive((current) => Math.max(current - 1, 0)); return; }
    if (event.key === 'Enter') { event.preventDefault(); run(rows[active]); return; }
    if (event.key === 'Escape') { event.preventDefault(); onClose(); }
  };

  return (
    <Modal open={open} onCancel={onClose} footer={null} width={640} centered destroyOnHidden
      className="command-palette" title={null} closable={false} styles={{ body: { padding: 0 } }}>
      <div className="command-palette-search">
        <Input ref={inputRef} size="large" allowClear value={query} onChange={(event) => setQuery(event.target.value)}
          onKeyDown={onKeyDown} prefix={<span className="command-palette-glyph">⌘</span>}
          placeholder={lang === 'en' ? 'Search nodes, panels and commands…' : '搜索节点、面板或命令…'} aria-label="命令面板搜索" />
      </div>
      <div className="command-palette-list" ref={listRef} role="listbox" aria-label="命令面板结果">
        {rows.length === 0 && <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={lang === 'en' ? 'No matching target' : '没有匹配的节点、面板或命令'} />}
        {rows.map((row, index) => {
          const first = index === 0 || rows[index - 1].group !== row.group;
          return (
            <div key={row.key} data-index={index} role="option" aria-selected={index === active}
              className={`command-palette-row${index === active ? ' is-active' : ''}`}
              onMouseEnter={() => setActive(index)} onClick={() => run(row)}>
              {first && <span className="command-palette-group">{groupLabel(row.group, lang)}</span>}
              <span className="command-palette-title">{row.title}</span>
              {row.hint && <Tag className="command-palette-hint">{row.hint}</Tag>}
            </div>
          );
        })}
      </div>
      <div className="command-palette-footer">
        <span>↑↓ 选择</span><span>Enter 执行</span><span>Esc 关闭</span>
        <span className="command-palette-count">{rows.length} 项</span>
      </div>
    </Modal>
  );
}
