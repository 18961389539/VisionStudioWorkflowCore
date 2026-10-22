/**
 * 自动调参结果的只读展示。运行摘要里出现 autoTuneNcc / autoTuneShape 时才渲染。
 * 与配置对话框里的预览面板不同，这里展示的是「本次运行真正用了什么」，
 * 便于现场核对推荐值是否按预期生效。
 */
const warningText: Record<string, string> = {
  'levels-missing': '原生未返回层数，改用尺寸预算',
  'levels-clamped': '推荐层数超出模板尺寸预算，已收缩',
  'levels-locked': '层数已锁定，保留当前值',
  'angle-step-missing': '原生未返回角步长，保留当前值',
  'angle-step-clamped': '推荐角步长超出范围，已钳制',
  'angle-step-locked': '角步长已锁定，保留当前值',
  'min-contrast-locked': '最小对比度已锁定，保留当前值',
  'contrast-lock-ignored': '对比度无可锁定的节点基线，保留推荐值',
  'profile-speed': '速度取向：层数减一、步长加倍',
  'profile-accuracy': '精度取向：层数加一、步长减半',
  'coarser-than-current': '推荐步长比当前更粗，搜索可能更快但定位更粗',
  'roi-mask-not-cropped': '模板 ROI 只作掩膜未裁剪，层数可能偏深一层',
  'flat-template': '模板过于平坦，特征点少',
  'thin-model': '模型点偏少，模板边缘信息不足',
  'too-few-model-points': '模型点数不足，建议扩大 ROI 或降低最小对比度',
  'inspect-unavailable': '模板自检不可用，质量评级缺失'
};

type AutoTuneReport = {
  version?: unknown;
  profile?: unknown;
  pyramidLevels?: unknown;
  angleStepDeg?: unknown;
  minContrast?: unknown;
  contrast?: unknown;
  quality?: unknown;
  warnings?: unknown;
};

const qualityText: Record<string, { label: string; color: string }> = {
  good: { label: '良好', color: '#389e0d' },
  thin: { label: '偏薄', color: '#d48806' },
  flat: { label: '过于平坦', color: '#d48806' },
  'too-few-points': { label: '点数不足', color: '#cf1322' }
};

function asReport(value: unknown): AutoTuneReport | undefined {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as AutoTuneReport : undefined;
}

function num(value: unknown, digits = 0): string {
  const parsed = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(parsed)) return '—';
  return digits > 0 ? parsed.toFixed(digits) : String(parsed);
}

export default function MatchingAutoTuneReport({ summary }: { summary: Record<string, unknown> }) {
  const ncc = asReport(summary.autoTuneNcc);
  const shape = asReport(summary.autoTuneShape);
  const report = shape ?? ncc;
  if (!report) return null;

  const kind = shape ? 'match.shape（legacy 运行期）' : 'match.template（运行期）';
  const warnings = Array.isArray(report.warnings) ? report.warnings.filter((item): item is string => typeof item === 'string') : [];
  const quality = typeof report.quality === 'string' ? qualityText[report.quality] : undefined;

  return <div className="node-result-auto-tune" aria-label="自动调参结果">
    <div className="node-result-scalars">
      <span><b>自动调参</b> {kind} · {String(report.version ?? '—')} · 取向 {String(report.profile ?? 'balanced')}</span>
      <span><b>层数</b> {num(report.pyramidLevels)}</span>
      <span><b>角步长</b> {num(report.angleStepDeg, 3)}°</span>
      {report.minContrast !== undefined && <span><b>最小对比度</b> {num(report.minContrast)}</span>}
      {report.contrast !== undefined && report.contrast !== null && <span><b>对比度参考</b> {num(report.contrast)}（不应用）</span>}
      {quality && <span><b>模板质量</b> <span style={{ color: quality.color }}>{quality.label}</span></span>}
    </div>
    {warnings.length > 0 && <div className="node-result-scalars" aria-label="自动调参提醒">
      {warnings.map(code => <span key={code}><b>{code}</b> {warningText[code] ?? '见原始诊断'}</span>)}
    </div>}
  </div>;
}

export { warningText as matchingAutoTuneWarningText };
