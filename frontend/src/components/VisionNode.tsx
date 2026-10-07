import { useMemo } from 'react';
import { Handle, Position, useConnection, useNodeConnections, useReactFlow } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import { portsCompatible } from '../validation';
import { nodePresentation } from '../toolboxModel';
import NodeCategoryIcon from './NodeCategoryIcon';
import { summarizeScalars } from '../summaryText';
import { portNames } from '../i18n/catalog';
import { getInitialLang } from '../i18n';
import type { CSSProperties } from 'react';

type PortLike = { name: string; dataType: string; label?: string; required?: boolean };

function statusText(status?: string) {
  if (status === 'pending') return '等待';
  if (status === 'running') return '执行';
  if (status === 'ok') return '成功';
  if (status === 'error') return '失败';
  if (status === 'warmup') return '预热';
  if (status === 'breakpoint') return '暂停';
  if (status === 'skipped') return '未走到';
  return '空闲';
}

/** 控制端口（执行/分支）聚为一组放在上方，数据端口排在下方——执行顺序与数据传递各自成区。
 *  只重排显示顺序与 top 位置，Handle 的 id 不变，既有连线完全不受影响。 */
function partitionPorts(ports: PortLike[]) {
  const control = ports.filter((port) => port.dataType === 'Control');
  const data = ports.filter((port) => port.dataType !== 'Control');
  return [...control, ...data].map((port, index) => ({
    port,
    index,
    firstData: control.length > 0 && index === control.length,
  }));
}

export default function VisionNode({ id, data, selected }: NodeProps) {
  const d = data as Record<string, any>;
  const inputs = (d.inputs ?? []) as PortLike[];
  const outputs = (d.outputs ?? []) as PortLike[];
  const status = String(d.status ?? 'idle');
  const duration = d.durationMs as number | undefined;
  const breakpoint = Boolean(d.breakpoint);
  const disposition = d.disposition === 'OK' || d.disposition === 'NG' ? (d.disposition as 'OK' | 'NG') : undefined;
  const errorText = typeof d.error === 'string' && d.error ? d.error : undefined;
  const connection = useConnection();
  const inputConnections = useNodeConnections({ handleType: 'target' });
  const outputConnections = useNodeConnections({ handleType: 'source' });
  const { updateNodeData } = useReactFlow();
  // 语言在启动时由 localStorage 确定性决定（项目无运行时切换入口），直接读模块级值即可
  const lang = getInitialLang();

  const connectedHandles = useMemo(() => ({
    input: new Set(inputConnections.map((c) => c.targetHandle).filter(Boolean) as string[]),
    output: new Set(outputConnections.map((c) => c.sourceHandle).filter(Boolean) as string[]),
  }), [inputConnections, outputConnections]);

  // 一条关键结果：摘要前 2 个标量（zh 模式键名走端口名中文映射），叠加耗时；失败节点优先显示错误摘要
  const summaryLine = summarizeScalars(d.summary as Record<string, unknown> | undefined, 2,
    lang === 'zh' ? (key) => portNames[key] ?? key : undefined);

  // 连线进行中：从起点端口解析类型，为对侧端口标注兼容性
  // （兼容=绿圈高亮，不兼容=降透明度；与 onConnect / isValidConnection / 预检共用 portsCompatible）
  const connectingPort = (() => {
    if (!connection.inProgress) return undefined;
    const source = connection.fromHandle.type === 'source';
    const ports = (source ? connection.fromNode?.data?.outputs : connection.fromNode?.data?.inputs) as PortLike[] | undefined;
    const port = ports?.find((item) => item.name === connection.fromHandle.id);
    return { source, fromNodeId: connection.fromNode?.id, dataType: port?.dataType };
  })();

  const compatFor = (port: PortLike, side: 'input' | 'output'): boolean | undefined => {
    if (!connectingPort?.dataType) return undefined;
    const isCandidate = connectingPort.source ? side === 'input' : side === 'output';
    if (!isCandidate) return undefined;
    if (connectingPort.fromNodeId === id) return false;
    return connectingPort.source
      ? portsCompatible(connectingPort.dataType, port.dataType)
      : portsCompatible(port.dataType, connectingPort.dataType);
  };

  const labelOf = (port: PortLike) => port.label ?? port.name;
  const isConnected = (port: PortLike, side: 'input' | 'output') =>
    side === 'input' ? connectedHandles.input.has(port.name) : connectedHandles.output.has(port.name);
  const portTitle = (port: PortLike, side: 'input' | 'output') => {
    const bits = [labelOf(port), port.dataType];
    if (side === 'input' && port.required && !isConnected(port, side)) bits.push('必需 · 未连接');
    return bits.join(' · ');
  };

  // 端口纵向位置由 CSS 变量驱动（--vs-port-base / --vs-port-step），紧凑模式只需覆盖变量
  const handleStyle = (index: number, compat: boolean | undefined) => ({
    top: `calc(var(--vs-port-base, 58px) + ${index} * var(--vs-port-step, 24px))`,
    ...(compat === true ? { boxShadow: '0 0 0 3px rgba(82, 196, 26, 0.45)', borderColor: '#52c41a' } : {}),
    ...(compat === false ? { opacity: 0.3 } : {})
  });

  const renderPortRow = (port: PortLike, side: 'input' | 'output', index: number, firstData: boolean) => {
    const connected = isConnected(port, side);
    const compat = compatFor(port, side);
    const handle = (
      <Handle
        id={port.name}
        className={port.dataType === 'Control' ? 'control-handle' : 'data-handle'}
        type={side === 'input' ? 'target' : 'source'}
        position={side === 'input' ? Position.Left : Position.Right}
        style={handleStyle(index, compat)}
      />
    );
    return (
      <div
        className={`port-row ${port.dataType === 'Control' ? 'control-port' : ''}${firstData ? ' first-data' : ''}`}
        key={port.name}
      >
        {side === 'input' && handle}
        <span title={portTitle(port, side)}>
          {side === 'input' && port.required && !connected && (
            <em className="port-required" title="必需输入未连接">!</em>
          )}
          {labelOf(port)}<small> {port.dataType}</small>
        </span>
        {side === 'output' && handle}
      </div>
    );
  };

  return (
    <div className={`vision-node ${selected ? 'selected' : ''} status-${status} ${String(d.typeKey).startsWith('flow.') ? 'flow-node' : ''} ${String(d.typeKey) === 'module.call' ? 'module-node' : ''}`} style={{ '--node-accent': nodePresentation(String(d.typeKey)).color } as CSSProperties}>
      {breakpoint && <span className="breakpoint-dot" title="断点" />}
      <div className="vision-node-title">
        <span className="vision-node-name" title={String(d.label ?? d.typeKey)}>
          <NodeCategoryIcon type={String(d.typeKey)} /><span>{String(d.label ?? d.typeKey)}</span>
        </span>
        <span className="node-title-right">
          {disposition && (
            <span className={`node-disposition ${disposition === 'NG' ? 'ng' : 'ok'}`} title="检测判定（与执行状态分开：算法执行成功也可能判定不合格）">
              {disposition}
            </span>
          )}
          <button
            type="button"
            className={`node-breakpoint-toggle${breakpoint ? ' active' : ''}`}
            title={breakpoint ? '移除断点' : '在此节点设置断点'}
            aria-label={breakpoint ? '移除断点' : '在此节点设置断点'}
            onClick={(event) => {
              event.stopPropagation();
              updateNodeData(id, { breakpoint: !breakpoint });
            }}
          >●</button>
          <span className="node-status">{statusText(status)}</span>
        </span>
      </div>
      <div className="vision-node-body">
        <div className="port-column input-column">
          {partitionPorts(inputs).map(({ port, index, firstData }) => renderPortRow(port, 'input', index, firstData))}
        </div>
        <div className="port-column output-column">
          {partitionPorts(outputs).map(({ port, index, firstData }) => renderPortRow(port, 'output', index, firstData))}
        </div>
      </div>
      <div className="vision-node-footer" title={[errorText, summaryLine].filter(Boolean).join(' · ') || undefined}>
        {errorText
          ? `⚠ ${errorText}`
          : summaryLine
            ? `${summaryLine}${duration !== undefined ? ` · ${duration.toFixed(1)} ms` : ''}`
            : duration !== undefined
              ? `${duration.toFixed(2)} ms`
              : (String(d.typeKey) === 'module.call' ? `module.call · V${String(d.moduleVersion ?? '?')}` : String(d.typeKey))}
      </div>
    </div>
  );
}
