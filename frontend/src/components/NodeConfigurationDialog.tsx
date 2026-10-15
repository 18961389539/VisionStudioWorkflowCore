import { useCallback, useEffect, useRef, useState, type CSSProperties } from 'react';
import type { Edge, Node } from '@xyflow/react';
import { Alert, Button, Collapse, Modal, Select, Segmented, Tabs, Tag, Tooltip } from 'antd';
import type { NodeCatalogItem, ParameterDescriptor, PortDescriptor, RunResult, VisionRoi } from '../types';
import NodeCategoryIcon from './NodeCategoryIcon';
import PropertyPanel from './PropertyPanel';
import ImageViewer from './ImageViewer';
import NodeExecutionResult from './NodeExecutionResult';
import SplitHandle from './SplitHandle';
import { useRunImages } from '../useRunImages';
import { cloneParameters, configurationIssues, configurationParameters, parameterFingerprint, withParameter, type NodeParameters } from '../nodeConfigurationState';

export type NodeConfigurationTab = 'parameters' | 'image' | 'details' | 'result';

type Props = {
  node: Node;
  catalogItem: NodeCatalogItem;
  nodes: Node[];
  edges: Edge[];
  open: boolean;
  disabled: boolean;
  onClose: () => void;
  onApply: (parameters: NodeParameters) => boolean | void;
  onPreview: (parameters: NodeParameters) => Promise<RunResult>;
  previewBlockedReason?: string;
  initialTab?: NodeConfigurationTab;
  result?: RunResult;
  resultReceivedAt?: string;
  resultStale?: boolean;
};

function PortTable({ ports, input }: { ports: PortDescriptor[]; input?: boolean }) {
  if (!ports.length) return <p className="node-info-muted">无{input ? '输入' : '输出'}端口</p>;
  return <div className="node-info-table-wrap"><table className="node-info-table">
    <thead><tr><th>端口</th><th>数据类型</th>{input && <th>连接要求</th>}</tr></thead>
    <tbody>{ports.map((port) => <tr key={port.name}>
      <td>{port.label ?? port.name}{port.label && port.label !== port.name && <code>{port.name}</code>}</td>
      <td>{port.dataType}</td>
      {input && <td>{port.required ? '必需' : '可选'}</td>}
    </tr>)}</tbody>
  </table></div>;
}

/** 参数说明行的元信息：默认值 / 取值范围 / 步进——目录里都有，此前说明页只有一句兜底文案。 */
function parameterMeta(parameter: ParameterDescriptor) {
  const bits: string[] = [];
  if (parameter.defaultValue !== undefined && parameter.defaultValue !== null && parameter.defaultValue !== '')
    bits.push(`默认 ${String(parameter.defaultValue)}`);
  if (parameter.min !== undefined || parameter.max !== undefined)
    bits.push(`范围 ${parameter.min ?? '—'} ~ ${parameter.max ?? '—'}`);
  if (parameter.step !== undefined) bits.push(`步进 ${parameter.step}`);
  return bits.join(' · ');
}

export default function NodeConfigurationDialog({ node, catalogItem, nodes, edges, open, disabled, onClose, onApply, onPreview, previewBlockedReason, initialTab = 'parameters', result, resultReceivedAt, resultStale = false }: Props) {
  // result 是节点“详情”的定位入口，结果统一在配置页展示。
  const [activeTab, setActiveTab] = useState<string>(initialTab === 'details' ? 'details' : 'workspace');
  const [maximized, setMaximized] = useState(false);
  const [imageShare, setImageShare] = useState(67);
  const [workspaceWidth, setWorkspaceWidth] = useState(1000);
  const workspaceRef = useRef<HTMLDivElement>(null);
  const columns = (share: number) => `minmax(260px, ${share}fr) 10px minmax(280px, ${100 - share}fr)`;
  const previewShare = (share: number) => workspaceRef.current?.style.setProperty('--node-columns', columns(share));
  const commitShare = (share: number) => { previewShare(share); setImageShare(share); };
  const bindWorkspace = useCallback((workspace: HTMLDivElement | null) => {
    workspaceRef.current = workspace;
    if (!workspace) return;
    const observer = new ResizeObserver(([entry]) => setWorkspaceWidth(Math.max(1, entry.contentRect.width)));
    setWorkspaceWidth(Math.max(1, workspace.getBoundingClientRect().width));
    observer.observe(workspace);
    return () => { observer.disconnect(); workspaceRef.current = null; };
  }, []);
  const resultRef = useRef<HTMLDivElement>(null);
  const focusResult = () => {
    resultRef.current?.focus({ preventScroll: true });
    resultRef.current?.scrollIntoView?.({ block: 'nearest' });
  };
  useEffect(() => setActiveTab(initialTab === 'details' ? 'details' : 'workspace'), [initialTab]);
  useEffect(() => {
    if (open && initialTab === 'result' && activeTab === 'workspace') {
      const frame = requestAnimationFrame(focusResult);
      return () => cancelAnimationFrame(frame);
    }
  }, [open, initialTab, activeTab]);
  const [baseline] = useState(() => configurationParameters((node.data.parameters ?? {}) as NodeParameters, catalogItem));
  const [draft, setDraft] = useState(() => cloneParameters(baseline));
  const [previewNodeId, setPreviewNodeId] = useState(node.id);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [previewError, setPreviewError] = useState<string>();
  const [preview, setPreview] = useState<{ result: RunResult; fingerprint: string; receivedAt: string }>();
  const [focusTick, setFocusTick] = useState(0);
  const draftFingerprint = parameterFingerprint(draft);
  const dirty = draftFingerprint !== parameterFingerprint(baseline);
  // 与打开弹窗时的值不同的参数项数：用于参数区“已改 N 项”与关闭确认
  const changedCount = catalogItem.parameters.filter((parameter) => {
    const current = draft[parameter.name];
    const origin = baseline[parameter.name];
    return typeof current === 'number' && typeof origin === 'number' ? current !== origin : String(current) !== String(origin);
  }).length;
  const [discardOpen, setDiscardOpen] = useState(false);
  const externalChange = parameterFingerprint(configurationParameters((node.data.parameters ?? {}) as NodeParameters, catalogItem)) !== parameterFingerprint(baseline);
  const locked = disabled || previewBusy;
  const issues = configurationIssues(catalogItem, draft);
  const displayedResult = preview?.result ?? result;
  const resultFailed = Boolean(displayedResult && (!displayedResult.success || displayedResult.nodeReports?.filter((report) => report.nodeId === node.id).slice(-1).some((report) => !report.success)));
  const [resultExpanded, setResultExpanded] = useState(() => initialTab === 'result' || resultFailed);
  useEffect(() => { if (initialTab === 'result') setResultExpanded(true); }, [initialTab]);
  // 新的失败报告自动展开；成功报告保留用户当前的展开状态。
  useEffect(() => { if (resultFailed) setResultExpanded(true); }, [displayedResult, resultFailed]);
  // P1（评审）：新的“运行此节点”结果落地后自动展开结果并滚入视口——此前值表格在首屏外
  //（被页脚裁切，需滚动整个弹窗体），参数少时右列还有大片空白。
  useEffect(() => {
    if (!preview) return;
    setResultExpanded(true);
    const frame = requestAnimationFrame(() => resultRef.current?.scrollIntoView?.({ block: 'nearest' }));
    return () => cancelAnimationFrame(frame);
  }, [preview]);
  const receivedAt = preview?.receivedAt ?? resultReceivedAt;
  const stale = Boolean(previewError) || (preview ? preview.fingerprint !== draftFingerprint : resultStale || dirty);
  const nodeLabels = Object.fromEntries(nodes.map((item) => [item.id, String(item.data.label ?? item.id)]));
  const { imageSource, setImageSource, imageViewerSource, viewerOverlays, selectedOutputAvailable, selectedInputAvailable,
    imageCatalogLoading, imageCatalogError, retryImageCatalog } =
    useRunImages({ result: displayedResult, selectedId: previewNodeId, nodes, edges, nodeLabels });
  const imagePorts = [...catalogItem.inputs, ...catalogItem.outputs].some((p) => p.dataType === 'Image' || p.dataType === 'DomainImage');
  const showImage = imagePorts || initialTab === 'image';
  const supportsRoi = catalogItem.capabilities?.supportsRoi === true;
  const canEditRoi = supportsRoi && previewNodeId === node.id && imageSource !== 'preview';
  const draftNode: Node = { ...node, data: { ...node.data, parameters: draft } };
  const changeParameter = (key: string, value: unknown) => {
    if (!locked) setDraft((current) => withParameter(current, key, value));
  };
  const runPreview = async () => {
    if (locked || issues.length || externalChange || previewBlockedReason) return;
    const submitted = cloneParameters(draft);
    setPreviewBusy(true);
    setPreviewError(undefined);
    try {
      const next = await onPreview(submitted);
      setPreview({ result: next, fingerprint: parameterFingerprint(submitted), receivedAt: new Date().toISOString() });
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '节点测试失败。');
      setResultExpanded(true);
    } finally { setPreviewBusy(false); }
  };
  const apply = () => {
    if (locked || issues.length || externalChange || !dirty) return;
    if (onApply(cloneParameters(draft)) !== false) onClose();
  };

  // P0（评审，实测复现）：预览结束后运行按钮短暂 disabled，焦点落回 body——antd Modal 的 Esc
  // 监听依赖弹窗内焦点，于是“运行完按 Esc 关不掉”。完成后把焦点还给弹窗（正在输入参数时不抢焦点）。
  const previewPreviousBusy = useRef(false);
  useEffect(() => {
    const wasBusy = previewPreviousBusy.current;
    previewPreviousBusy.current = previewBusy;
    if (!wasBusy || previewBusy) return;
    const active = document.activeElement as HTMLElement | null;
    if (active && active.closest('input, textarea, select, [contenteditable="true"], .ant-select, .ant-input-number')) return;
    workspaceRef.current?.focus({ preventScroll: true });
  }, [previewBusy]);

  // P1（评审）：弹窗打开时主程序会吞掉 Ctrl+S（preventDefault 后无动作，属“按键黑洞”）。
  // 这里把它变成“应用”，并补 Ctrl+Enter“运行此节点”；用 ref 读取最新闭包，避免每次渲染重注册监听。
  const applyRef = useRef(apply); applyRef.current = apply;
  const runPreviewRef = useRef(runPreview); runPreviewRef.current = runPreview;
  useEffect(() => {
    if (!open) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.altKey) return;
      const key = event.key.toLowerCase();
      if (key === 's') { event.preventDefault(); applyRef.current(); }
      else if (key === 'enter') { event.preventDefault(); runPreviewRef.current(); }
    };
    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [open]);

  // P0（评审）：未应用的修改不能再被 ✕/Esc 静默丢弃——“取消”保持直接废弃语义，
  // 但 ✕ 与 Esc（遮罩关闭本来就没开）统一先确认，避免误按丢失整轮参数调整。
  const requestClose = () => {
    if (dirty) { setDiscardOpen(true); return; }
    onClose();
  };
  const locateRoi = () => {
    setActiveTab('workspace');
    setPreviewNodeId(node.id);
    setImageSource(selectedInputAvailable ? 'input' : 'output');
    setFocusTick((value) => value + 1);
  };
  const details = <div className="node-info">
    <p className="node-info-description">{catalogItem.description || '暂无节点说明。'}</p>
    <h3>输入端口</h3><PortTable ports={catalogItem.inputs} input />
    <h3>输出端口</h3><PortTable ports={catalogItem.outputs} />
    {catalogItem.parameters.length > 0 && <>
      <h3>参数说明</h3>
      <dl className="node-info-parameters">{catalogItem.parameters.map((parameter) => {
        // P1（评审）：说明行补充“默认值 · 范围 · 步进”，描述缺失时不再重复兜底句
        const meta = parameterMeta(parameter);
        return <div key={parameter.name}>
          <dt>{parameter.label}{parameter.unit ? `（${parameter.unit}）` : ''}</dt>
          <dd>
            {parameter.description && <span>{parameter.description}</span>}
            {meta && <span className="node-info-param-meta">{meta}</span>}
            {parameter.options?.length ? <span>可选：{parameter.options.map((option) => option.label).join('、')}</span> : null}
          </dd>
        </div>;
      })}</dl>
    </>}
    <Collapse size="small" items={[{ key: 'technical', label: '技术信息', children: <div className="node-info-technical">
      <span>节点类型：<code>{catalogItem.type}</code></span>
      <span>来源：{catalogItem.pluginId ?? 'builtin'}</span>
      <div>
        {catalogItem.capabilities?.supportsRoi && <Tag>支持 ROI</Tag>}
        {catalogItem.capabilities?.emitsOverlay && <Tag>输出覆盖层</Tag>}
        {catalogItem.capabilities?.deterministic !== undefined && <Tag>{catalogItem.capabilities.deterministic ? '确定性执行' : '非确定性执行'}</Tag>}
      </div>
    </div> }]} />
  </div>;

  return <>
    <Modal open={open} onCancel={requestClose} mask={{ closable: false }} destroyOnHidden
      afterOpenChange={(visible) => { if (visible && initialTab === 'result') focusResult(); }}
      className={'node-configuration-modal' + (maximized ? ' is-maximized' : '')} width={maximized ? '100vw' : 'min(1680px, calc(100vw - 32px))'} style={{ top: maximized ? 0 : 16 }}
      title={<div className="node-configuration-title"><NodeCategoryIcon type={catalogItem.type} /><span>节点配置 · {String(node.data.label ?? catalogItem.displayName)}</span>
        <Button className="node-dialog-maximize" size="small" type="text" aria-label={maximized ? '还原窗口' : '最大化窗口'} title={maximized ? '还原窗口' : '最大化窗口'} onClick={() => setMaximized((value) => !value)}>{maximized ? '⤡' : '⤢'}</Button>
      </div>}
      footer={<div className="node-configuration-footer">
        <span>{dirty ? '修改已暂存；取消或关闭将丢弃本次修改' : '参数与当前流程一致'}{previewBusy ? ' · 测试中' : ''}</span>
        <div>
          <Tooltip title={previewBlockedReason || '使用暂存参数测试此节点，并预热上游输入；不写回流程参数。（Ctrl+Enter）'}>
            <Button onClick={() => void runPreview()} loading={previewBusy} disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason)}>运行此节点</Button>
          </Tooltip>
          <Button onClick={onClose}>取消</Button><Button type="primary" onClick={apply} disabled={locked || !dirty || issues.length > 0 || externalChange} title="应用暂存参数到流程（Ctrl+S）">应用</Button>
        </div>
      </div>}>
      {externalChange && <Alert type="warning" showIcon title="流程中的节点参数已更新，请关闭后重新打开，避免覆盖其他修改。" />}
      <Tabs activeKey={activeTab} onChange={setActiveTab} items={[
        { key: 'workspace', label: '配置', forceRender: true, children: <>
          <div ref={bindWorkspace} tabIndex={-1} className={'node-configuration-workspace' + (showImage ? '' : ' without-image')}
            style={{ '--node-columns': columns(imageShare) } as CSSProperties}>
            {showImage && <section className="node-configuration-image" aria-label="图像预览">
              <div className="node-configuration-image-source">
                <span>预览节点</span><Select size="small" aria-label="预览节点" showSearch optionFilterProp="label" value={previewNodeId}
                  style={{ minWidth: 130, flex: '1 1 130px' }} options={nodes.map((item) => ({ value: item.id, label: nodeLabels[item.id] }))}
                  onChange={(id) => { setPreviewNodeId(id); setImageSource('output'); }} />
                <Segmented size="small" value={imageSource} onChange={(value) => setImageSource(value as 'preview' | 'output' | 'input')}
                  options={[{ label: '最终预览', value: 'preview' }, { label: '输出', value: 'output', disabled: !selectedOutputAvailable }, { label: '输入', value: 'input', disabled: !selectedInputAvailable }]} />
              </div>
              <div className="node-image-provenance"><span>{imageViewerSource.label}</span>
                {imageCatalogLoading && <span>正在加载节点图像…</span>}
                {imageCatalogError && <span role="alert" title={imageCatalogError}>节点图像加载失败 <Button size="small" onClick={retryImageCatalog}>重试加载</Button></span>}
                {receivedAt && <span>更新于 {new Date(receivedAt).toLocaleString()}</span>}
                {stale && displayedResult && <Tag color="gold">上次运行图像 · 当前参数待验证</Tag>}
              </div>
              <div className="node-configuration-viewer"><ImageViewer previewUrl={imageViewerSource.url} imageWidth={imageViewerSource.width} imageHeight={imageViewerSource.height}
                overlays={viewerOverlays} roi={canEditRoi ? draft.roi as VisionRoi | undefined : undefined} showRoiTools={canEditRoi}
                roiEnabled={canEditRoi && !locked && Boolean(imageViewerSource.url)} roiTargetLabel={String(node.data.label ?? node.id)}
                loading={previewBusy} onRoiChange={(roi) => changeParameter('roi', roi)} nodeLabels={nodeLabels} focusTick={focusTick}
                emptyHint="运行此节点以显示图像与矢量覆盖层"
                emptyAction={{
                  label: '运行此节点',
                  onClick: () => void runPreview(),
                  disabled: locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason),
                  title: previewBlockedReason || '使用暂存参数测试此节点（Ctrl+Enter）'
                }} /></div>
            </section>}
            {showImage && <SplitHandle axis="x" label="调整图像与参数宽度" value={imageShare} min={35} max={80} scale={100 / workspaceWidth}
              collapsed={false} collapsible={false} collapseGlyph="" expandGlyph="" onToggleCollapse={() => {}}
              preview={previewShare} commit={commitShare} reset={() => commitShare(67)} />}
            <section className="node-configuration-parameters" aria-label="节点参数">
              {/* 标题只保留“参数（暂存）”（节点名已在弹窗标题与预览行出现三次）；脏状态由“已改 N 项”承担，
                  与“恢复出厂默认”区分开：“还原为打开时”回到打开弹窗那一刻的值 */}
              <div className="node-parameter-heading"><strong>参数（暂存）</strong>
                <span className="node-parameter-tools">
                  {dirty && <Tag color="gold" className="node-parameter-dirty" title="与打开弹窗时的值不同">已改 {changedCount} 项</Tag>}
                  <Button size="small" type="link" disabled={!dirty || locked} onClick={() => setDraft(cloneParameters(baseline))}>还原为打开时</Button>
                </span>
              </div>
              {issues.length > 0 && <Alert type="error" title="请修正参数后再运行或应用" description={issues.join('；')} />}
              <PropertyPanel node={draftNode} catalogItem={catalogItem} onChange={changeParameter} disabled={locked} onLocateRoi={showImage && supportsRoi ? locateRoi : undefined} />
            </section>
          </div>
          <div ref={resultRef} tabIndex={-1} className="node-result-anchor">
            <NodeExecutionResult nodeId={node.id} catalog={catalogItem} result={displayedResult}
              receivedAt={receivedAt} stale={stale} draftResult={Boolean(preview)}
              expanded={resultExpanded} onExpandedChange={setResultExpanded} requestError={previewError} />
          </div>
        </> },
        { key: 'details', label: '详细说明', children: details }
      ]} />
    </Modal>
    {/* P0（评审）：✕ / Esc 丢弃未应用修改前的确认（“取消”按钮保持直接废弃语义） */}
    <Modal open={discardOpen} title="放弃未应用的参数修改？" width={440} centered
      onCancel={() => setDiscardOpen(false)}
      footer={[
        <Button key="edit" onClick={() => setDiscardOpen(false)}>继续编辑</Button>,
        <Button key="discard" danger onClick={() => { setDiscardOpen(false); onClose(); }}>放弃修改</Button>
      ]}>
      <p className="node-discard-hint">当前有 {changedCount} 项参数修改尚未“应用”，关闭弹窗后将被丢弃。若要保留，请返回并点击“应用”。</p>
    </Modal>
  </>;
}
