import { useId } from 'react';
import type { Edge, Node } from '@xyflow/react';
import { nodePresentation } from '../toolboxModel';

type Props = { nodes: Node[]; edges: Edge[]; labels: Record<string, string>; name: string; detailed?: boolean };

/** 使用模板本身的位置和连线绘制拓扑，分支及并行结构不会被误画成线性步骤。 */
export default function TemplateFlowPreview({ nodes, edges, labels, name, detailed = false }: Props) {
  const markerId = `template-arrow-${useId().replace(/:/g, '')}`;
  if (!nodes.length) return null;
  const nodeWidth = 180;
  const nodeHeight = 48;
  const minX = Math.min(...nodes.map((node) => node.position.x)) - 20;
  const minY = Math.min(...nodes.map((node) => node.position.y)) - 20;
  const width = Math.max(...nodes.map((node) => node.position.x)) - minX + nodeWidth + 20;
  const height = Math.max(...nodes.map((node) => node.position.y)) - minY + nodeHeight + 20;
  const byId = new Map(nodes.map((node) => [node.id, node]));
  return <svg className={`template-flow-preview${detailed ? ' detailed' : ''}`} role="img"
    aria-label={`${name}：${nodes.length} 个节点、${edges.length} 条连线的流程图`}
    viewBox={`${minX} ${minY} ${width} ${height}`}
    style={detailed ? { width: Math.max(640, width * .7), height: Math.max(120, height * .7) } : undefined}>
    <title>{name} · 虚线为执行顺序，实线为数据传递</title>
    <defs><marker id={markerId} viewBox="0 0 10 10" refX="9" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse">
      <path d="M0 0 10 5 0 10z" fill="currentColor" />
    </marker></defs>
    <g className="template-flow-edges" fill="none" stroke="currentColor" strokeWidth={detailed ? 2 : 3}>
      {edges.map((edge) => {
        const source = byId.get(edge.source);
        const target = byId.get(edge.target);
        if (!source || !target) return null;
        const control = edge.data?.kind === 'control';
        const offset = control ? 15 : 33;
        const x1 = source.position.x + nodeWidth;
        const y1 = source.position.y + offset;
        const x2 = target.position.x;
        const y2 = target.position.y + offset;
        const bend = Math.max(40, Math.abs(x2 - x1) * .5);
        return <path key={edge.id} d={`M${x1} ${y1} C${x1 + bend} ${y1} ${x2 - bend} ${y2} ${x2} ${y2}`}
          strokeDasharray={control ? '8 5' : undefined} markerEnd={`url(#${markerId})`} />;
      })}
    </g>
    {nodes.map((node) => {
      const type = String(node.data.typeKey ?? '');
      const label = labels[type] ?? type;
      const labelLimit = detailed ? 8 : 10;
      return <g key={node.id} transform={`translate(${node.position.x} ${node.position.y})`}>
        <title>{label} · {node.id}</title>
        <rect className="template-flow-node" width={nodeWidth} height={nodeHeight} rx="8" />
        <rect x="0" y="8" width="4" height="32" rx="2" fill={nodePresentation(type).color} />
        <text x="14" y="29" className="template-flow-label">{label.length > labelLimit ? `${label.slice(0, labelLimit - 1)}…` : label}</text>
      </g>;
    })}
  </svg>;
}
