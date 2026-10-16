import { useMemo } from 'react';
import { Button, Empty, Input, InputNumber, Select, Switch, Tag, Tooltip } from 'antd';
import type { Node } from '@xyflow/react';
import type { NodeCatalogItem, ParameterDescriptor, VisionRoi } from '../types';
import { isParameterVisible, validateParameterValues } from '../validation';
import { roiConfigurationIssue } from '../nodeConfigurationState';

type Props = {
  node?: Node;
  catalogItem?: NodeCatalogItem;
  onChange: (key: string, value: unknown) => void;
  /** 运行/调试在飞时冻结参数编辑：结果对应提交时的流程，边跑边改会让人误以为新参数已被验证 */
  disabled?: boolean;
  /** 在图像查看器中居中显示当前节点的 ROI */
  onLocateRoi?: (key?: string) => void;
  roiFields?: { key: string; label: string; emptyText?: string; showEmpty?: boolean }[];
};

function sameValue(a: unknown, b: unknown) {
  if (typeof a === 'number' && typeof b === 'number') return a === b;
  return String(a) === String(b);
}

function isVisionRoi(value: unknown): value is VisionRoi {
  if (!value || typeof value !== 'object') return false;
  const type = (value as { type?: unknown }).type;
  return type === 'Rectangle' || type === 'Circle' || type === 'Polygon' || type === 'Brush';
}

/** ROI 结构化编辑：形状 + 位置/尺寸数值；数值与图像查看器中的拖动互为镜像 */
function RoiEditor({ roi, disabled, onChange }: { roi: VisionRoi; disabled?: boolean; onChange: (roi: VisionRoi) => void }) {
  const field = (label: string, value: number, apply: (next: number) => void) => (
    <label className="roi-field" key={label}>
      <span>{label}</span>
      <InputNumber size="small" aria-label={label} disabled={disabled} value={Number(value.toFixed(2))} onChange={(next) => apply(Number(next ?? 0))} />
    </label>
  );
  if (roi.type === 'Rectangle') {
    return (
      <div className="roi-grid">
        <Tag color="red">矩形</Tag>
        {field('X', roi.x, (v) => onChange({ ...roi, x: v }))}
        {field('Y', roi.y, (v) => onChange({ ...roi, y: v }))}
        {field('宽', roi.width, (v) => onChange({ ...roi, width: Math.max(0, v) }))}
        {field('高', roi.height, (v) => onChange({ ...roi, height: Math.max(0, v) }))}
      </div>
    );
  }
  if (roi.type === 'Circle') {
    return (
      <div className="roi-grid">
        <Tag color="red">圆形</Tag>
        {field('X', roi.x, (v) => onChange({ ...roi, x: v }))}
        {field('Y', roi.y, (v) => onChange({ ...roi, y: v }))}
        {field('半径', roi.radius, (v) => onChange({ ...roi, radius: Math.max(0, v) }))}
      </div>
    );
  }
  if (roi.type === 'Brush') {
    const addCount = roi.strokes.filter((stroke) => stroke.mode === 'Add').length;
    const eraseCount = roi.strokes.filter((stroke) => stroke.mode === 'Erase').length;
    const lastBrush = [...roi.strokes].reverse().find((stroke) => stroke.mode === 'Add');
    return <div className="roi-grid">
      <Tag color="red">笔刷</Tag>
      <span className="roi-polygon-note">{addCount} 笔 · {eraseCount} 次擦除{lastBrush ? ` · 笔宽 ${lastBrush.width}px` : ''}</span>
    </div>;
  }
  return (
    <div>
      <div className="roi-grid">
        <Tag color="red">多边形</Tag>
        <span className="roi-polygon-note">{roi.points.length} 个顶点 · 拖动图像上的控制点或输入坐标</span>
      </div>
      <div className="roi-polygon-vertices">
        {roi.points.map((point, index) => (
          <div className="roi-vertex-row" key={index}>
            <span>点 {index + 1}</span>
            <InputNumber size="small" aria-label={`点 ${index + 1} X`} value={Number(point.x.toFixed(2))}
              disabled={disabled} onChange={(value) => onChange({ ...roi, points: roi.points.map((item, i) => i === index ? { ...item, x: Number(value ?? 0) } : item) })} />
            <InputNumber size="small" aria-label={`点 ${index + 1} Y`} value={Number(point.y.toFixed(2))}
              disabled={disabled} onChange={(value) => onChange({ ...roi, points: roi.points.map((item, i) => i === index ? { ...item, y: Number(value ?? 0) } : item) })} />
            <Button type="text" size="small" danger aria-label={`删除点 ${index + 1}`} title="删除此顶点" disabled={disabled || roi.points.length <= 3}
              onClick={() => onChange({ ...roi, points: roi.points.filter((_, i) => i !== index) })}>×</Button>
          </div>
        ))}
      </div>
    </div>
  );
}

type Section = { title?: string; parameters: ParameterDescriptor[] };

/** 只有出现 ≥2 个分组时才分区展示；组名沿用目录里的英文，未分组参数归入 General */
function buildSections(catalogItem?: NodeCatalogItem): Section[] {
  const parameters = catalogItem?.parameters ?? [];
  const groups = Array.from(new Set(parameters.map((p) => p.group).filter((group): group is string => Boolean(group))));
  if (groups.length < 2) return [{ parameters }];
  const sections: Section[] = groups.map((title) => ({ title, parameters: parameters.filter((p) => p.group === title) }));
  const ungrouped = parameters.filter((p) => !p.group);
  if (ungrouped.length > 0) sections.push({ title: 'General', parameters: ungrouped });
  return sections;
}

export default function PropertyPanel({ node, catalogItem, onChange, disabled = false, onLocateRoi, roiFields }: Props) {
  const sections = useMemo(() => buildSections(catalogItem), [catalogItem]);

  if (!node || !catalogItem) {
    return (
      <div className="property-panel" title={disabled ? '运行/调试进行中，参数编辑已冻结（结果对应提交时的流程）' : undefined}>
      <div className="panel-title">属性{disabled ? ' · 运行中已冻结' : ''}</div>
        <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="选择一个节点" />
      </div>
    );
  }

  const parameters = (node.data.parameters ?? {}) as Record<string, unknown>;
  const configuredRoiFields = roiFields ?? [{ key: 'roi', label: '感兴趣区域' }];
  const modified = catalogItem.parameters.filter((parameter) => parameters[parameter.name] !== undefined
    && parameter.defaultValue !== undefined
    && !sameValue(parameters[parameter.name], parameter.defaultValue));

  // 参数值预检（与“校验”问题列表共用同一套判定）：数字缺失/非法/越界、选择项越界
  const paramIssues = new Map(validateParameterValues(catalogItem.parameters, parameters).map((issue) => [issue.name, issue.message]));

  const resetAll = () => modified.forEach((parameter) => onChange(parameter.name, parameter.defaultValue));

  const renderControl = (parameter: ParameterDescriptor) => {
    const issue = paramIssues.get(parameter.name);
    const status = issue ? ('error' as const) : undefined;
    if (parameter.type === 'number') {
      return (
        <InputNumber
          size="small"
          aria-label={parameter.label}
          status={status}
          disabled={disabled}
          value={parameters[parameter.name] === null ? null : Number(parameters[parameter.name] ?? parameter.defaultValue ?? 0)}
          min={parameter.min}
          max={parameter.max}
          step={parameter.step}
          onChange={(value) => onChange(parameter.name, value)}
        />
      );
    }
    if (parameter.type === 'text') {
      return (
        <Input
          size="small"
          aria-label={parameter.label}
          status={status}
          disabled={disabled}
          value={String(parameters[parameter.name] ?? '')}
          onChange={(event) => onChange(parameter.name, event.target.value)}
        />
      );
    }
    if (parameter.type === 'textarea') {
      return (
        <Input.TextArea
          size="small"
          aria-label={parameter.label}
          status={status}
          disabled={disabled}
          autoSize={{ minRows: 3, maxRows: 12 }}
          value={String(parameters[parameter.name] ?? '')}
          onChange={(event) => onChange(parameter.name, event.target.value)}
        />
      );
    }
    if (parameter.type === 'boolean') {
      return (
        <Switch
          size="small"
          aria-label={parameter.label}
          disabled={disabled}
          checked={Boolean(parameters[parameter.name])}
          onChange={(value) => onChange(parameter.name, value)}
        />
      );
    }
    return (
      <Select
        size="small"
        aria-label={parameter.label}
        status={status}
        disabled={disabled}
        value={String(parameters[parameter.name] ?? parameter.defaultValue ?? '')}
        options={parameter.options}
        onChange={(value) => onChange(parameter.name, value)}
      />
    );
  };

  const renderRow = (parameter: ParameterDescriptor) => {
    const wide = parameter.type === 'textarea' || parameter.type === 'text';
    const isModified = parameters[parameter.name] !== undefined
      && parameter.defaultValue !== undefined
      && !sameValue(parameters[parameter.name], parameter.defaultValue);
    const issue = paramIssues.get(parameter.name);
    return (
      <div className={`property-row${wide ? ' wide' : ''}`} key={parameter.name}>
        <label>
          <span className="property-label-text">
            <span className="property-label-name">
              {parameter.label}{parameter.unit ? ` (${parameter.unit})` : ''}
            </span>
            {parameter.description && <Tooltip title={parameter.description} trigger={['hover', 'focus']}>
              <button type="button" className="property-help" aria-label={`${parameter.label}说明`}>?</button>
            </Tooltip>}
            {isModified && (
              <span className="property-label-actions">
                <em className="property-modified">已改</em>
                <button
                  type="button"
                  className="property-reset"
                  title={`恢复默认值 ${String(parameter.defaultValue)}`}
                  aria-label={`恢复 ${parameter.label} 的默认值`}
                  disabled={disabled}
                  onClick={() => onChange(parameter.name, parameter.defaultValue)}
                >↺</button>
              </span>
            )}
          </span>
        </label>
        <div className="property-control">
          {renderControl(parameter)}
          {issue && <div style={{ color: '#ff4d4f', fontSize: 11, lineHeight: 1.45 }}>{issue}</div>}
        </div>
      </div>
    );
  };

  return (
    <div className="property-panel">
      {disabled && (
        <div className="property-frozen-note">运行/调试进行中：参数编辑已冻结，结果对应提交时的流程；结束后可继续编辑。</div>
      )}
      {catalogItem.parameters.length > 0 && <div className="property-subtitle property-actions">
        {/* 单条操作不再藏在“更多参数操作 ···”菜单里：直接可见、一次点击（与“还原为打开时”互补：
            本按钮回到节点出厂默认值，弹窗标题栏的“还原为打开时”回到打开弹窗那一刻的值） */}
        <Button size="small" type="text" disabled={disabled || modified.length === 0}
          title="把已改参数恢复为节点出厂默认值；如需恢复为打开弹窗时的值，用标题栏的“还原为打开时”"
          onClick={resetAll}>恢复出厂默认（{modified.length}）</Button>
      </div>}

      <div className="property-list">
        {configuredRoiFields.map((field) => {
          const value = parameters[field.key];
          if (value === undefined && !field.showEmpty) return null;
          const roi = isVisionRoi(value) ? value : undefined;
          const roiIssue = value === undefined ? undefined : roiConfigurationIssue(value, field.label, 1024);
          return <div className="roi-property" key={field.key}>
            <div className="roi-property-title">
              <span>{field.label}</span>
              {onLocateRoi && <button type="button" className="text-button" disabled={disabled}
                onClick={() => onLocateRoi(field.key)} title="在图像查看器中编辑此选区">在图像中编辑</button>}
            </div>
            {roiIssue ? <div role="alert" className="roi-invalid-state" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span className="roi-polygon-note" style={{ color: '#ff4d4f', flex: 1 }}>{roiIssue}</span>
              <Button size="small" danger disabled={disabled} onClick={() => onChange(field.key, undefined)}>清除无效区域</Button>
            </div> : roi ? <RoiEditor roi={roi} disabled={disabled} onChange={(next) => onChange(field.key, next)} />
              : value !== undefined ? <code>{JSON.stringify(value)}</code>
                : <span className="roi-polygon-note">{field.emptyText ?? '未设置'}</span>}
          </div>;
        })}
        {catalogItem.parameters.length === 0 && !parameters.roi && !roiFields && <div className="property-empty">无可配置参数</div>}
        {sections.map((section, index) => {
          const visibleParameters = section.parameters.filter((parameter) =>
            isParameterVisible(parameter, catalogItem.parameters, parameters));
          if (visibleParameters.length === 0) return null;
          return <div key={section.title ?? `section-${index}`}>
            {section.title && <div className="property-group">{section.title}</div>}
            {visibleParameters.map(renderRow)}
          </div>;
        })}
      </div>
    </div>
  );
}
