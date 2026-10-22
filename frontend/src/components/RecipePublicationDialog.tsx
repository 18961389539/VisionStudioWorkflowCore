import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Modal, Space, Spin, Typography } from 'antd';
import type { JobDescriptor, JobVersionDiff } from '../types';
import { responseErrorMessage } from '../apiErrors';
import { apiFetch } from '../apiClient';
import { API_BASE } from '../endpoints';

export type PublicationTarget = { jobId: string; name: string; version: number; rollback: boolean };
type Review = { job: JobDescriptor; diff?: JobVersionDiff; blockedReason?: string };
type Props = { target: PublicationTarget; onCancel: () => void; onPublished: (job: JobDescriptor) => void };
const REQUEST_TIMEOUT_MS = 20_000;

async function readJson<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await apiFetch(url, { signal });
  if (!response.ok) throw new Error(await responseErrorMessage(response, `读取发布信息失败（HTTP ${response.status}）`));
  return response.json();
}

/** 发布对象、对比基线和并发修订一起核对；失败留在对话框内，写入不自动重试。 */
export default function RecipePublicationDialog({ target, onCancel, onPublished }: Props) {
  const [review, setReview] = useState<Review>();
  const [loading, setLoading] = useState(true);
  const [publishing, setPublishing] = useState(false);
  const [error, setError] = useState<string>();
  const [requiresReload, setRequiresReload] = useState(false);
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
        const job = await readJson<JobDescriptor>(`${API_BASE}/jobs/${encodeURIComponent(target.jobId)}`, controller.signal);
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
          diff = await readJson<JobVersionDiff>(`${API_BASE}/jobs/${encodeURIComponent(target.jobId)}/versions/${current}/diff/${target.version}`, controller.signal);
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
    const path = target.rollback ? `rollback/${target.version}` : `versions/${target.version}/publish`;
    // 保留后端原始时间精度；不能转为 Date 再序列化，否则会丢失亚毫秒修订。
    const url = `${API_BASE}/jobs/${encodeURIComponent(target.jobId)}/${path}?expectedUpdatedAt=${encodeURIComponent(review.job.updatedAt)}`;
    try {
      const response = await apiFetch(url, { method: 'POST', signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) });
      if (!current()) return;
      if (!response.ok) {
        const detail = await responseErrorMessage(response, `${action}失败（HTTP ${response.status}）`);
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
        disabled={loading || !review || Boolean(review.blockedReason) || requiresReload} onClick={() => void confirm()}>确认{action}</Button>
    </Space>}>
    <Typography.Paragraph>配方：{target.jobId} · 目标版本 V{target.version}</Typography.Paragraph>
    {loading && <Space><Spin size="small" />正在核对生效版本、验证结论和变更…</Space>}
    {error && <Alert type="error" showIcon title={error} style={{ marginBottom: 12 }} />}
    {review && <>
      <Typography.Paragraph>验证结论：{version?.validation?.accepted ? '通过' : '未通过'}{typeof accuracy === 'number' ? ` · 准确率 ${(accuracy * 100).toFixed(2)}%` : ''}</Typography.Paragraph>
      {review.blockedReason && <Alert type="warning" showIcon title={review.blockedReason} />}
      <Typography.Paragraph strong>{review.job.publishedVersion == null ? '首次发布：当前没有生效版本可比较。' : `当前生效 V${review.job.publishedVersion} → 目标 V${target.version}`}</Typography.Paragraph>
      {review.diff && <pre className="job-diff-pre">{review.diff.changes.length
        ? review.diff.changes.map((change) => `${change.kind}  ${change.path}\n  ${change.before ?? '∅'} → ${change.after ?? '∅'}`).join('\n\n')
        : '没有影响执行语义的变更。'}</pre>}
    </>}
  </Modal>;
}
