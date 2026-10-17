import { useState } from 'react';
import { Button, Segmented } from 'antd';
import type { NodeParameters } from '../nodeConfigurationState';
import type { ParameterDescriptor } from '../types';
import type { MeasurementGeometry } from './ImageViewer';
import CaliperSamplingField from './CaliperSamplingField';

export const caliperEdgeQuickParameterNames: readonly string[] = ['sampleWidth', 'edgeThreshold', 'polarity', 'direction', 'refine'];
type SearchLine = Extract<MeasurementGeometry, { type: 'line' }>;
const samplingParameters: ParameterDescriptor[] = [
  { name: 'edgeThreshold', label: '边缘阈值', type: 'number', defaultValue: 12, min: 0, max: 255, step: 1, unit: 'gray' },
  { name: 'sampleWidth', label: '投影长度', type: 'number', defaultValue: 5, min: 1, max: 99, step: 2, unit: 'px' }
];
const lineKey = (line?: SearchLine) => line ? JSON.stringify([line.start, line.end]) : '';

export default function CaliperEdgeQuickSettings({ parameters, parameterDescriptors, disabled, geometry, geometryDisabled,
  geometryHint, onGeometryChange, onChange }: {
  parameters: NodeParameters; parameterDescriptors: ParameterDescriptor[]; disabled: boolean;
  geometry?: SearchLine; geometryDisabled: boolean; geometryHint?: string;
  onGeometryChange: (geometry: SearchLine) => void; onChange: (patch: NodeParameters) => void;
}) {
  const [undo, setUndo] = useState<{ previous: SearchLine; after: string }>();
  const length = geometry ? Math.hypot(geometry.end.x - geometry.start.x, geometry.end.y - geometry.start.y) : 0;
  const editable = !disabled && !geometryDisabled && geometry && Number.isFinite(length) && length >= 2;
  const angle = geometry ? Math.atan2(geometry.end.y - geometry.start.y, geometry.end.x - geometry.start.x) * 180 / Math.PI : 0;
  const setLine = (next: SearchLine) => {
    if (!editable || !geometry || lineKey(next) === lineKey(geometry)) return;
    if ([next.start.x, next.start.y, next.end.x, next.end.y].some(value => !Number.isFinite(value) || Math.abs(value) > 10_000_000)) return;
    setUndo({ previous: geometry, after: lineKey(next) });
    onGeometryChange(next);
  };
  const orient = (degrees: number) => {
    if (!geometry) return;
    const midpoint = { x: (geometry.start.x + geometry.end.x) / 2, y: (geometry.start.y + geometry.end.y) / 2 };
    const radians = degrees * Math.PI / 180;
    const delta = { x: Math.cos(radians) * length / 2, y: Math.sin(radians) * length / 2 };
    setLine({ ...geometry, start: { x: midpoint.x - delta.x, y: midpoint.y - delta.y },
      end: { x: midpoint.x + delta.x, y: midpoint.y + delta.y } });
  };
  const undoAvailable = undo && undo.after === lineKey(geometry);
  const choice = (name: string, fallback: string) => String(parameters[name] ?? fallback).trim().toLowerCase();
  const polarity = choice('polarity', 'any');
  const polarityValue = (value: string) => parameterDescriptors.find(item => item.name === 'polarity')?.options
    ?.find(option => option.value.toLowerCase() === value)?.value ?? ({ any: 'Any', rising: 'Rising', falling: 'Falling' }[value] ?? value);

  return <section className="caliper-circle-quick-settings caliper-edge-quick-settings" aria-label="单边缘卡尺快捷设置">
    <strong>单边缘卡尺快捷设置</strong>
    <div className="caliper-circle-quick-row" role="group" aria-label="搜索线快捷调整">
      <span>搜索线</span><div className="caliper-circle-quick-buttons">
        <Button size="small" disabled={!editable} title="保留中心和长度，调整为水平方向" onClick={() => orient(geometry && geometry.end.x < geometry.start.x ? 180 : 0)}>水平</Button>
        <Button size="small" disabled={!editable} title="保留中心和长度，调整为垂直方向" onClick={() => orient(geometry && geometry.end.y < geometry.start.y ? -90 : 90)}>垂直</Button>
        <Button size="small" disabled={!editable} title="保留中心和长度，逆时针旋转 15°" onClick={() => orient(angle - 15)}>↶ 15°</Button>
        <Button size="small" disabled={!editable} title="保留中心和长度，顺时针旋转 15°" onClick={() => orient(angle + 15)}>↷ 15°</Button>
        <Button size="small" disabled={!editable} title="交换实际起点和终点；暗亮极性将按新的起点→终点解释" onClick={() => geometry && setLine({ ...geometry, start: geometry.end, end: geometry.start })}>交换端点</Button>
        {undoAvailable && <Button size="small" disabled={!editable} onClick={() => {
          if (geometry) onGeometryChange({ ...geometry, start: undo.previous.start, end: undo.previous.end });
          setUndo(undefined);
        }}>撤销选线调整</Button>}
      </div>
    </div>
    {geometry && <output aria-label="搜索线尺寸">长度 {Number(length.toFixed(2))} px · 方向 {Number(angle.toFixed(1))}°（图像坐标）</output>}
    {geometryHint && <p>{geometryHint}</p>}
    <div className="caliper-circle-quick-row">
      <span>边缘极性</span><Segmented size="small" aria-label="卡尺边缘极性" disabled={disabled} value={polarity}
        options={[{ label: '任意', value: 'any' }, { label: '暗→亮', value: 'rising' }, { label: '亮→暗', value: 'falling' }]}
        onChange={value => onChange({ polarity: polarityValue(String(value)) })} />
    </div>
    <div className="caliper-circle-quick-row">
      <span>选边顺序</span><Segmented size="small" aria-label="卡尺选边顺序" disabled={disabled} value={choice('direction', 'strongest')}
        options={[{ label: '最强', value: 'strongest' }, { label: '从起点', value: 'firstfromstart' }, { label: '从终点', value: 'firstfromend' }]}
        onChange={value => onChange({ direction: value })} />
    </div>
    <p>极性始终按蓝色起点 → 紫色终点定义；“从终点”只改变合格边缘的选择顺序。</p>
    <div className="caliper-circle-sampling-controls" role="group" aria-label="卡尺采样参数">
      {samplingParameters.map(fallback => {
        const parameter = { ...fallback, ...parameterDescriptors.find(item => item.name === fallback.name) };
        return <CaliperSamplingField key={parameter.name} parameter={parameter} rawValue={parameters[parameter.name]} disabled={disabled} onChange={onChange} />;
      })}
    </div>
    <div className="caliper-circle-quick-row">
      <span>位置细化</span><Segmented size="small" aria-label="卡尺位置细化" disabled={disabled} value={choice('refine', 'none')}
        options={[{ label: '亚像素', value: 'parabolic' }, { label: '不细化', value: 'none' }]}
        onChange={value => onChange({ refine: value })} />
    </div>
    <p>参数修改后点击“运行此节点”，更新选点与诊断。</p>
  </section>;
}
