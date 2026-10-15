import { useEffect, useMemo, useState } from 'react';
import { Handle, Position, useConnection, useNodeConnections, useReactFlow, useUpdateNodeInternals } from '@xyflow/react';
import type { NodeProps } from '@xyflow/react';
import { connectionVerdict } from '../validation';
import { nodePresentation } from '../toolboxModel';
import NodeCategoryIcon from './NodeCategoryIcon';
import { summarizeScalars } from '../summaryText';
import { portNames } from '../i18n/catalog';
import { getInitialLang } from '../i18n';
import { configureNode, showNodeDetails } from '../uiBridge';
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

/** 有“上一次结果”遗留的状态（改了参数/ROI 后旧结果尚未刷新） */
const hasResultState = (status: string) => status === 'ok' || status === 'error' || status === 'warmup' || status === 'breakpoint';

/** 控制端口（执行/分支）聚为一组放在上方，数据端口排在下方——执行顺序与数据传递各自成区。
 *  保留 Handle 的 id；显示列表变化后刷新连线落点，Handle 由 CSS 相对端口行居中。 */
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
  const typeKey = String(d.typeKey ?? '');
  const status = String(d.status ?? 'idle');
  const duration = d.durationMs as number | undefined;
  const breakpoint = Boolean(d.breakpoint);
  const disposition = d.disposition === 'OK' || d.disposition === 'NG' ? (d.disposition as 'OK' | 'NG') : undefined;
  const errorText = typeof d.error === 'string' && d.error ? d.error : undefined;
  // 改过参数/ROI 但还没重跑：节点上仍挂着上次结果 → 标“过期”，避免把旧 OK/NG 当成当前结论
  const stale = Boolean(d.stale) && hasResultState(status);
  const connection = useConnection();
  const inputConnections = useNodeConnections({ handleType: 'target' });
  const outputConnections = useNodeConnections({ handleType: 'source' });
  const [portsExpanded, setPortsExpanded] = useState(false);
  const updateNodeInternals = useUpdateNodeInternals();
  const { updateNodeData, getNode } = useReactFlow();
  // 语言在启动时由 localStorage 确定性决定（项目无运行时切换入口），直接读模块级值即可
  const lang = getInitialLang();

  const connectedHandles = useMemo(() => ({
    input: new Set(inputConnections.map((c) => c.targetHandle).filter(Boolean) as string[]),
    output: new Set(outputConnections.map((c) => c.sourceHandle).filter(Boolean) as string[]),
  }), [inputConnections, outputConnections]);

  const selectPorts = (ports: PortLike[], connected: Set<string>) => {
    if (portsExpanded || connection.inProgress || ports.length <= 5) return ports;
    const primary = new Set(ports.filter((port) => port.dataType !== 'Control').slice(0, 3).map((port) => port.name));
    return ports.filter((port) => port.dataType === 'Control' || port.required || connected.has(port.name) || primary.has(port.name));
  };
  const visibleInputs = selectPorts(inputs, connectedHandles.input);
  const visibleOutputs = selectPorts(outputs, connectedHandles.output);
  const hiddenCount = inputs.length + outputs.length - visibleInputs.length - visibleOutputs.length;
  const portSignature = JSON.stringify([visibleInputs.map((p) => p.name), visibleOutputs.map((p) => p.name)]);
  // 展开/折叠后刷新 Handle 坐标，既有连线仍绑定原端口 ID。
  useEffect(() => { updateNodeInternals(id); }, [id, portSignature, updateNodeInternals]);

  // 每个输入口上的入边按种类计数：控制口可多入边（join），数据口只允许 1 条
  const incoming = useMemo(() => {
    const buckets = new Map<string, { control: number; data: number }>();
    for (const incoming of inputConnections) {
      if (!incoming.targetHandle) continue;
      const sourcePort = ((getNode(incoming.source)?.data?.outputs ?? []) as PortLike[])
        .find((port) => port.name === incoming.sourceHandle);
      const bucket = buckets.get(incoming.targetHandle) ?? { control: 0, data: 0 };
      if (sourcePort?.dataType === 'Control') bucket.control += 1; else bucket.data += 1;
      buckets.set(incoming.targetHandle, bucket);
    }
    return buckets;
  }, [inputConnections, getNode]);

  // 必需输入未连接：与 validateWorkflow 同一套语义（控制必需口只认控制线，数据必需口只认数据线）
  const missingRequired = inputs.filter((port) => {
    if (!port.required) return false;
    const bucket = incoming.get(port.name);
    if (!bucket) return true;
    return port.dataType === 'Control' ? bucket.control === 0 : bucket.data === 0;
  }).length;

  // 一条关键结果：按节点类型选主字段（见 summaryText），叠加耗时；失败节点优先显示错误摘要
  const summaryLine = summarizeScalars(d.summary as Record<string, unknown> | undefined, 2,
    lang === 'zh' ? (key: string) => portNames[key] ?? key : undefined, typeKey);

  // 连线进行中：从起点端口解析类型，为对侧端口标注兼容性
  // （允许=绿圈高亮，不允许=降透明度 + 给出禁止原因；与 onConnect / isValidConnection / 预检共用 connectionVerdict）
  const connectingPort = (() => {
    if (!connection.inProgress) return undefined;
    const source = connection.fromHandle.type === 'source';
    const ports = (source ? connection.fromNode?.data?.outputs : connection.fromNode?.data?.inputs) as PortLike[] | undefined;
    const port = ports?.find((item) => item.name === connection.fromHandle.id);
    return { source, fromNodeId: connection.fromNode?.id, dataType: port?.dataType };
  })();

  const verdictFor = (port: PortLike, side: 'input' | 'output') => {
    if (!connectingPort?.dataType) return undefined;
    const isCandidate = connectingPort.source ? side === 'input' : side === 'output';
    if (!isCandidate) return undefined;
    if (connectingPort.fromNodeId === id) return { allowed: false as const, reason: '不能连接节点自身' };
    // 目标是数据输入时带上已有来源数：已占用的输入不再高亮为可连
    const targetDataSources = side === 'input' && port.dataType !== 'Control'
      ? (incoming.get(port.name)?.data ?? 0)
      : 0;
    return connectingPort.source
      ? connectionVerdict({ sourceType: connectingPort.dataType, targetType: port.dataType, targetDataSources })
      : connectionVerdict({ sourceType: port.dataType, targetType: connectingPort.dataType });
  };

  const labelOf = (port: PortLike) => port.label ?? (lang === 'zh' ? portNames[port.name] : undefined) ?? port.name;
  const isConnected = (port: PortLike, side: 'input' | 'output') =>
    side === 'input' ? connectedHandles.input.has(port.name) : connectedHandles.output.has(port.name);
  const portTitle = (port: PortLike, side: 'input' | 'output', verdict?: { allowed: boolean; reason?: string }) => {
    const bits = [labelOf(port), port.dataType];
    if (side === 'input' && port.required && !isConnected(port, side)) bits.push('必需 · 未连接');
    if (verdict && !verdict.allowed) bits.push(verdict.reason ?? '不可连接');
    return bits.join(' · ');
  };

  // Handle 的纵向位置由 CSS 负责（相对端口行垂直居中、水平锚定节点边缘），这里只给连线中的兼容态视觉
  const handleStyle = (verdict?: { allowed: boolean }) => ({
    ...(verdict?.allowed === true ? { boxShadow: '0 0 0 3px rgba(82, 196, 26, 0.45)', borderColor: '#52c41a' } : {}),
    ...(verdict?.allowed === false ? { opacity: 0.3 } : {})
  });

  const renderPortRow = (port: PortLike, side: 'input' | 'output', firstData: boolean) => {
    const connected = isConnected(port, side);
    const verdict = verdictFor(port, side);
    const handle = (
      <Handle
        id={port.name}
        className={port.dataType === 'Control' ? 'control-handle' : 'data-handle'}
        type={side === 'input' ? 'target' : 'source'}
        position={side === 'input' ? Position.Left : Position.Right}
        style={handleStyle(verdict)}
      />
    );
    return (
      <div
        className={`port-row ${port.dataType === 'Control' ? 'control-port' : ''}${firstData ? ' first-data' : ''}`}
        key={port.name}
      >
        {side === 'input' && handle}
        <span title={portTitle(port, side, verdict)}>
          {side === 'input' && port.required && !connected && (
            <em className="port-required" title="必需输入未连接">!</em>
          )}
          <span className="port-label">{labelOf(port)}</span>
        </span>
        {side === 'output' && handle}
      </div>
    );
  };

  // 关键警告（错误 / 必需缺口）单独成段：远缩放时仍显示，只有摘要和耗时会被折叠
  const alertText = errorText
    ? `⚠ ${errorText}`
    : missingRequired > 0
      ? `缺 ${missingRequired} 个必需输入`
      : '';
  const resultText = summaryLine
    ? `${summaryLine}${duration !== undefined ? ` · ${duration.toFixed(1)} ms` : ''}`
    : duration !== undefined
      ? `${duration.toFixed(2)} ms`
      : '';
  // 类型 / 模块版本常驻（不再被摘要顶掉），远缩放下让位给关键警告
  const footerMeta = typeKey === 'module.call' ? `V${String(d.moduleVersion ?? '?')}` : typeKey.split('.').pop() || typeKey;
  const hasDetail = Boolean(errorText || summaryLine);

  return (
    <div className={`vision-node ${selected ? 'selected' : ''} status-${stale ? 'stale' : status} ${typeKey.startsWith('flow.') ? 'flow-node' : ''} ${typeKey === 'module.call' ? 'module-node' : ''}${stale ? ' result-stale' : ''}`} style={{ '--node-accent': nodePresentation(typeKey).color } as CSSProperties}>
      {breakpoint && <span className="breakpoint-dot" title="断点" />}
      <div className="vision-node-title">
        <span className="vision-node-name" title={String(d.label ?? typeKey)}>
          <NodeCategoryIcon type={typeKey} /><span>{String(d.label ?? typeKey)}</span>
        </span>
        <span className="node-title-right">
          <button type="button" className="node-configure nodrag nopan" title="配置节点（也可双击节点）" aria-label={`配置节点：${String(d.label ?? typeKey)}`}
            onDoubleClick={(event) => event.stopPropagation()}
            onClick={(event) => { event.stopPropagation(); configureNode(id); }}>
            <svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" aria-hidden="true">
              <path d="M4 7h16M4 17h16" /><circle cx="9" cy="7" r="3" fill="var(--vs-bg-node-title)" /><circle cx="15" cy="17" r="3" fill="var(--vs-bg-node-title)" />
            </svg>
          </button>
          {disposition && !stale && (
            <span className={`node-disposition ${disposition === 'NG' ? 'ng' : 'ok'}`} title="检测判定（与执行状态分开：算法执行成功也可能判定不合格）">
              {disposition}
            </span>
          )}
          <button
            type="button"
            className={`node-breakpoint-toggle nodrag${breakpoint ? ' active' : ''}`}
            title={breakpoint ? '移除断点' : '在此节点设置断点'}
            aria-label={breakpoint ? '移除断点' : '在此节点设置断点'}
            aria-pressed={breakpoint}
            onClick={(event) => {
              event.stopPropagation();
              updateNodeData(id, { breakpoint: !breakpoint });
            }}
          />
          <span className="node-status" title={stale ? `上次执行状态：${statusText(status)}${disposition ? ` · ${disposition}` : ''}` : undefined}>{stale ? '待运行' : statusText(status)}</span>
        </span>
      </div>
      {stale && <div className="node-stale-message">参数已修改，待运行</div>}
      <div className="vision-node-body">
        <div className="port-column input-column">
          {partitionPorts(visibleInputs).map(({ port, firstData }) => renderPortRow(port, 'input', firstData))}
        </div>
        <div className="port-column output-column">
          {partitionPorts(visibleOutputs).map(({ port, firstData }) => renderPortRow(port, 'output', firstData))}
        </div>
      </div>
      {(hiddenCount > 0 || portsExpanded) && <button type="button" className="node-ports-toggle nodrag nopan" aria-expanded={portsExpanded || connection.inProgress}
        onDoubleClick={(event) => event.stopPropagation()}
        onClick={(event) => { event.stopPropagation(); setPortsExpanded((value) => !value); }}>
        {portsExpanded ? '收起未连接端口' : `展开其余端口（${hiddenCount}）`}
      </button>}
      <div
        className={`vision-node-footer${!errorText && missingRequired > 0 ? ' footer-warning' : ''}${stale ? ' footer-stale' : ''}`}
        title={[errorText, missingRequired > 0 ? `缺 ${missingRequired} 个必需输入（未连接）` : undefined, summaryLine].filter(Boolean).join(' · ') || undefined}
      >
        {/* 关键警告（缺必需输入 / 错误 / 判定）在最前，远缩放时仍保留；类型与版本常驻右侧 */}
        <span className="footer-main">
          {stale && (alertText || resultText) && <span className="footer-stale-mark" title="上一次运行的结果，不代表当前参数">旧结果 · </span>}
          {alertText && <span className="footer-alert">{alertText}</span>}
          {resultText && <span className="footer-summary">{resultText}</span>}
          {hasDetail && (
            <button type="button" className="footer-detail nodrag" title="在弹窗中查看该节点的完整运行结果"
              onClick={(event) => { event.stopPropagation(); showNodeDetails(id); }}>详情</button>
          )}
        </span>
        <span className="footer-meta" title={typeKey}>{footerMeta}</span>
      </div>
    </div>
  );
}
