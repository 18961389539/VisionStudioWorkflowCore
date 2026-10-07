import { useEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react';
import { Input, Select, type InputRef } from 'antd';
import type { NodeCatalogItem } from '../types';
import { compatiblePorts, nodePresentation, searchTools, type PortRecommendation } from '../toolboxModel';
import { getInitialLang } from '../i18n';
import NodeCategoryIcon from './NodeCategoryIcon';

type Props = {
  catalog: NodeCatalogItem[];
  recentTypes: string[];
  onAddNode: (typeKey: string, portName?: string) => void;
  connection?: PortRecommendation;
  onClose?: () => void;
};

const COLLAPSED_KEY = 'visionstudio.toolbox-collapsed';
const FAVORITES_KEY = 'visionstudio.toolbox-favorites';
const FAVORITES_EVENT = 'visionstudio:toolbox-favorites';
function readList(key: string): string[] {
  try {
    const parsed = JSON.parse(window.localStorage.getItem(key) ?? '[]');
    return Array.isArray(parsed) ? parsed.filter((value): value is string => typeof value === 'string') : [];
  } catch { return []; }
}
function persistList(key: string, value: string[]) {
  try { window.localStorage.setItem(key, JSON.stringify(value)); } catch { /* Storage may be unavailable. */ }
}
function Highlight({ text, query }: { text: string; query: string }) {
  const keyword = query.trim().toLowerCase();
  const index = keyword ? text.toLowerCase().indexOf(keyword) : -1;
  return index < 0 ? <>{text}</> : <>{text.slice(0, index)}<mark>{text.slice(index, index + keyword.length)}</mark>{text.slice(index + keyword.length)}</>;
}

export default function Toolbox({ catalog, recentTypes, onAddNode, connection, onClose }: Props) {
  const lang = getInitialLang();
  const text = (zh: string, en: string) => lang === 'en' ? en : zh;
  const [query, setQuery] = useState('');
  const [collapsed, setCollapsed] = useState<string[]>(() => readList(COLLAPSED_KEY));
  const [favorites, setFavorites] = useState<string[]>(() => readList(FAVORITES_KEY));
  const [tab, setTab] = useState<'all' | 'favorites'>('all');
  const [showAllRecent, setShowAllRecent] = useState(false);
  const [selectedType, setSelectedType] = useState<string>();
  const [selectedPort, setSelectedPort] = useState<string>();
  const [feedback, setFeedback] = useState('');
  const inputRef = useRef<InputRef>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const keyword = query.trim();

  useEffect(() => { if (connection) inputRef.current?.focus(); }, [connection]);
  useEffect(() => { persistList(COLLAPSED_KEY, collapsed); }, [collapsed]);
  useEffect(() => {
    const refreshFavorites = () => setFavorites(readList(FAVORITES_KEY));
    const sync = (event: StorageEvent) => {
      if (event.key === FAVORITES_KEY || event.key === null) refreshFavorites();
    };
    window.addEventListener('storage', sync);
    window.addEventListener(FAVORITES_EVENT, refreshFavorites);
    return () => {
      window.removeEventListener('storage', sync);
      window.removeEventListener(FAVORITES_EVENT, refreshFavorites);
    };
  }, []);
  const results = useMemo(() => searchTools(catalog, query).filter((item) =>
    (tab === 'all' || favorites.includes(item.type)) && (!connection || compatiblePorts(item, connection).length > 0)),
  [catalog, query, tab, favorites, connection]);
  const groups = useMemo(() => {
    const map = new Map<string, { key: string; label: string; items: NodeCatalogItem[] }>();
    results.forEach((item) => {
      const category = nodePresentation(item.type).category;
      const key = category === 'plugin' ? `plugin:${item.category}` : category;
      const group = map.get(key);
      if (group) group.items.push(item);
      else map.set(key, { key, label: item.category, items: [item] });
    });
    return [...map.values()];
  }, [results]);
  const recentItems = recentTypes.map((type) => results.find((item) => item.type === type))
    .filter((item): item is NodeCatalogItem => Boolean(item));
  const visibleItems = keyword || tab === 'favorites' || connection ? results : [
    ...recentItems.slice(0, showAllRecent ? undefined : 3),
    ...groups.filter((group) => !collapsed.includes(group.key) && !collapsed.includes(group.label)).flatMap((group) => group.items)
  ];
  const navigationItems = [...new Map(visibleItems.map((item) => [item.type, item])).values()];
  const selected = catalog.find((item) => item.type === selectedType);
  const selectedVisible = navigationItems.some((item) => item.type === selectedType);
  const activeType = selectedVisible ? selectedType : keyword || connection ? navigationItems[0]?.type : undefined;
  const ports = selected && connection ? compatiblePorts(selected, connection) : [];

  useEffect(() => {
    listRef.current?.querySelector<HTMLElement>('[data-tool-active="true"]')?.scrollIntoView({ block: 'nearest' });
  }, [selectedType, query, tab]);
  const select = (type: string) => {
    if (selectedType !== type) setSelectedPort(undefined);
    setSelectedType(type);
    const item = catalog.find((item) => item.type === type);
    setFeedback(item ? text(`已选「${item.displayName}」· 回车添加`, `Selected ${item.displayName} · Enter to add`) : '');
  };
  const add = (item: NodeCatalogItem) => {
    const compatible = connection ? compatiblePorts(item, connection) : [];
    if (connection && compatible.length === 0) return;
    const port = compatible.length === 1 ? compatible[0].name : selectedType === item.type ? selectedPort : undefined;
    if (connection && (!port || !compatible.some((candidate) => candidate.name === port))) {
      select(item.type);
      setFeedback(text('请选择要连接的端口', 'Choose a port to connect'));
      return;
    }
    onAddNode(item.type, port);
    setFeedback(text(`已添加「${item.displayName}」`, `Added ${item.displayName}`));
  };
  const onSearchKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.nativeEvent.isComposing) return;
    if (event.key === 'Escape') {
      event.preventDefault(); event.stopPropagation();
      if (query) { setQuery(''); setFeedback(''); } else onClose?.();
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (!navigationItems.length) return;
      const index = navigationItems.findIndex((item) => item.type === activeType);
      const next = index < 0 ? event.key === 'ArrowDown' ? 0 : navigationItems.length - 1
        : (index + (event.key === 'ArrowDown' ? 1 : -1) + navigationItems.length) % navigationItems.length;
      select(navigationItems[next].type);
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      const item = navigationItems.find((item) => item.type === activeType);
      if (item) add(item);
    }
  };
  const toggleFavorite = (type: string) => {
    const next = favorites.includes(type) ? favorites.filter((item) => item !== type) : [...favorites, type];
    persistList(FAVORITES_KEY, next);
    window.dispatchEvent(new Event(FAVORITES_EVENT));
    setFavorites(next);
  };
  const renderItem = (item: NodeCatalogItem) => {
    const favorite = favorites.includes(item.type);
    const active = item.type === activeType;
    return <div key={item.type} className={`toolbox-item${active ? ' active' : ''}`} data-tool-active={active}
      style={{ '--node-accent': nodePresentation(item.type).color } as CSSProperties}
      draggable={!connection} onDragStart={(event) => {
        event.dataTransfer.setData('application/vision-node', item.type);
        event.dataTransfer.effectAllowed = 'copy';
      }}>
      <button type="button" className="toolbox-item-main" aria-pressed={selectedType === item.type}
        title={item.displayName} onClick={() => select(item.type)} onDoubleClick={() => add(item)}
        onKeyDown={(event) => { if (event.key === 'Enter') { event.preventDefault(); add(item); } }}>
        <NodeCategoryIcon type={item.type} /><span className="toolbox-item-name"><Highlight text={item.displayName} query={query} /></span>
      </button>
      <button type="button" className={`toolbox-action${favorite ? ' favorited' : ''}`} aria-label={text(`${favorite ? '取消收藏' : '收藏'} ${item.displayName}`, `${favorite ? 'Unfavorite' : 'Favorite'} ${item.displayName}`)}
        aria-pressed={favorite} title={text(favorite ? '取消收藏' : '收藏', favorite ? 'Unfavorite' : 'Favorite')}
        onClick={() => toggleFavorite(item.type)}>{favorite ? '★' : '☆'}</button>
      <button type="button" className="toolbox-action toolbox-add" aria-label={text(`添加 ${item.displayName}`, `Add ${item.displayName}`)}
        title={text('添加到画布', 'Add to canvas')} onClick={() => add(item)}>＋</button>
    </div>;
  };

  return <div className={`toolbox${connection ? ' toolbox-connection' : ''}`}>
    <div className="toolbox-header">
      <div className="panel-title"><span>{text(connection ? '添加兼容节点' : '节点工具箱', connection ? 'Add compatible node' : 'Node toolbox')}</span>
        <span className="toolbox-total">{results.length}</span>
        {onClose && <button type="button" className="toolbox-close" aria-label={text('关闭节点推荐', 'Close node picker')} onClick={onClose}>×</button>}
      </div>
      <div className="toolbox-search"><Input ref={inputRef} size="small" allowClear value={query} aria-label={text('搜索节点', 'Search nodes')}
        prefix={<span aria-hidden="true">⌕</span>} placeholder={text('搜索名称、别名或类型', 'Name, alias or type')}
        onChange={(event) => { setQuery(event.target.value); setSelectedType(undefined); setSelectedPort(undefined); setFeedback(''); }} onKeyDown={onSearchKeyDown} /></div>
      {!connection && <div className="toolbox-tabs" role="group" aria-label={text('节点筛选', 'Filter nodes')}>
        <button type="button" aria-pressed={tab === 'all'} onClick={() => setTab('all')}>{text('全部', 'All')}</button>
        <button type="button" aria-pressed={tab === 'favorites'} onClick={() => setTab('favorites')}>{text('收藏', 'Favorites')} <small>{catalog.filter((item) => favorites.includes(item.type)).length}</small></button>
      </div>}
      <div className="toolbox-hint">{connection ? `${connection.dataType} ${connection.direction === 'source' ? '→' : '←'} ${text('兼容端口', 'compatible ports')}` : text('拖拽 / 双击 / ＋ 添加', 'Drag / double-click / ＋ to add')}</div>
    </div>
    <div className="toolbox-list" ref={listRef}>
      {results.length === 0 ? <div className="toolbox-empty">
        {keyword ? text(`未找到「${keyword}」`, `No results for “${keyword}”`) : text(tab === 'favorites' ? '还没有收藏的节点' : connection ? '暂无兼容节点' : '暂无可用节点', tab === 'favorites' ? 'No favorite nodes yet' : connection ? 'No compatible nodes' : 'No nodes available')}
        <small>{text(tab === 'favorites' ? '在全部节点中点击 ☆ 收藏' : '尝试其他名称、别名或类型', tab === 'favorites' ? 'Use ☆ in All to save favorites' : 'Try another name, alias or type')}</small>
        {keyword && <button type="button" className="toolbox-link" onClick={() => setQuery('')}>{text('清空搜索', 'Clear search')}</button>}
      </div> : keyword || tab === 'favorites' || connection ? <>
        <div className="toolbox-hint">{text(`${results.length} 个结果 · ↑↓ 选择 · 回车添加`, `${results.length} results · ↑↓ select · Enter to add`)}</div>
        {results.map(renderItem)}
      </> : <>
        {recentItems.length > 0 && <section aria-label={text('最近使用', 'Recently used')}>
          <div className="toolbox-category static"><span>{text('最近使用', 'Recent')}</span><span className="toolbox-category-count">{recentItems.length}</span>
            {recentItems.length > 3 && <button type="button" className="toolbox-link" aria-expanded={showAllRecent} onClick={() => setShowAllRecent(!showAllRecent)}>{text(showAllRecent ? '收起' : '更多', showAllRecent ? 'Less' : 'More')}</button>}
          </div>{recentItems.slice(0, showAllRecent ? undefined : 3).map(renderItem)}
        </section>}
        {groups.map((group) => {
          const closed = collapsed.includes(group.key) || collapsed.includes(group.label);
          return <section key={group.key}>
            <button type="button" className="toolbox-category" aria-expanded={!closed} onClick={() => setCollapsed((current) => closed
              ? current.filter((key) => key !== group.key && key !== group.label) : [...current, group.key])}>
              <span className="toolbox-caret">{closed ? '▸' : '▾'}</span><span className="toolbox-category-name">{group.label}</span><span className="toolbox-category-count">{group.items.length}</span>
            </button>{!closed && group.items.map(renderItem)}
          </section>;
        })}
      </>}
    </div>
    {selected && selectedVisible && <section className="toolbox-detail" aria-label={text('节点详情', 'Node details')}>
      <div className="toolbox-detail-heading"><strong>{selected.displayName}</strong><button type="button" className="toolbox-close" aria-label={text('关闭节点详情', 'Close node details')} onClick={() => setSelectedType(undefined)}>×</button></div>
      <code>{selected.type}</code>
      <p>{selected.type === 'image.threshold' ? text('将图像转换为二值图像，便于分离目标与背景。', 'Convert an image to binary to separate objects from the background.') : selected.description || text('选择后添加到画布，在属性面板配置参数。', 'Add to canvas and configure in the properties panel.')}</p>
      <div className="toolbox-badges">
        {selected.pluginId && selected.pluginId !== 'builtin' && <span>{text('来源', 'Source')} · {selected.pluginId}</span>}
        {['image.acquire', 'camera.syncCapture'].includes(selected.type) && <span>{text('需要相机', 'Camera required')}</span>}
        {selected.type.startsWith('coordinate.') && selected.parameters.some((parameter) => parameter.name.toLowerCase().includes('calibration')) && <span>{text('需要标定', 'Calibration required')}</span>}
        {['device.writeTag', 'device.writeVisionResult', 'robot.executeTarget'].includes(selected.type) && <span className="toolbox-effect">{text('执行时操作设备', 'Operates hardware when run')}</span>}
        {selected.capabilities?.supportsRoi && <span>ROI</span>}
      </div>
      {(['inputs', 'outputs'] as const).map((side) => {
        const dataPorts = selected[side].filter((port) => port.dataType !== 'Control');
        return dataPorts.length > 0 && <div className="toolbox-ports" key={side}><span>{text(side === 'inputs' ? '输入' : '输出', side === 'inputs' ? 'Input' : 'Output')}</span><div>{dataPorts.map((port) => <span key={port.name}>{port.name} <small>{port.dataType}{port.required ? ' *' : ''}</small></span>)}</div></div>;
      })}
      {connection && ports.length > 1 && <Select className="toolbox-port-select" size="small" value={selectedPort} aria-label={text('选择连接端口', 'Choose connection port')}
        placeholder={text('选择要连接的端口', 'Choose a port to connect')} onChange={setSelectedPort} options={ports.map((port) => ({ value: port.name, label: `${port.name} · ${port.dataType}` }))} />}
      <button type="button" className="toolbox-detail-add" disabled={Boolean(connection && ports.length > 1 && !selectedPort)} onClick={() => add(selected)}>{text(connection ? '添加并连接' : '＋ 添加到画布', connection ? 'Add and connect' : '＋ Add to canvas')}</button>
    </section>}
    <div className="toolbox-feedback" role="status" aria-live="polite">{feedback || text('单击节点查看用途和端口', 'Click a node to view its purpose and ports')}</div>
  </div>;
}
