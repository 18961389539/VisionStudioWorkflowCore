import { useEffect, useMemo, useRef, useState, type Dispatch, type SetStateAction } from 'react';
import { message } from 'antd';
import type { Edge, Node } from '@xyflow/react';
import type { NodeCatalogItem, RunResult, WorkflowPayload } from './types';
import { workflowWithNodeParameters, type NodeParameters } from './nodeConfigurationState';
import { apiErrorMessage, responseErrorMessage } from './apiErrors';
import { isRunResult } from './runResult';
import { apiFetch } from './apiClient';
import { debug as debugEndpoints, apiUrl } from './endpoints';

export type BottomTab = 'result' | 'issues' | 'observability';

type MessageApi = ReturnType<typeof message.useMessage>[0];

/** /api/debug/sessions 系列端点的返回负载：与 RunResult 同形，附带会话标识与操作名（detail 兼容 ProblemDetails）。 */
export type DebugSessionPayload = RunResult & { sessionId?: string; operation?: string; detail?: string };

/** 前端会话视图：faulted 也包括请求结果或资源释放未确认的状态。 */
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


type Operation = { kind: 'run' | 'preview' | 'debug' | 'close'; epoch: number; controller: AbortController };

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
  const [resultReceivedAt, setResultReceivedAt] = useState<string>();
  const operationRef = useRef<Operation | undefined>(undefined);
  const epochRef = useRef(0);
  const mounted = useRef(false);
  const detachedSessionRef = useRef(false);
  const releasePendingRef = useRef(false);
  const currentWorkflowRef = useRef('');
  currentWorkflowRef.current = JSON.stringify(workflowPayload);
  // 调试会话（后端有状态会话）：启动到断点 / 继续 / 用缓存输入运行单节点 / 结束会话。
  // anyBusy 供界面禁用入口，operationRef 在事件内同步互斥，避免重渲染前重复提交。
  const [debugSession, setDebugSession] = useState<DebugSessionView>();
  const [debugBusy, setDebugBusy] = useState(false);
  const [allowSideEffects, setAllowSideEffects] = useState(false);
  const debugSessionRef = useRef<DebugSessionView | undefined>(undefined);
  const updateSession = (value: DebugSessionView | undefined) => {
    debugSessionRef.current = value;
    if (mounted.current) setDebugSession(value);
  };
  const anyBusy = running || debugBusy;
  // 取消请求会通知后端 CancellationToken；前端不据此断言物理设备已经停止。
  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      epochRef.current += 1;
      // 已发出的删除应有机会完成；其它执行请求取消后再尝试释放已知会话。
      if (operationRef.current?.kind !== 'close') operationRef.current?.controller.abort();
      const session = debugSessionRef.current;
      if (session && operationRef.current?.kind !== 'close') {
        void apiFetch(debugEndpoints.session(session.sessionId), {
          method: 'DELETE', signal: AbortSignal.timeout(15_000), keepalive: true,
        }).catch(() => undefined);
      }
    };
  }, []);

  const begin = (kind: Operation['kind']): Operation | undefined => {
    if (!mounted.current || operationRef.current) return undefined;
    const operation = { kind, epoch: epochRef.current, controller: new AbortController() };
    operationRef.current = operation;
    setRunning(kind === 'run' || kind === 'preview');
    setDebugBusy(kind === 'debug' || kind === 'close');
    return operation;
  };
  const isCurrent = (operation: Operation) => mounted.current && operationRef.current === operation
    && operation.epoch === epochRef.current && !operation.controller.signal.aborted;
  const finish = (operation: Operation) => {
    if (operationRef.current !== operation) return;
    operationRef.current = undefined;
    if (!mounted.current) return;
    setRunning(false);
    setDebugBusy(false);
    if (releasePendingRef.current) {
      releasePendingRef.current = false;
      if (debugSessionRef.current) void endDebugSession(true);
    }
  };

  const resetExecutionMarks = () => {
    setNodes((current) => current.map((node) => ({ ...node, data: { ...node.data, status: 'idle' } })));
    setEdges((current) => current.map((edge) => edge.data?.kind === 'control' ? { ...edge, animated: false } : edge));
  };

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
    detachedSessionRef.current = true;
    const current = debugSessionRef.current;
    if (current) updateSession({ ...current, status: 'faulted', error: '文档已切换，旧调试会话等待释放。' });
    if (operationRef.current?.kind === 'close') return;
    releasePendingRef.current = true;
    if (!operationRef.current) {
      releasePendingRef.current = false;
      if (current) void endDebugSession(true);
    }
  };

  // 当前展示结果对应的流程指纹：与 result 成对定格，来源未知时保持为空。
  // 不能在提交时就改写判定用的值——否则“运行 B 被拒绝”会让仍在展示的旧结果 A 被错标为不过期。
  const resultWorkflowRef = useRef('');

  const applyResult = (data: RunResult, workflowFingerprint?: string) => {
    if (!mounted.current || !isRunResult(data)) return;
    // 结果与来源快照成对落地；来源未知时不得借用最近一次运行的指纹冒充当前流程验证。
    resultWorkflowRef.current = workflowFingerprint ?? '';
    const stale = !workflowFingerprint || workflowFingerprint !== currentWorkflowRef.current;
    setResult(data);
    setResultReceivedAt(new Date().toISOString());
    if (!workflowFingerprint) { setBottomTab('result'); return; }
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
          // 参数可能在执行期间改变；节点报告也必须标记实际快照是否过期。
          stale
        }
      };
    }));
    const decisions = new Map((data.controlFlowDecisions ?? []).map((x) => [x.nodeId, x]));
    setEdges((current) => current.map((edge) => {
      if (edge.data?.kind !== 'control') return edge;
      const decision = decisions.get(edge.source);
      if (!decision) {
        const { className: _className, ...rest } = edge;
        return { ...rest, animated: false };
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
    if (debugSessionRef.current) { messageApi.warning('请先结束当前调试会话，再运行流程。'); return; }
    const operation = begin('run');
    if (!operation) return;
    const debugBody = body as { workflow?: unknown };
    const workflowFingerprint = JSON.stringify(debugBody?.workflow ?? body);
    setNodes((current) => current.map((node) => ({ ...node, data: {
      ...node.data, status: 'pending', durationMs: undefined, summary: undefined,
      error: undefined, disposition: undefined, stale: false,
    } })));
    setEdges((current) => current.map((edge) => {
      const { className: _className, ...rest } = edge;
      return edge.data?.kind === 'control' ? { ...rest, animated: true } : rest;
    }));
    let resultApplied = false;
    try {
      const response = await apiFetch(url, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body), signal: operation.controller.signal,
      });
      const data = await response.json();
      if (!isCurrent(operation)) return;
      if (!isRunResult(data)) throw new Error(apiErrorMessage(data, `未返回有效运行结果（HTTP ${response.status}）`));
      // 执行失败的完整报告仍需展示；请求拒绝或无效成功响应不能覆盖旧结果。
      applyResult(data, workflowFingerprint);
      resultApplied = true;
      if (!response.ok) messageApi.error(apiErrorMessage(data, `运行失败（HTTP ${response.status}）`));
    } catch (error) {
      if (!mounted.current || operation.epoch !== epochRef.current) return;
      if (operation.controller.signal.aborted) messageApi.info('已发送中止请求，请以运行追溯及设备实际状态确认收尾结果。');
      else messageApi.error(error instanceof Error ? error.message : '运行失败');
    } finally {
      if (mounted.current && operation.epoch === epochRef.current && !resultApplied) resetExecutionMarks();
      finish(operation);
    }
  };

  const run = () => execute(apiUrl('run'), workflowPayload);
  const abortRun = () => {
    const operation = operationRef.current;
    if (operation?.kind === 'run' || operation?.kind === 'preview') operation.controller.abort();
  };

  const previewNode = async (nodeId: string, parameters: NodeParameters): Promise<RunResult> => {
    if (operationRef.current) throw new Error('已有运行正在进行，请稍后再试。');
    if (debugSessionRef.current) throw new Error('请先结束当前调试会话，再测试暂存参数。');
    const target = nodes.find((node) => node.id === nodeId);
    const metadata = catalog.find((item) => item.type === target?.data.typeKey);
    if (!target || !metadata) throw new Error('待测试节点不可用。');
    if (target.data.typeKey === 'module.call' || metadata.capabilities?.supportsRunNode === false) throw new Error('此节点不支持单节点测试，请应用参数后使用流程运行入口。');
    const workflow = workflowWithNodeParameters(workflowPayload, nodeId, parameters);
    const operation = begin('preview');
    if (!operation) throw new Error('运行控制器已关闭。');
    let timedOut = false;
    const timeout = setTimeout(() => { timedOut = true; operation.controller.abort(); }, 60_000);
    try {
      const response = await apiFetch(debugEndpoints.run, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, signal: operation.controller.signal,
        body: JSON.stringify({ workflow, options: { mode: 'RunNode', targetNodeId: nodeId, breakpoints: [] } }),
      });
      const data = await response.json();
      if (!isCurrent(operation)) throw new Error('节点测试已取消或流程已切换。');
      if (!isRunResult(data)) throw new Error(apiErrorMessage(data, `未返回有效节点测试结果（HTTP ${response.status}）`));
      return data;
    } catch (error) {
      if (operation.controller.signal.aborted) throw new Error(timedOut
        ? '节点测试请求超时，请检查运行状态后重试。' : '节点测试已取消。');
      throw error;
    } finally {
      clearTimeout(timeout);
      finish(operation);
    }
  };

  const runNode = () => {
    if (!selectedId) return messageApi.warning('先选择一个节点');
    return execute(apiUrl('debug/run'), { workflow: workflowPayload, options: { mode: 'RunNode', targetNodeId: selectedId, breakpoints: [] } });
  };
  const runFromHere = () => {
    if (!selectedId) return messageApi.warning('先选择一个节点');
    return execute(apiUrl('debug/run'), { workflow: workflowPayload, options: { mode: 'RunFromNode', targetNodeId: selectedId, breakpoints: [] } });
  };

  // 切换文档后才返回的会话只用于释放；不允许把现场或结果应用到新文档。
  const discardCreatedSession = (sessionId: string, fingerprint: string) => {
    if (!mounted.current) {
      void apiFetch(debugEndpoints.session(sessionId), {
        method: 'DELETE', signal: AbortSignal.timeout(15_000), keepalive: true,
      }).catch(() => undefined);
      return;
    }
    detachedSessionRef.current = true;
    debugSessionWorkflowRef.current = fingerprint;
    updateSession({ sessionId, status: 'faulted', error: '文档已切换，旧调试会话等待释放。' });
    releasePendingRef.current = true;
  };

  const startDebugSession = async () => {
    if (operationRef.current) return;
    if (debugSessionRef.current) { messageApi.warning('请先结束当前调试会话。'); return; }
    if (hasModuleCalls) { messageApi.warning('含复用模块的流程暂不支持会话调试，请先在“可复用模块”中调试模块内部节点'); return; }
    if (breakpoints.length === 0) { messageApi.warning('请先为至少一个节点设置断点'); return; }
    const operation = begin('debug');
    if (!operation) return;
    detachedSessionRef.current = false;
    const sessionFingerprint = JSON.stringify(workflowPayload);
    const options = { mode: 'Breakpoints', breakpoints, allowSideEffects };
    setNodes((current) => current.map((node) => ({ ...node, data: {
      ...node.data, status: 'pending', durationMs: undefined, summary: undefined,
      error: undefined, disposition: undefined, stale: false,
    } })));
    let resultApplied = false;
    try {
      const response = await apiFetch(debugEndpoints.sessions, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ workflow: workflowPayload, options }), signal: operation.controller.signal,
      });
      const data: DebugSessionPayload = await response.json();
      const sessionId = typeof data?.sessionId === 'string' && data.sessionId ? data.sessionId : undefined;
      if (!isCurrent(operation)) {
        if (sessionId) discardCreatedSession(sessionId, sessionFingerprint);
        return;
      }
      if (sessionId) {
        debugSessionWorkflowRef.current = sessionFingerprint;
        updateSession({
          sessionId, status: !response.ok || !isRunResult(data) || !data.success ? 'faulted' : data.debugState === 'Breakpoint' ? 'halted' : 'completed',
          haltNodeId: data.haltNodeId ?? undefined, error: data.error ?? undefined,
          disposition: data.qualityDisposition ?? undefined,
        });
      }
      if (!sessionId || !isRunResult(data)) throw new Error(apiErrorMessage(data, `未返回有效调试会话（HTTP ${response.status}）`));
      applyResult(data, sessionFingerprint);
      resultApplied = true;
      if (!response.ok) throw new Error(apiErrorMessage(data, `调试会话启动失败（HTTP ${response.status}）`));
      messageApi.success(data.debugState === 'Breakpoint'
        ? `调试会话已启动，停在「${data.haltNodeId ? (nodeLabels[data.haltNodeId] ?? data.haltNodeId) : ''}」`
        : '调试会话已启动并执行完成');
    } catch (error) {
      if (isCurrent(operation)) {
        if (!resultApplied) resetExecutionMarks();
        messageApi.error(error instanceof Error ? error.message : '调试会话启动失败');
      }
    } finally { finish(operation); }
  };

  const forgetSession = (sessionId: string) => {
    if (debugSessionRef.current?.sessionId !== sessionId) return;
    updateSession(undefined);
    debugSessionWorkflowRef.current = '';
    detachedSessionRef.current = false;
  };
  const refreshDebugSession = async (sessionId: string, operation: Operation) => {
    try {
      const response = await apiFetch(debugEndpoints.session(sessionId), {
        signal: AbortSignal.any([operation.controller.signal, AbortSignal.timeout(15_000)]),
      });
      if (!isCurrent(operation)) return false;
      if (response.status === 404) { forgetSession(sessionId); return true; }
      if (!response.ok) return false;
      const snapshot = await response.json();
      if (!isCurrent(operation) || debugSessionRef.current?.sessionId !== sessionId) return false;
      if (!snapshot || snapshot.sessionId !== sessionId || !['Halted', 'Faulted', 'Completed'].includes(snapshot.state)) return false;
      updateSession({
        sessionId, status: snapshot.state === 'Halted' ? 'halted' : snapshot.state === 'Faulted' ? 'faulted' : 'completed',
        haltNodeId: snapshot.haltNodeId ?? undefined, error: snapshot.error ?? undefined,
        disposition: snapshot.qualityDisposition ?? undefined,
      });
      return true;
    } catch { return false; }
  };

  const continueDebugSession = async () => {
    const session = debugSessionRef.current;
    if (!session || session.status !== 'halted' || detachedSessionRef.current) return;
    const operation = begin('debug');
    if (!operation) return;
    const fingerprint = debugSessionWorkflowRef.current;
    let sessionConfirmed = false;
    try {
      const response = await apiFetch(debugEndpoints.continue(session.sessionId), {
        method: 'POST', signal: operation.controller.signal,
      });
      const data: DebugSessionPayload = await response.json();
      if (!isCurrent(operation)) return;
      if (response.status === 404) { forgetSession(session.sessionId); messageApi.warning('调试会话已过期，请重新开始'); return; }
      if (!response.ok) {
        sessionConfirmed = await refreshDebugSession(session.sessionId, operation);
        if (!isCurrent(operation)) return;
        if (isRunResult(data)) applyResult(data, fingerprint);
        throw new Error(apiErrorMessage(data, `继续执行失败（HTTP ${response.status}）`));
      }
      if (!isRunResult(data)) throw new Error('继续执行未返回有效运行结果。');
      updateSession({
        sessionId: session.sessionId, status: data.debugState === 'Breakpoint' ? 'halted' : 'completed',
        haltNodeId: data.haltNodeId ?? undefined, disposition: data.qualityDisposition ?? undefined,
      });
      applyResult(data, fingerprint);
      messageApi.success(data.debugState === 'Breakpoint'
        ? `已继续，停在「${data.haltNodeId ? (nodeLabels[data.haltNodeId] ?? data.haltNodeId) : ''}」`
        : `调试会话执行完成${data.qualityDisposition ? ` · ${data.qualityDisposition}` : ''}`);
    } catch (error) {
      if (isCurrent(operation)) {
        const detail = error instanceof Error ? error.message : '继续执行失败';
        if (!sessionConfirmed && debugSessionRef.current?.sessionId === session.sessionId) updateSession({ ...session, status: 'faulted', error: detail });
        messageApi.error(detail);
      }
    } finally { finish(operation); }
  };

  const runSelectedNodeInSession = async () => {
    const session = debugSessionRef.current;
    if (!session || detachedSessionRef.current) return;
    if (session.status === 'faulted') { messageApi.warning('会话已中断，请先结束会话'); return; }
    if (!selectedId) { messageApi.warning('先选择一个节点'); return; }
    const operation = begin('debug');
    if (!operation) return;
    const fingerprint = debugSessionWorkflowRef.current;
    const targetNodeId = selectedId;
    const previousStatus = String(nodes.find((node) => node.id === targetNodeId)?.data.status ?? 'idle');
    const markTarget = (status: string) => setNodes((current) => current.map((node) =>
      node.id === targetNodeId ? { ...node, data: { ...node.data, status } } : node));
    markTarget('running');
    let resultApplied = false;
    let sessionConfirmed = false;
    try {
      const response = await apiFetch(debugEndpoints.runNode(session.sessionId), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ nodeId: targetNodeId }), signal: operation.controller.signal,
      });
      const data: DebugSessionPayload = await response.json();
      if (!isCurrent(operation)) return;
      if (response.status === 404) { forgetSession(session.sessionId); messageApi.warning('调试会话已过期，请重新开始'); return; }
      if (!response.ok) {
        sessionConfirmed = await refreshDebugSession(session.sessionId, operation);
        if (!isCurrent(operation)) return;
        if (isRunResult(data)) { applyResult(data, fingerprint); resultApplied = true; }
        throw new Error(apiErrorMessage(data, `运行节点失败（HTTP ${response.status}）`));
      }
      if (!isRunResult(data)) throw new Error('节点运行未返回有效运行结果。');
      applyResult(data, fingerprint);
      resultApplied = true;
      messageApi.success(`已用缓存输入执行「${nodeLabels[targetNodeId] ?? targetNodeId}」`);
    } catch (error) {
      if (isCurrent(operation)) {
        const detail = error instanceof Error ? error.message : '运行节点失败';
        if (!sessionConfirmed && debugSessionRef.current?.sessionId === session.sessionId) updateSession({ ...session, status: 'faulted', error: detail });
        messageApi.error(detail);
      }
    } finally {
      if (isCurrent(operation) && !resultApplied) markTarget(previousStatus);
      finish(operation);
    }
  };

  // 失败时保留会话 ID；仅确认删除或 404 后才清除，用户可以重试释放资源。
  const endDebugSession = async (silent = false) => {
    const session = debugSessionRef.current;
    if (!session) return;
    const operation = begin('close');
    if (!operation) return;
    releasePendingRef.current = false;
    try {
      const response = await apiFetch(debugEndpoints.session(session.sessionId), {
        method: 'DELETE', signal: AbortSignal.any([operation.controller.signal, AbortSignal.timeout(15_000)]),
      });
      if (!response.ok && response.status !== 404) throw new Error(await responseErrorMessage(response, `结束会话失败（HTTP ${response.status}）`));
      forgetSession(session.sessionId);
      if (mounted.current && !silent) messageApi.info('调试会话已结束，现场已释放');
    } catch (error) {
      if (mounted.current && debugSessionRef.current?.sessionId === session.sessionId) {
        const detail = error instanceof Error ? error.message : '网络请求失败';
        const errorMessage = `调试会话释放未确认，请点击“结束调试会话”重试：${detail}`;
        updateSession({ ...session, status: 'faulted', error: errorMessage });
        messageApi.error(errorMessage);
      }
    } finally { finish(operation); }
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
  const resultStale = Boolean(result) && (!resultWorkflowRef.current
    || resultWorkflowRef.current !== currentWorkflowRef.current);

  const clearResult = () => {
    epochRef.current += 1;
    const operation = operationRef.current;
    if (operation?.kind === 'run' || operation?.kind === 'preview') operation.controller.abort();
    endDebugSessionSilently();
    resultWorkflowRef.current = '';
    setResult(undefined);
    setResultReceivedAt(undefined);
  };

  return {
    running,
    result,
    resultReceivedAt,
    clearResult,
    applyResult,
    resultStale,
    run,
    abortRun,
    runNode,
    previewNode,
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
