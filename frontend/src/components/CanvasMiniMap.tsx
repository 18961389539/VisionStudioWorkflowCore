import { useState } from 'react';
import { MiniMap, Panel } from '@xyflow/react';

const KEY = 'visionstudio.minimap';
const readPreference = () => {
  try { const value = localStorage.getItem(KEY); return value === 'shown' || value === 'hidden' ? value : 'auto'; }
  catch { return 'auto'; }
};

/** 小型流程保持画布清爽；用户的显隐选择优先于自动规则。 */
export default function CanvasMiniMap({ nodeCount }: { nodeCount: number }) {
  const [preference, setPreference] = useState(readPreference);
  const visible = preference === 'shown' || (preference === 'auto' && nodeCount >= 8);
  const toggle = () => {
    const next = visible ? 'hidden' : 'shown';
    setPreference(next);
    try { localStorage.setItem(KEY, next); } catch { /* 显隐仍在当前会话有效 */ }
  };
  return <Panel position="bottom-right" className="canvas-minimap-panel">
    <div className="canvas-minimap-actions"><button type="button" className="canvas-minimap-toggle" aria-expanded={visible} aria-controls="canvas-minimap-content" onClick={toggle}>
      {visible ? '收起小地图' : '展开小地图'}
    </button>
    {preference !== 'auto' && <button type="button" className="canvas-minimap-toggle" aria-label="小地图恢复自动显隐" title="自动：8 个及以上节点展开，小型流程隐藏" onClick={() => {
      setPreference('auto');
      try { localStorage.removeItem(KEY); } catch { /* 当前会话仍恢复自动 */ }
    }}>自动</button>}</div>
    {visible && <div id="canvas-minimap-content"><MiniMap pannable zoomable ariaLabel="流程小地图" style={{ position: 'relative', margin: 0, width: 160, height: 104 }} /></div>}
  </Panel>;
}
