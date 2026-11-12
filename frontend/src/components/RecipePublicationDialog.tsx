import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Checkbox, Modal, Space, Spin, Tag, Typography } from 'antd';
import type { JobDescriptor, JobVersionDiff } from '../types';
import { apiErrorMessage, readErrorPayload, responseErrorMessage } from '../apiErrors';
import { apiFetch } from '../apiClient';
import { jobs } from '../endpoints';

export type PublicationTarget = { jobId: string; name: string; version: number; rollback: boolean };
/** 后端发布门禁返回的未成熟节点（2026-11-12）：字段与 NodeMaturityGate.Finding 对齐。 */
type MaturityFinding = { nodeId: string; nodeType: string; nodeName: string; maturity: string; reason: string };
type MaturityBlocked = { message: string; findings: MaturityFinding[] };
const MATURITY_CODE = 'NODE_MATURITY_ACK_REQUIRED';
type Review = { job: JobDescriptor; diff?: JobVersionDiff; blockedReason?: string };
type Props = { target: PublicationTarget; onCancel: () => void; onPublished: (job: JobDescriptor) => void };
const REQUEST_TIMEOUT_MS = 20_000;

async function readJson<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await apiFetch(url, { signal });
  if (!response.ok) throw new Error(await responseErrorMessage(response, `读取发布信息失败（HTTP ${response.status}）`));
  return response.json();
}

/**
 * 识别发布门禁响应。必须逐字段校验：把任意 409 当成"缺确认"会放行真正该拦下的发布，
 * 反过来把门禁当成"条件已变化"则会让工程师陷在重新核对的循环里。
 */
function isMaturityBlocked(payload: unknown): payload is { message: string; findings: MaturityFinding[] } {
  if (payload == null || typeof payload !== 'object') return false;
  const candidate = payload as { code?: unknown; message?: unknown; findings?: unknown };
  if (candidate.code !== MATURITY_CODE || typeof candidate.message !== 'string' || !Array.isArray(candidate.findings)) return false;
  if (candidate.findings.length === 0) return false;
  return candidate.findings.every((item) => item != null && typeof item === 'object'
    && typeof (item as MaturityFinding).nodeId === 'string'
    && typeof (item as MaturityFinding).nodeType === 'string'
    && typeof (item as MaturityFinding).nodeName === 'string'
    && typeof (item as MaturityFinding).maturity === 'string'
    && typeof (item as MaturityFinding).reason === 'string');
}

function maturityMessage(payload: { message?: unknown }): string {
  return typeof payload.message === 'string' && payload.message.trim() ? payload.message.trim()
    : '该版本包含未达到 Stable 的节点，确认后才会发布。';
}

/** 发布对象、对比基线和并发修订一起核对；失败留在对话框内，写入不自动重试。 */
export default function RecipePublicationDialog({ target, onCancel, onPublished }: Props) {
  const [review, setReview] = useState<Review>();
  const [loading, setLoading] = useState(true);
  const [publishing, setPublishing] = useState(false);
  const [error, setError] = useState<string>();
  const [requiresReload, setRequiresReload] = useState(false);
  // 发布门禁：含 Preview/Experimental 节点的版本需要再点一次（勾选后确认）。
  const [maturity, setMaturity] = useState<MaturityBlocked>();
  const [maturityAcknowledged, setMaturityAcknowledged] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const publishingRef = useRef(false);
  const generationRef = useRef(0);
  const mounted = useRef(false);
  const action = target.rollback ? '回滚' : '发布';

  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; };
  }, []);

  useEffect(() => {
    const generation = ++generationRef.current;
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS);
    let active = true;
    setLoading(true);
    setReview(undefined);
    setError(undefined);
    setRequiresReload(false);
    void (async () => {
      try {
        const job = await readJson<JobDescriptor>(jobs.byId(target.jobId), controller.signal);
        if (!active || controller.signal.aborted) return;
        if (job?.id !== target.jobId || typeof job.updatedAt !== 'string' || !Number.isFinite(Date.parse(job.updatedAt)) || !Array.isArray(job.versions)) {
          throw new Error('配方信息不完整，请重新核对。');
        }
        const version = job.versions.find((item) => item.version === target.version);
        if (!version) throw new Error(`目标版本 V${target.version} 不存在，请刷新配方列表。`);
        const current = job.publishedVersion;
        const blockedReason = !version.validation?.accepted ? '目标版本尚未通过数据集验证，不能发布或回滚。'
          : current === target.version ? '目标版本已生效，无需重复发布。'
            : target.rollback && (!current || target.version >= current) ? '回滚只能选择比当前生效版本更早的版本。'
              : undefined;
        let diff: JobVersionDiff | undefined;
        if (!blockedReason && current != null) {
          diff = await readJson<JobVersionDiff>(jobs.diff(target.jobId, current, target.version), controller.signal);
          if (!diff || diff.jobId !== job.id || diff.fromVersion !== current || diff.toVersion !== target.version || !Array.isArray(diff.changes)
            || diff.changes.some((change) => !change || typeof change.kind !== 'string' || typeof change.path !== 'string')) {
            throw new Error('版本差异与发布对象不匹配，请重新核对。');
          }
        }
        if (active && !controller.signal.aborted && generation === generationRef.current) setReview({ job, diff, blockedReason });
      } catch (reason) {
        if (active) setError(controller.signal.aborted ? '发布信息读取超时（20 秒），请重试。' : reason instanceof Error ? reason.message : '发布信息读取失败');
      } finally {
        clearTimeout(timeout);
        if (active) setLoading(false);
      }
    })();
    return () => { active = false; generationRef.current += 1; controller.abort(); clearTimeout(timeout); };
  }, [target.jobId, target.version, target.rollback, attempt]);

  const confirm = async () => {
    if (publishingRef.current || loading || !review || review.blockedReason || requiresReload) return;
    publishingRef.current = true;
    setPublishing(true);
    setError(undefined);
    const generation = generationRef.current;
    const current = () => mounted.current && generation === generationRef.current;
    // 保留后端原始时间精度；不能转为 Date 再序列化，否则会丢失亚毫秒修订。
    const url = target.rollback
      ? jobs.rollbackVersion(target.jobId, String(target.version), review.job.updatedAt)
      : jobs.publishVersion(target.jobId, String(target.version), review.job.updatedAt, Boolean(maturity) && maturityAcknowledged);
    try {
      const response = await apiFetch(url, { method: 'POST', signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) });
      if (!current()) return;
      if (!response.ok) {
        const payload = await readErrorPayload(response);
        // 发布门禁不是"条件已变化"：目标本身没错，缺的是一次显式确认。把它做成可继续的一步，
        // 而不是让工程师在"重新核对"里循环——那样只会训练出"看到红字就再点一次"的习惯。
        if (response.status === 409 && isMaturityBlocked(payload)) {
          if (current()) {
            setMaturity({ message: maturityMessage(payload), findings: payload.findings });
            setMaturityAcknowledged(false);
            setError(undefined);
          }
          return;
        }
        const detail = apiErrorMessage(payload, `${action}失败（HTTP ${response.status}）`);
        if (current()) {
          setError(response.status === 409 ? `配方或发布条件已变化，请重新核对后再确认。${detail}` : detail);
          setRequiresReload(response.status === 409 || response.status >= 500);
        }
        return;
      }
      const published: JobDescriptor = await response.json();
      if (!current()) return;
      if (published?.id !== target.jobId || published.publishedVersion !== target.version || typeof published.updatedAt !== 'string') {
        throw new Error('响应未包含已确认的生效版本');
      }
      onPublished(published);
    } catch (reason) {
      if (current()) {
        setError(`${action}结果未确认，后端可能已经提交。请先重新核对，勿直接重复提交。${reason instanceof Error ? `（${reason.message}）` : ''}`);
        setRequiresReload(true);
      }
    } finally {
      publishingRef.current = false;
      if (mounted.current) setPublishing(false);
    }
  };

  const version = review?.job.versions.find((item) => item.version === target.version);
  const accuracy = version?.validation?.summary?.accuracy;
  const cancel = () => { if (!publishingRef.current) onCancel(); };
  return <Modal open title={`${action} ${review?.job.name ?? target.name} V${target.version}`} width={720}
    closable={!publishing} keyboard={!publishing} mask={{ closable: !publishing }}
    onCancel={cancel}
    footer={<Space>
      <Button disabled={publishing} onClick={cancel}>取消</Button>
      <Button disabled={publishing || loading} onClick={() => { if (!publishingRef.current) setAttempt((value) => value + 1); }}>重新核对</Button>
      <Button type="primary" danger={target.rollback} loading={publishing}
        disabled={loading || !review || Boolean(review.blockedReason) || requiresReload || (Boolean(maturity) && !maturityAcknowledged)} onClick={() => void confirm()}>确认{action}</Button>
    </Space>}>
    <Typography.Paragraph>配方：{target.jobId} · 目标版本 V{target.version}</Typography.Paragraph>
    {loading && <Space><Spin size="small" />正在核对生效版本、验证结论和变更…</Space>}
    {error && <Alert type="error" showIcon title={error} style={{ marginBottom: 12 }} />}
    {review && <>
      <Typography.Paragraph>验证结论：{version?.validation?.accepted ? '通过' : '未通过'}{typeof accuracy === 'number' ? ` · 准确率 ${(accuracy * 100).toFixed(2)}%` : ''}</Typography.Paragraph>
      {review.blockedReason && <Alert type="warning" showIcon title={review.blockedReason} />}
      {maturity && <div className="publication-maturity-gate" style={{ margin: '12px 0' }}>
        <Alert type="warning" showIcon title={maturity.message}
          description={<ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
            {maturity.findings.map((finding) => <li key={finding.nodeId} style={{ marginBottom: 4 }}>
              <Tag color={finding.maturity === 'Experimental' ? 'red' : 'gold'}>{finding.maturity === 'Experimental' ? '实验' : '预览'}</Tag>
              <Typography.Text strong>{finding.nodeName}</Typography.Text>
              <Typography.Text type="secondary">（{finding.nodeType} / {finding.nodeId}）</Typography.Text>
              <div><Typography.Text type="secondary">{finding.reason}</Typography.Text></div>
            </li>)}
          </ul>} />
        <Checkbox checked={maturityAcknowledged} onChange={(event) => setMaturityAcknowledged(event.target.checked)} style={{ marginTop: 8 }}>
          我已了解上述节点尚未达到 Stable，确认由本配方承担现场验证责任
        </Checkbox>
      </div>}
      <Typography.Paragraph strong>{review.job.publishedVersion == null ? '首次发布：当前没有生效版本可比较。' : `当前生效 V${review.job.publishedVersion} → 目标 V${target.version}`}</Typography.Paragraph>
      {review.diff && <pre className="job-diff-pre">{review.diff.changes.length
        ? review.diff.changes.map((change) => `${change.kind}  ${change.path}\n  ${change.before ?? '∅'} → ${change.after ?? '∅'}`).join('\n\n')
        : '没有影响执行语义的变更。'}</pre>}
    </>}
  </Modal>;
}
