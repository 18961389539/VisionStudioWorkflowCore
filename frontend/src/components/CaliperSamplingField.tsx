import { useId } from 'react';
import { Button, InputNumber, Slider, Tooltip } from 'antd';
import { effectiveSampleWidth } from '../caliperCircleSampling';
import type { NodeParameters } from '../nodeConfigurationState';
import type { ParameterDescriptor } from '../types';
import { validateParameterValues } from '../validation';

export default function CaliperSamplingField({ parameter, rawValue, disabled, onChange }: {
  parameter: ParameterDescriptor;
  rawValue: unknown;
  disabled: boolean;
  onChange: (patch: NodeParameters) => void;
}) {
  const hintId = useId();
  const value = rawValue === undefined ? parameter.defaultValue : rawValue;
  const numeric = value === null || value === '' || !Number.isFinite(Number(value)) ? null : Number(value);
  const issue = validateParameterValues([parameter], { [parameter.name]: value })[0]?.message;
  const minimum = parameter.min ?? 0, maximum = parameter.max ?? 255;
  const sliderValue = Math.min(maximum, Math.max(minimum, numeric ?? Number(parameter.defaultValue)));
  const projectionWidth = parameter.name === 'sampleWidth' ? effectiveSampleWidth(sliderValue, Number(parameter.defaultValue)) : undefined;
  const change = (next: number | null) => onChange({ [parameter.name]: next });

  return <div className="caliper-circle-sampling-field">
    <div className="caliper-circle-sampling-label">
      <span>{parameter.label}{parameter.unit && ` (${parameter.unit})`}
        {parameter.description && <Tooltip title={parameter.description} trigger={['hover', 'focus']}>
          <button type="button" className="property-help" aria-label={`${parameter.label}说明`}>?</button>
        </Tooltip>}
      </span>
      <Button size="small" type="text" disabled={disabled || value === parameter.defaultValue}
        aria-label={`恢复 ${parameter.label} 的默认值`} title={`恢复默认值 ${parameter.defaultValue}`}
        onClick={() => onChange({ [parameter.name]: parameter.defaultValue })}>↺</Button>
      <InputNumber size="small" aria-label={parameter.label} aria-describedby={hintId} aria-invalid={Boolean(issue)}
        status={issue ? 'error' : undefined} disabled={disabled} min={minimum} max={maximum}
        step={parameter.step} value={numeric} onChange={change} />
    </div>
    <Slider min={minimum} max={maximum} step={parameter.step} disabled={disabled}
      ariaLabelForHandle={`${parameter.label}滑动调节`} ariaValueTextFormatterForHandle={next => `${next} ${parameter.unit ?? ''}`}
      value={projectionWidth ?? sliderValue} tooltip={{ formatter: next => `${next} ${parameter.unit ?? ''}` }} onChange={change} />
    <span id={hintId} className={'caliper-circle-sampling-hint' + (issue ? ' has-error' : '')}>
      {issue ?? (projectionWidth === undefined ? '' : `实际投影 ${projectionWidth} px；偶数按下一个奇数采样。`)}
    </span>
  </div>;
}
