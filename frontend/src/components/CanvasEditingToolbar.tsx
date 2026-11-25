import { memo, useState, type RefObject } from 'react';
import { Button, Input, Switch, Tag, type InputRef } from 'antd';
import { useI18n } from '../i18n/provider';

type Props = {
  nodeCount: number;
  selectedId: string;
  configurableSelected: boolean;
  selectedCount: number;
  groupableSelectionCount: number;
  selectedEdgeIds: string[];
  anyBusy: boolean;
  canUndo: boolean;
  canRedo: boolean;
  undoLabel?: string;
  redoLabel?: string;
  undo: () => void;
  redo: () => void;
  deleteElements: (nodeIds: string[], edgeIds: string[]) => void;
  compactNodes: boolean;
  setCompactNodes: (value: boolean) => void;
  openNodeConfiguration: (nodeId: string) => void;
  imagePreviewOpen: boolean;
  focusMode: boolean;
  exitFocusMode: (restore: boolean) => void;
  setImagePreviewOpen: (value: boolean | ((open: boolean) => boolean)) => void;
  addCanvasNote: () => void;
  createGroup: () => void;
  alignSelection: (mode: 'left' | 'centerX' | 'right' | 'top' | 'centerY' | 'bottom') => void;
  searchInputRef: RefObject<InputRef | null>;
  searchQuery: string;
  setSearchQuery: (value: string) => void;
  searchOpen: boolean;
  setSearchOpen: (value: boolean) => void;
  searchMatches: { id: string; label: string }[];
  focusNode: (id: string) => void;
};

function CanvasEditingToolbar(props: Props) {
  const { lang } = useI18n();
  const text = (zh: string, en: string) => lang === 'en' ? en : zh;
  const { searchMatches, searchOpen, searchQuery, setSearchOpen, setSearchQuery, focusNode } = props;
  const [searchActiveIndex, setSearchActiveIndex] = useState(0);
  const visibleSearchMatches = searchMatches.slice(0, 8);
  const clampedSearchIndex = visibleSearchMatches.length > 0 ? Math.min(searchActiveIndex, visibleSearchMatches.length - 1) : -1;
  return <>
    <div className="canvas-toolbar" role="toolbar" aria-label="画布编辑工具">
      <div className="canvas-search">
        <Input ref={props.searchInputRef} size="small" allowClear role="combobox" aria-autocomplete="list"
          aria-expanded={searchOpen && searchQuery.trim() !== ''} aria-controls="canvas-search-listbox"
          aria-activedescendant={searchOpen && searchQuery.trim() !== '' && clampedSearchIndex >= 0
            ? `canvas-search-option-${clampedSearchIndex}` : undefined}
          className="canvas-search-input" placeholder={text('画布中查找（Ctrl+F）', 'Find on canvas (Ctrl+F)')}
          value={searchQuery}
          onChange={(event) => { setSearchQuery(event.target.value); setSearchActiveIndex(0); setSearchOpen(true); }}
          onFocus={() => setSearchOpen(true)} onBlur={() => setSearchOpen(false)}
          onKeyDown={(event) => {
            if (event.key === 'ArrowDown') {
              event.preventDefault();
              setSearchOpen(true);
              if (visibleSearchMatches.length > 0) setSearchActiveIndex((index) => (index + 1) % visibleSearchMatches.length);
              return;
            }
            if (event.key === 'ArrowUp') {
              event.preventDefault();
              setSearchOpen(true);
              if (visibleSearchMatches.length > 0)
                setSearchActiveIndex((index) => (index - 1 + visibleSearchMatches.length) % visibleSearchMatches.length);
              return;
            }
            if (event.key === 'Enter' && visibleSearchMatches.length > 0) {
              const target = visibleSearchMatches[Math.max(0, clampedSearchIndex)] ?? visibleSearchMatches[0];
              focusNode(target.id);
              setSearchOpen(false);
            }
            if (event.key === 'Escape') { setSearchQuery(''); setSearchOpen(false); }
          }} />
        {searchOpen && searchQuery.trim() !== '' && (
          <div id="canvas-search-listbox" role="listbox" aria-label="画布节点搜索结果" className="canvas-search-results"
            onMouseDown={(event) => event.preventDefault()}>
            <div className="canvas-search-count">命中 {searchMatches.length} / {props.nodeCount}{searchMatches.length > 8 ? '（仅显示前 8 条）' : ''}</div>
            {visibleSearchMatches.map((node, index) => (
              <button key={node.id} id={`canvas-search-option-${index}`} type="button" role="option"
                aria-selected={index === clampedSearchIndex} className={`canvas-search-item${index === clampedSearchIndex ? ' is-active' : ''}`}
                title="定位并选中该节点" onMouseEnter={() => setSearchActiveIndex(index)}
                onClick={() => { focusNode(node.id); setSearchOpen(false); }}>
                <span className="canvas-search-label">{node.label}</span><small>{node.id}</small>
              </button>
            ))}
            {searchMatches.length === 0 && <div className="canvas-search-empty">无匹配节点</div>}
          </div>
        )}
      </div>
      <span className="canvas-toolbar-divider" />
      <Button size="small" className="canvas-history-button" disabled={!props.canUndo || props.anyBusy}
        title={`${text('撤销', 'Undo')}${props.undoLabel ? ` · ${props.undoLabel}` : ''}（Ctrl+Z）`} aria-label={text('撤销', 'Undo')} onClick={props.undo}>↶</Button>
      <Button size="small" className="canvas-history-button" disabled={!props.canRedo || props.anyBusy}
        title={`${text('重做', 'Redo')}${props.redoLabel ? ` · ${props.redoLabel}` : ''}（Ctrl+Shift+Z）`} aria-label={text('重做', 'Redo')} onClick={props.redo}>↷</Button>
      {props.selectedEdgeIds.length > 0 && <Button size="small" danger disabled={props.anyBusy}
        aria-label={text(`删除选中连线 ${props.selectedEdgeIds.length} 条`, `Delete ${props.selectedEdgeIds.length} selected edges`)}
        title={text('删除选中的连线（Delete / Backspace）', 'Delete selected edges (Delete / Backspace)')}
        onClick={() => props.deleteElements([], props.selectedEdgeIds)}>{text(`删除连线 · ${props.selectedEdgeIds.length}`, `Delete edges · ${props.selectedEdgeIds.length}`)}</Button>}
      {props.selectedEdgeIds.length === 0 && <span className="canvas-edge-delete-hint" title={text('单击画布中的连线将其选中', 'Click an edge on canvas to select')}>{text('连线：选中后按 Del 删除', 'Edge: select & press Del')}</span>}
      <span className="canvas-toolbar-divider" />
      <label className="canvas-compact-toggle" title={text('紧凑模式：压缩节点标题与端口间距，适合多端口的大型流程', 'Compact mode: tighten node header & port spacing')}>
        <Switch size="small" checked={props.compactNodes} onChange={props.setCompactNodes} /><span>{text('紧凑', 'Compact')}</span>
      </label>
      <Button size="small" disabled={!props.configurableSelected} title={text('打开所选节点的参数、图像和详细说明（也可双击节点）', 'Configure selected node (or double-click)')}
        onClick={() => props.openNodeConfiguration(props.selectedId)}>{text('节点配置', 'Configure')}</Button>
      <Button size="small" type={props.imagePreviewOpen && !props.focusMode ? 'primary' : 'default'}
        aria-pressed={props.imagePreviewOpen && !props.focusMode}
        onClick={() => { if (props.focusMode) props.exitFocusMode(true); props.setImagePreviewOpen((open) => props.focusMode || !open); }}>{text('图像预览', 'Preview')}</Button>
      <Button size="small" disabled={props.anyBusy} title={text('添加只保存在本机画布中的流程说明', 'Add a canvas-only note')} onClick={props.addCanvasNote}>{text('便签', 'Note')}</Button>
      <Button size="small" disabled={props.anyBusy || props.groupableSelectionCount === 0}
        title={text('将选中的未分组节点收纳为可移动的画布分组', 'Group selected ungrouped nodes on the canvas')}
        onClick={props.createGroup}>{text('分组', 'Group')}</Button>
    </div>
    {props.selectedCount >= 2 && <div className="canvas-align-toolbar" role="toolbar" aria-label="多选对齐工具">
      <Tag style={{ marginInlineEnd: 0 }}>{text(`已选 ${props.selectedCount}`, `${props.selectedCount} selected`)}</Tag>
      <Button size="small" onClick={() => props.alignSelection('left')}>{text('左对齐', 'Left')}</Button>
      <Button size="small" onClick={() => props.alignSelection('centerX')}>{text('水平居中', 'Center X')}</Button>
      <Button size="small" onClick={() => props.alignSelection('right')}>{text('右对齐', 'Right')}</Button>
      <Button size="small" onClick={() => props.alignSelection('top')}>{text('顶对齐', 'Top')}</Button>
      <Button size="small" onClick={() => props.alignSelection('centerY')}>{text('垂直居中', 'Center Y')}</Button>
      <Button size="small" onClick={() => props.alignSelection('bottom')}>{text('底对齐', 'Bottom')}</Button>
    </div>}
  </>;
}

export default memo(CanvasEditingToolbar, (previous, next) => {
  if (Object.keys(previous).length !== Object.keys(next).length) return false;
  for (const key of Object.keys(previous) as (keyof Props)[]) {
    if (key !== 'searchMatches' && !Object.is(previous[key], next[key])) return false;
  }
  return previous.searchMatches.length === next.searchMatches.length
    && previous.searchMatches.every((node, index) =>
      node.id === next.searchMatches[index].id && node.label === next.searchMatches[index].label);
});
