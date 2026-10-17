import { Button, Segmented } from 'antd';
import { buildCaliperCircleAngles, circleArcSpan, normalizeCaliperAngle, normalizeCaliperCircleArc } from '../caliperCircleSampling';
import type { NodeParameters } from '../nodeConfigurationState';
import type { ParameterDescriptor } from '../types';
import CaliperSamplingField from './CaliperSamplingField';

export const caliperCircleQuickParameterNames: readonly string[] = ['radialBand', 'minContrast', 'sampleWidth'];
const samplingParameters: ParameterDescriptor[] = [
  { name: 'radialBand', label: '径向搜索半宽', type: 'number', defaultValue: 8, min: 1, max: 500, step: 1, unit: 'px' },
  { name: 'minContrast', label: '最小边缘对比度', type: 'number', defaultValue: 8, min: 0, max: 255, step: 1, unit: 'gray' },
  { name: 'sampleWidth', label: '投影长度', type: 'number', defaultValue: 3, min: 1, max: 99, step: 2, unit: 'px' }
];

const arcPresets = [
  { label: '整圆', start: -180, end: 180 },
  { label: '上半圆', start: -180, end: 0 },
  { label: '下半圆', start: 0, end: 180 },
  { label: '左半圆', start: 90, end: 270 },
  { label: '右半圆', start: -90, end: 90 }
];

export default function CaliperCircleQuickSettings({ parameters, parameterDescriptors, disabled, onChange }: {
  parameters: NodeParameters;
  parameterDescriptors?: ParameterDescriptor[];
  disabled: boolean;
  onChange: (patch: NodeParameters) => void;
}) {
  const number = (name: string, fallback: number) => Number.isFinite(Number(parameters[name])) ? Number(parameters[name]) : fallback;
  const start = number('angleStartDeg', -180), end = number('angleEndDeg', 180);
  const { span, fullCircle } = circleArcSpan(start, end);
  const samplingMode = String(parameters.samplingMode ?? (parameters.angleStepDeg !== undefined ? 'step' : 'count')).trim().toLowerCase();
  const angles = buildCaliperCircleAngles({ angleStartDeg: start, angleEndDeg: end, samplingMode,
    caliperCount: number('caliperCount', 72), angleStepDeg: number('angleStepDeg', 5) });
  const minimum = Math.max(3, Math.min(361, Math.floor(number('minValidCalipers', 3) + 0.5)));
  const step = angles.length > 1 ? angles[1] - angles[0] : 0;
  const arcEditable = span > 0;
  const normalizedArc = normalizeCaliperCircleArc(start, end);
  const rotateArc = (degrees: number) => {
    const rotated = normalizeCaliperCircleArc(normalizedArc.start + degrees, normalizedArc.end + degrees);
    onChange({ angleStartDeg: rotated.start, angleEndDeg: rotated.end });
  };

  return <section className="caliper-circle-quick-settings" aria-label="圆卡尺快捷设置">
    <strong>圆卡尺快捷设置</strong>
    <div className="caliper-circle-quick-row" role="group" aria-label="圆弧范围快捷设置">
      <span>圆弧范围</span><div className="caliper-circle-quick-buttons">{arcPresets.map(preset => {
        const selected = preset.end - preset.start === 360 ? fullCircle
          : !fullCircle && Math.abs(span - 180) < 1e-9 && Math.abs(normalizeCaliperAngle(start) - normalizeCaliperAngle(preset.start)) < 1e-9;
        return <Button key={preset.label} size="small" disabled={disabled} type={selected ? 'primary' : 'default'}
          aria-pressed={selected} onClick={() => onChange({ angleStartDeg: preset.start, angleEndDeg: preset.end })}
          title={`${preset.label}：${preset.start}° → ${preset.end}°（图像坐标，顺时针）`}>{preset.label}</Button>;
      })}</div>
    </div>
    <div className="caliper-circle-quick-row" role="group" aria-label="圆弧微调">
      <span>圆弧微调</span><div className="caliper-circle-quick-buttons">
        <Button size="small" disabled={disabled || !arcEditable} onClick={() => rotateArc(-15)}
          title="保留圆弧角度范围，逆时针旋转 15°">逆时针 15°</Button>
        <Button size="small" disabled={disabled || !arcEditable} onClick={() => rotateArc(15)}
          title="保留圆弧角度范围，顺时针旋转 15°">顺时针 15°</Button>
        <Button size="small" disabled={disabled || !arcEditable || fullCircle} onClick={() => {
          const complement = normalizeCaliperCircleArc(normalizedArc.end, normalizedArc.start + 360);
          onChange({ angleStartDeg: complement.start, angleEndDeg: complement.end });
        }} title="切换为同一圆上未选中的另一段圆弧">反选圆弧</Button>
      </div>
    </div>
    <div className="caliper-circle-quick-row" role="group" aria-label="卡尺数量快捷设置">
      <span>卡尺数量</span><div className="caliper-circle-quick-buttons">{[24, 72, 144].map(count => <Button key={count} size="small"
        disabled={disabled || count < minimum} aria-pressed={samplingMode === 'count' && angles.length === count}
        type={samplingMode === 'count' && angles.length === count ? 'primary' : 'default'}
        title={count < minimum ? `少于当前最少有效卡尺数 ${minimum}` : `切换为计数模式，在当前圆弧上均匀放置 ${count} 条卡尺`}
        onClick={() => onChange({ samplingMode: 'count', caliperCount: count })}>{count} 条</Button>)}</div>
    </div>
    <div className="caliper-circle-quick-row">
      <span>边缘选择</span><Segmented size="small" disabled={disabled} value={String(parameters.searchDirection ?? 'strongest').trim().toLowerCase()}
        aria-label="径向边缘选择" options={[{ label: '最强边缘', value: 'strongest' },
          { label: '由内向外', value: 'firstfromstart' }, { label: '由外向内', value: 'firstfromend' }]}
        onChange={value => onChange({ searchDirection: value })} />
    </div>
    <output aria-label="圆卡尺采样摘要">{angles.length} 条卡尺 · {Number(span.toFixed(2))}° 圆弧 · 间距 {Number(step.toFixed(2))}°</output>
    <div className="caliper-circle-sampling-controls" role="group" aria-label="常用采样参数">
      {samplingParameters.map(fallback => {
        const parameter = { ...fallback, ...parameterDescriptors?.find(item => item.name === fallback.name) };
        return <CaliperSamplingField key={parameter.name} parameter={parameter} rawValue={parameters[parameter.name]}
          disabled={disabled} onChange={onChange} />;
      })}
    </div>
    <p>滑动或输入数值可同步预览卡尺；拟合门槛在下方调整。</p>
  </section>;
}
