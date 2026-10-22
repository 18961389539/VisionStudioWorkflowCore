import { useState } from 'react';
import { Alert, Button, Space, Tag } from 'antd';
import { apiFetch } from '../apiClient';

type Warning = { code: string; message: string };

type AutoTuneResponse = {
  nodeType: string;
  pyramidLevels: number;
  angleStepDeg: number;
  contrast: number | null;
  minContrast: number;
  optimization: string;
  inspect: {
    levels: number;
    modelPointCount: number;
    coverageRatio: number;
    meanGradient: number;
    quality: string;
    note?: string | null;
    warnings: Warning[];
  };
  warnings: Warning[];
  ignoredParameters: string[];
  apply: Record<string, unknown>;
  provenance: {
    autoTuneVersion: string;
    nativeLibrary: string;
    templateSha256: string;
    templateWidth: number;
    templateHeight: number;
    computedAt: string;
  };
};

type Props = {
  nodeType: 'match.shape' | 'match.template';
  parameters: Record<string, unknown>;
  disabled?: boolean;
  onApply: (values: Record<string, unknown>) => void;
};

const qualityMeta: Record<string, { text: string; color: string }> = {
  good: { text: '良好', color: 'green' },
  thin: { text: '偏薄', color: 'orange' },
  flat: { text: '过于平坦', color: 'orange' },
  'too-few-points': { text: '模型点不足', color: 'red' },
  unavailable: { text: '不可用', color: 'default' }
};

function asNumber(value: unknown, fallback: number) {
  return typeof value === 'number' && Number.isFinite(value) ? value : fallback;
}

function asString(value: unknown, fallback = '') {
  return typeof value === 'string' && value.trim().length > 0 ? value : fallback;
}

async function readError(response: Response) {
  const text = await response.text();
  try {
    const body = JSON.parse(text) as Record<string, unknown>;
    return String(body.detail ?? body.title ?? text);
  } catch {
    return text || `HTTP ${response.status}`;
  }
}

/**
 * 匹配节点自动调参面板：调用 /api/matching/auto-tune 拿 JLVision determine 的推荐值，
 * 展示“当前 vs 推荐 + 模板质量”，一键写入节点参数。应用后把 parameterSource 置为 auto。
 */
export default function MatchingAutoTunePanel({ nodeType, parameters, disabled, onApply }: Props) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [result, setResult] = useState<AutoTuneResponse>();
  const [applied, setApplied] = useState(false);

  const templatePath = asString(parameters.templatePath);
  const range = nodeType === 'match.shape'
    ? asNumber(parameters.trainingAngleRangeDeg, asNumber(parameters.angleRangeDeg, 180))
    : Math.max(
      Math.abs(asNumber(parameters.searchAngleStartDeg, -asNumber(parameters.angleRangeDeg, 0))),
      Math.abs(asNumber(parameters.searchAngleEndDeg, asNumber(parameters.angleRangeDeg, 0)))
    );

  const currentLevels = asNumber(parameters.pyramidLevels, 0);
  const currentStep = asNumber(parameters.angleStepDeg, nodeType === 'match.shape' ? 1 : 5);
  const currentMinContrast = asNumber(parameters.minContrast, 5);

  const run = async () => {
    setBusy(true);
    setError(undefined);
    setApplied(false);
    try {
      const response = await apiFetch('/api/matching/auto-tune', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          nodeType,
          templatePath,
          angleStartDeg: -Math.max(range, 0.1),
          angleRangeDeg: 2 * Math.max(range, 0.1),
          scaleTolerancePercent: nodeType === 'match.shape'
            ? Math.max(
              asNumber(parameters.scaleTolerancePercent, 0),
              asNumber(parameters.scaleToleranceXPercent, 0),
              asNumber(parameters.scaleToleranceYPercent, 0))
            : 0,
          metric: parameters.ignorePolarity ? 'ignore_global_polarity' : 'use_polarity',
          minContrast: currentMinContrast,
          currentNumLevels: currentLevels,
          currentAngleStepDeg: currentStep,
          profile: asString(parameters.autoTuneProfile, 'balanced'),
          lockedParameters: asString(parameters.autoTuneLockedParams)
        })
      });
      if (!response.ok) {
        setError(await readError(response));
        return;
      }
      setResult(await response.json() as AutoTuneResponse);
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause));
    } finally {
      setBusy(false);
    }
  };

  const apply = () => {
    if (!result) return;
    onApply(result.apply);
    setApplied(true);
  };

  const quality = result ? qualityMeta[result.inspect.quality] ?? { text: result.inspect.quality, color: 'default' } : undefined;
  const rows = result ? [
    { label: '金字塔层数', current: currentLevels === 0 ? '自动' : String(currentLevels), next: String(result.pyramidLevels) },
    { label: '角度步长', current: `${currentStep}°`, next: `${result.angleStepDeg}°` },
    { label: '最小特征对比度', current: String(currentMinContrast), next: String(result.minContrast) },
    { label: '对比度（参考，不应用）', current: '自动', next: result.contrast === null ? '未返回' : String(result.contrast) }
  ] : [];

  return <div style={{ marginBottom: 12, padding: '10px 12px', border: '0.5px solid var(--color-border-tertiary)', borderRadius: 8 }}>
    <Space wrap size={8} style={{ marginBottom: result || error ? 8 : 0 }}>
      <Button size="small" onClick={() => void run()} loading={busy}
        disabled={disabled || templatePath.length === 0} title={templatePath ? '调用 JLVision determine 预览推荐参数' : '先填写模板路径'}>
        自动调参（JLVision）
      </Button>
      {templatePath.length === 0 && <span style={{ fontSize: 12, color: 'var(--color-text-secondary)' }}>
        需要模板路径：自动调参从磁盘读取模板，端口输入不参与
      </span>}
      {applied && <Tag color="green">已写入节点参数（未保存）</Tag>}
    </Space>

    {error && <Alert type="error" showIcon title="自动调参失败" description={error} />}

    {result && <>
      <div style={{ display: 'grid', gridTemplateColumns: '1.2fr 1fr 1fr', gap: '4px 12px', fontSize: 12, marginBottom: 8 }}>
        <span style={{ color: 'var(--color-text-secondary)' }}>参数</span>
        <span style={{ color: 'var(--color-text-secondary)' }}>当前</span>
        <span style={{ color: 'var(--color-text-secondary)' }}>推荐</span>
        {rows.map((row) => <span key={row.label} style={{ display: 'contents' }}>
          <span>{row.label}</span>
          <span>{row.current}</span>
          <span style={{ fontWeight: 500 }}>{row.next}</span>
        </span>)}
      </div>

      <Space wrap size={8} style={{ marginBottom: 6 }}>
        {quality && <Tag color={quality.color}>模板质量：{quality.text}</Tag>}
        <span style={{ fontSize: 12, color: 'var(--color-text-secondary)' }}>
          模型点 {result.inspect.modelPointCount} · 覆盖率 {(result.inspect.coverageRatio * 100).toFixed(1)}% ·
          模板 {result.provenance.templateWidth}×{result.provenance.templateHeight}
        </span>
        <Button size="small" type="primary" onClick={apply} disabled={disabled || applied}>应用推荐值</Button>
      </Space>

      {result.warnings.length > 0 && <Alert type="warning" showIcon title="调参提示"
        description={<ul style={{ margin: 0, paddingInlineStart: 18 }}>
          {result.warnings.map((warning) => <li key={warning.code}>{warning.message}</li>)}
        </ul>} />}

      <div style={{ fontSize: 11, color: 'var(--color-text-tertiary)', marginTop: 6 }}>
        引擎 {result.provenance.autoTuneVersion} · 原生库 {result.provenance.nativeLibrary} ·
        模板哈希 {result.provenance.templateSha256.slice(0, 12)}… · {result.provenance.computedAt}
      </div>
    </>}
  </div>;
}
