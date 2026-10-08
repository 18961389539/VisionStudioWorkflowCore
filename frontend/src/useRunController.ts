import { useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react';
import { message } from 'antd';
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, RunResult, WorkflowPayload } from './types';

export type BottomTab = 'result' | 'issues' | 'observability';

type MessageApi = ReturnType<typeof message.useMessage>[0];

/** /api/debug/sessions 系列端点的返回负载：与 RunResult 同形，附带会话标识与操作名（detail 兼容 ProblemDetails）。 */
export type DebugSessionPayload = RunResult & { sessionId?: string; operation?: string; detail?: string };

/** 前端会话视图：运行中由 anyBusy 表达，这里只保留后端持久态。 */
export type DebugSessionView = {
  sessionId: string;
  status: 'halted' | 'completed' | 'faulted';
  haltNodeId?: string;
  error?: string;
  disposition?: string;
};

type Params = {
  workflowPayload: WorkflowPayload;
  nodes: Node[];
  catalog: NodeCatalogItem[];
  selectedId: string;
  nodeLabels: Record<string, string>;
  breakpoints: string[];
  setNodes: Dispatch<SetStateAction<Node[]>>;
  setEdges: Dispatch<SetStateAction<Edge[]>>;
  setBottomTab: Dispatch<SetStateAction<BottomTab>>;
  messageApi: MessageApi;
};

/**
 * 响应是否为一次运行的完整结果（RunResult 结构：runId + success 字段）。
 * 执行完成但失败的结果同样带这些字段（哪怕节点报告为空：计划获取超时、执行前异常等），
 * 必须落地展示；请求被拒绝（租约冲突等 ProblemDetails）没有这些字段，不能进 applyResult。
 */
function isRunResult(data: unknown): boolean {
  if (!data || typeof data !== 'object') return false;
  const candidate = data as Partial<RunResult>;
  return typeof candidate.runId === 'string' && typeof candidate.success === 'boolean';
}

/**
 * 运行 / 调试控制（从 Editor 抽出的独立模块）：
 * - 普通运行与一次性调试运行（run / runNode / runFromHere）共用 execute；
 * - 断点会话（启动到断点 / 继续 / 用缓存输入运行单节点 / 结束）由后端持有流程快照；
 * - 结果落地（applyResult）与"结果对应提交时版本"的过期判定（只比较流程指纹，不含调试选项）也在本模块内。
 */
export function useRunController({
  workflowPayload,
  nodes,
  catalog,
  selectedId,
  nodeLabels,
  breakpoints,
  setNodes,
  setEdges,
  setBottomTab,
  messageApi
}: Params) {
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<RunResult>();
  // 调试会话（后端有状态会话）：启动到断点 / 继续 / 用缓存输入运行单节点 / 结束会话。
  // debugBusy 与 running 合成统一忙碌闸门 anyBusy：任何执行在飞时禁用全部运行/调试入口，防止重复提交。
  const [debugSession, setDebugSession] = useState<DebugSessionView>();
  const [debugBusy, setDebugBusy] = useState(false);
  const [allowSideEffects, setAllowSideEffects] = useState(false);
  const debugSessionRef = useRef<DebugSessionView | undefined>(undefined);
  debugSessionRef.current = debugSession;
  const anyBusy = running || debugBusy;

  // 含 PLC 写入 / 机器人命令（supportsRunNode=false）的流程：仅勾选“允许副作用”后才可启动会话
  const hasSideEffectNodes = useMemo(() => nodes.some((node) => {
    const item = catalog.find((x) => x.type === node.data.typeKey);
    return item?.capabilities?.supportsRunNode === false;
  }), [catalog, nodes]);
  const hasModuleCalls = useMemo(() => nodes.some((node) => node.data.typeKey === 'module.call'), [nodes]);

  // 调试会话创建时的流程指纹：会话在后端持有固定快照，continue / run-node 不重新提交流程，
  // 其结果必须绑定创建时的指纹。与「最近一次请求」指纹分开存——否则会话中断期间被拒绝的
  // 普通运行会污染会话结果的版本标记。
  const debugSessionWorkflowRef = useRef('');

  // 批量加载 / 恢复草稿前静默结束后端会话，避免对旧快照误“继续”
  const endDebugSessionSilently = () => {
    debugSessionWorkflowRef.current = '';
    const current = debugSessionRef.current;
    if (!current) return;
    debugSessionRef.current = undefined;
    setDebugSession(undefined);
    void fetch(`/api/debug/sessions/${current.sessionId}`, { method: 'DELETE' }).catch(() => undefined);
  };

  // 提交指纹：流程指纹与调试选项分开记录 —— /api/run 提交的是流程本身，/api/debug/run 提交的是
  // { workflow, options }；过期判定只比较流程部分，避免拿不同结构比较导致调试结果被永久误报过期。
  const submittedWorkflowRef = useRef('');
  const submittedOptionsRef = useRef('');
  // 当前展示结果对应的流程指纹：与 result 成对定格（applyResult 时显式传入或取最近一次提交）。
  // 不能在提交时就改写判定用的值——否则“运行 B 被拒绝”会让仍在展示的旧结果 A 被错标为不过期。
  const resultWorkflowRef = useRef('');

  const applyResult = (data: RunResult, workflowFingerprint?: string) => {
    // 结果与它的流程指纹成对更新：显式传入结果对应的提交指纹（普通请求 / 调试会话快照），
    // 未传时回退到最近一次请求的指纹（如 JobPanel 直接投递的结果）。
    // 共享的 submittedWorkflowRef 会被后续请求改写，继续旧会话时必须显式传会话创建时的指纹。
    resultWorkflowRef.current = workflowFingerprint ?? submittedWorkflowRef.current;
    setResult(data);
    const byId = new Map((data.nodeReports ?? []).map((x) => [x.nodeId, x]));
    setNodes((current) => current.map((node) => {
      const report = byId.get(node.id);
      const isHalt = data.haltNodeId === node.id;
      // 准确状态：有报告 → 成功/失败（预热单独标注）；断点暂停 → 暂停；
      // 断点会话里未执行的 → 等待（续跑会执行）；其余未执行（未走到的分支 / 一次性调试暂停点之后）→ 未走到
      let status = data.debugState === 'Breakpoint' ? 'pending' : 'skipped';
      if (report) status = report.success ? (report.phase === 'Warmup' ? 'warmup' : 'ok') : 'error';
      if (isHalt && data.debugState === 'Breakpoint') status = 'breakpoint';
      // 节点级判定（OK/NG）与执行状态分开：算法执行成功也可能判定不合格
      const rawDisposition = (report?.summary as Record<string, unknown> | undefined)?.disposition;
      return {
        ...node,
        data: {
          ...node.data,
          status,
          durationMs: report?.durationMs,
          summary: report?.summary,
          error: report?.error ?? undefined,
          disposition: rawDisposition === 'OK' || rawDisposition === 'NG' ? rawDisposition : undefined,
          // 结果刚落地：清掉“过期”标记（本次结果对应当前参数）
          stale: false
        }
      };
    }));
    const decisions = new Map((data.controlFlowDecisions ?? []).map((x) => [x.nodeId, x]));
    setEdges((current) => current.map((edge) => {
      if (edge.data?.kind !== 'control') return edge;
      const decision = decisions.get(edge.source);
      if (!decision) {
        const { className: _className, ...rest } = edge;
        return rest;
      }
      const branch = edge.sourceHandle ?? '';
      const active = decision.activeBranches.includes(branch);
      return {
        ...edge,
        className: active ? 'branch-taken' : 'branch-skipped',
        animated: active
      };
    }));
    setBottomTab('result');
  };

  const execute = async (url: string, body: unknown) => {
    if (anyBusy) return;
    let resultApplied = false;
    setRunning(true);
    // 记录提交时的流程指纹与调试选项（分开存）：结果返回后若画布已改动，RunPanel 提示“结果对应提交时的版本”
    const debugBody = body as { workflow?: unknown; options?: unknown };
    const workflowFingerprint = JSON.stringify(debugBody?.workflow ?? body);
    submittedWorkflowRef.current = workflowFingerprint;
    submittedOptionsRef.current = debugBody?.workflow ? JSON.stringify(debugBody.options ?? null) : '';
    // 提交后节点进入“等待”而不是“运行中”：执行顺序由引擎调度，前端不再假装所有节点同时执行。
    // 同时清掉上一次运行的摘要/错误/判定与过期标记，避免“等待 + 旧 NG / 旧错误”同时出现
    setNodes((current) => current.map((node) => ({
      ...node,
      data: {
        ...node.data,
        status: 'pending',
        durationMs: undefined,
        summary: undefined,
        error: undefined,
        disposition: undefined,
        stale: false
      }
    })));
    setEdges((current) => current.map((edge) => {
      const { className: _className, ...rest } = edge;
      return edge.data?.kind === 'control' ? { ...rest, animated: true } : rest;
    }));
    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
      const data: RunResult & { detail?: string } = await response.json();
      // “执行完成但失败”与“请求被拒绝”必须区分：
      // - 执行失败：后端同样以 400 返回完整结果（nodeReports/overlays/errorCode/判定，报告可为空），
      //   必须落地展示，否则诊断信息被丢弃、节点被清成空闲、界面还留着旧结果；
      // - 请求被拒绝（租约 409 / 校验等 ProblemDetails）没有 RunResult 结构，不能进 applyResult。
      if (!response.ok && !isRunResult(data))
        throw new Error(data.error ?? data.detail ?? `运行失败 (${response.status})`);
      resultApplied = true;
      applyResult(data, workflowFingerprint);
      if (!response.ok) messageApi.error(data.error ?? `运行失败 (${response.status})`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '运行失败');
      if (!resultApplied) {
        setNodes((current) => current.map((node) => ({ ...node, data: { ...node.data, status: 'idle' } })));
      }
    } finally {
      setRunning(false);
    }
  };

  const run = () => execute('/api/run', workflowPayload);

  const runNode = () => {
    if (!selectedId) return messageApi.warning('先选择一个节点');
    return execute('/api/debug/run', {
      workflow: workflowPayload,
      options: { mode: 'RunNode', targetNodeId: selectedId, breakpoints: [] }
    });
  };

  const runFromHere = () => {
    if (!selectedId) return messageApi.warning('先选择一个节点');
    return execute('/api/debug/run', {
      workflow: workflowPayload,
      options: { mode: 'RunFromNode', targetNodeId: selectedId, breakpoints: [] }
    });
  };

  // 调试会话：启动（跑到首个断点并保留现场）→ 继续 / 用缓存输入运行单节点 → 结束会话。
  // 所有入口共用 anyBusy 闸门；会话在后端持有流程快照，界面编辑不影响进行中的会话。
  const startDebugSession = async () => {
    if (anyBusy) return;
    if (hasModuleCalls) { messageApi.warning('含复用模块的流程暂不支持会话调试，请先在“可复用模块”中调试模块内部节点'); return; }
    if (breakpoints.length === 0) { messageApi.warning('请先为至少一个节点设置断点'); return; }
    endDebugSessionSilently(); // 重新开始：先释放旧会话，避免孤儿会话占资源
    setDebugBusy(true);
    const options = { mode: 'Breakpoints', breakpoints, allowSideEffects };
    // 会话基于启动时的流程快照：会话指纹单独记录；continue / run-node 的结果都绑定它，
    // 不会被会话中断期间的其他请求覆盖（画布改动后会话结果仍会被正确标记过期）
    const sessionFingerprint = JSON.stringify(workflowPayload);
    submittedWorkflowRef.current = sessionFingerprint;
    submittedOptionsRef.current = JSON.stringify(options);
    debugSessionWorkflowRef.current = sessionFingerprint;
    // 会话从起点开跑：节点先进入“等待”，结束后再落实际状态；
    // 旧的错误/判定/摘要一并清掉（之前只清耗时，会残留“等待 + 旧错误”）
    setNodes((current) => current.map((node) => ({
      ...node,
      data: {
        ...node.data,
        status: 'pending',
        durationMs: undefined,
        summary: undefined,
        error: undefined,
        disposition: undefined,
        stale: false
      }
    })));
    try {
      const response = await fetch('/api/debug/sessions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ workflow: workflowPayload, options })
      });
      const data: DebugSessionPayload = await response.json();
      if (!response.ok || !data.sessionId) {
        // 会话可能已在后端建立但首段执行失败：保留会话视图以便“结束会话”释放资源，
        // 同时用返回的报告落实际状态（已执行的节点给出成功/失败/未走到）
        if (data.sessionId) {
          setDebugSession({ sessionId: data.sessionId, status: 'faulted', error: data.error ?? undefined });
          applyResult(data, sessionFingerprint);
        } else {
          setNodes((current) => current.map((node) => ({ ...node, data: { ...node.data, status: 'idle' } })));
        }
        throw new Error(data.error ?? data.detail ?? `调试会话启动失败 (${response.status})`);
      }
      setDebugSession({
        sessionId: data.sessionId,
        status: data.debugState === 'Breakpoint' ? 'halted' : 'completed',
        haltNodeId: data.haltNodeId ?? undefined,
        disposition: data.qualityDisposition ?? undefined
      });
      applyResult(data, sessionFingerprint);
      if (data.debugState === 'Breakpoint') {
        const label = data.haltNodeId ? (nodeLabels[data.haltNodeId] ?? data.haltNodeId) : '';
        messageApi.success(`调试会话已启动，停在「${label}」`);
      } else {
        messageApi.success('调试会话已启动并执行完成');
      }
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '调试会话启动失败');
    } finally {
      setDebugBusy(false);
    }
  };

  const refreshDebugSession = async (sessionId: string) => {
    try {
      const response = await fetch(`/api/debug/sessions/${sessionId}`);
      if (response.status === 404) { setDebugSession(undefined); return; }
      if (!response.ok) return;
      const snapshot = await response.json();
      const status = snapshot.state === 'Halted' ? 'halted' : snapshot.state === 'Faulted' ? 'faulted' : 'completed';
      setDebugSession({
        sessionId,
        status,
        haltNodeId: snapshot.haltNodeId ?? undefined,
        error: snapshot.error ?? undefined,
        disposition: snapshot.qualityDisposition ?? undefined
      });
    } catch { /* 网络失败保留现有视图 */ }
  };

  const continueDebugSession = async () => {
    const session = debugSession;
    if (anyBusy || !session || session.status !== 'halted') return;
    setDebugBusy(true);
    try {
      const response = await fetch(`/api/debug/sessions/${session.sessionId}/continue`, { method: 'POST' });
      const data: DebugSessionPayload = await response.json();
      if (!response.ok) {
        if (response.status === 404) { setDebugSession(undefined); messageApi.warning('调试会话已过期，请重新开始'); return; }
        await refreshDebugSession(session.sessionId);
        // 执行完成但失败：完整结果随响应返回（报告可为空），同样落地展示（会话视图已刷新为真实状态）；
        // 绑定会话创建时的流程指纹，而非最近一次请求
        if (isRunResult(data)) applyResult(data, debugSessionWorkflowRef.current);
        throw new Error(data.error ?? data.detail ?? `继续执行失败 (${response.status})`);
      }
      setDebugSession({
        sessionId: session.sessionId,
        status: data.debugState === 'Breakpoint' ? 'halted' : 'completed',
        haltNodeId: data.haltNodeId ?? undefined,
        disposition: data.qualityDisposition ?? undefined
      });
      applyResult(data, debugSessionWorkflowRef.current);
      if (data.debugState === 'Breakpoint') {
        const label = data.haltNodeId ? (nodeLabels[data.haltNodeId] ?? data.haltNodeId) : '';
        messageApi.success(`已继续，停在「${label}」`);
      } else {
        messageApi.success(`调试会话执行完成${data.qualityDisposition ? ` · ${data.qualityDisposition}` : ''}`);
      }
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '继续执行失败');
    } finally {
      setDebugBusy(false);
    }
  };

  const runSelectedNodeInSession = async () => {
    const session = debugSession;
    if (anyBusy || !session) return;
    if (session.status === 'faulted') { messageApi.warning('会话已中断，请先结束会话'); return; }
    if (!selectedId) { messageApi.warning('先选择一个节点'); return; }
    setDebugBusy(true);
    // 单节点运行只会执行这一个节点：精确标“执行”（这是唯一能确定在执行的对象）
    const targetNodeId = selectedId;
    const previousStatus = String(nodes.find((node) => node.id === targetNodeId)?.data.status ?? 'idle');
    const markTarget = (status: string) => setNodes((current) => current.map((node) =>
      node.id === targetNodeId ? { ...node, data: { ...node.data, status } } : node));
    markTarget('running');
    let resultApplied = false;
    try {
      const response = await fetch(`/api/debug/sessions/${session.sessionId}/run-node`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ nodeId: targetNodeId })
      });
      const data: DebugSessionPayload = await response.json();
      if (!response.ok) {
        if (response.status === 404) { markTarget(previousStatus); setDebugSession(undefined); messageApi.warning('调试会话已过期，请重新开始'); return; }
        // 执行完成但失败：完整结果已返回（报告可为空）→ 落地展示（含失败节点的实际状态），不回滚“执行中”标记；
        // 绑定会话创建时的流程指纹，而非最近一次请求
        if (isRunResult(data)) {
          applyResult(data, debugSessionWorkflowRef.current);
          resultApplied = true;
        }
        throw new Error(data.error ?? data.detail ?? `运行节点失败 (${response.status})`);
      }
      applyResult(data, debugSessionWorkflowRef.current);
      resultApplied = true;
      messageApi.success(`已用缓存输入执行「${nodeLabels[targetNodeId] ?? targetNodeId}」`);
    } catch (error) {
      if (!resultApplied) markTarget(previousStatus);
      messageApi.error(error instanceof Error ? error.message : '运行节点失败');
    } finally {
      setDebugBusy(false);
    }
  };

  const endDebugSession = async () => {
    const session = debugSession;
    if (anyBusy || !session) return;
    setDebugBusy(true);
    try {
      const response = await fetch(`/api/debug/sessions/${session.sessionId}`, { method: 'DELETE' });
      if (!response.ok && response.status !== 404) throw new Error(`结束会话失败 (${response.status})`);
      messageApi.info('调试会话已结束，现场已释放');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : '结束会话失败');
    } finally {
      setDebugSession(undefined);
      debugSessionWorkflowRef.current = '';
      setDebugBusy(false);
    }
  };

  const debugStatus: { color: string; text: string } = debugBusy
    ? { color: 'processing', text: '运行中…' }
    : !debugSession
      ? { color: 'default', text: '未开始调试' }
      : debugSession.status === 'halted'
        ? { color: 'gold', text: `断点暂停${debugSession.haltNodeId ? ` · ${nodeLabels[debugSession.haltNodeId] ?? debugSession.haltNodeId}` : ''}` }
        : debugSession.status === 'completed'
          ? { color: 'success', text: `完成${debugSession.disposition ? ` · ${debugSession.disposition}` : ''}` }
          : { color: 'error', text: '已中断' };

  // 结果过期判定：画布 payload 与“当前展示结果对应的流程指纹”不一致 → RunPanel 提示“结果对应提交时的版本”，
  // 避免“改了参数却看着旧结果以为已验证”的误导（调试选项单独留存，不参与比对）
  const resultStale = Boolean(result) && resultWorkflowRef.current !== ''
    && resultWorkflowRef.current !== JSON.stringify(workflowPayload);

  const clearResult = () => {
    resultWorkflowRef.current = '';
    setResult(undefined);
  };

  return {
    running,
    result,
    clearResult,
    applyResult,
    resultStale,
    run,
    runNode,
    runFromHere,
    debugSession,
    debugBusy,
    allowSideEffects,
    setAllowSideEffects,
    anyBusy,
    hasSideEffectNodes,
    hasModuleCalls,
    startDebugSession,
    continueDebugSession,
    runSelectedNodeInSession,
    endDebugSession,
    debugStatus,
    endDebugSessionSilently
  };
}
