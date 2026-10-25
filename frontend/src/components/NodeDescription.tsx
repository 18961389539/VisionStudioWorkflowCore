import { useState } from 'react';
import { useI18n } from '../i18n/provider';

/** 节点说明往往同时包含用途、接线、参数和失败语义；默认只显示第一句，完整内容按需展开。 */
export default function NodeDescription({ text, className = 'node-info-description' }: { text?: string; className?: string }) {
  const [expanded, setExpanded] = useState(false);
  const { lang } = useI18n();
  const zh = lang !== 'en';
  const full = (text ?? '').trim();
  const summaryEnd = [full.indexOf('。'), full.indexOf('！'), full.indexOf('？'), full.search(/\.(?:\s|$)/)]
    .filter((index) => index >= 0)
    .sort((left, right) => left - right)[0];
  const summary = summaryEnd !== undefined && summaryEnd < full.length - 1 ? full.slice(0, summaryEnd + 1) : full;
  const hasMore = summary.length < full.length;
  if (!full) return <p className={className}>{zh ? '暂无节点说明。' : 'No description.'}</p>;
  return <div className="node-description">
    <p className={className}>{expanded ? full : summary}</p>
    {hasMore && <button type="button" className="node-description-toggle" aria-expanded={expanded}
      onClick={() => setExpanded((value) => !value)}>
      {expanded ? (zh ? '收起说明' : 'Show less') : (zh ? '展开完整说明' : 'Show full description')}
    </button>}
  </div>;
}
