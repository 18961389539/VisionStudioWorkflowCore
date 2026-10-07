import { useMemo, useState } from 'react';
import { Empty, Input, InputNumber, Select, Switch, Tag, Typography } from 'antd';
import type { Node } from '@xyflow/react';
import type { NodeCatalogItem, ParameterDescriptor } from '../types';
import { validateParameterValues } from '../validation';

type Props = {
  node?: Node;
  catalogItem?: NodeCatalogItem;
  onChange: (key: string, value: unknown) => void;
  /** 运行/调试在飞时冻结参数编辑：结果对应提交时的流程，边跑边改会让人误以为新参数已被验证 */
  disabled?: boolean;
};

const META_KEY = 'visionstudio.property-meta-expanded';

/** 返回 undefined 表示用户尚未表态，由“有参数则折叠”的默认策略决定 */
function readMetaExpanded(): boolean | undefined {
  try {
    const raw = window.localStorage.getItem(META_KEY);
    return raw === '1' ? true : raw === '0' ? false : undefined;
  } catch {
    return undefined;
  }
}

function persistMetaExpanded(expanded: boolean) {
  try { window.localStorage.setItem(META_KEY, expanded ? '1' : '0'); } catch { /* 忽略存储失败 */ }
}

function sameValue(a: unknown, b: unknown) {
  if (typeof a === 'number' && typeof b === 'number') return a === b;
  return String(a) === String(b);
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

export default function PropertyPanel({ node, catalogItem, onChange, disabled = false }: Props) {
  const [metaOverride, setMetaOverride] = useState<boolean | undefined>(readMetaExpanded);
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
  const hasParameters = catalogItem.parameters.length > 0;
  // 默认折叠技术信息；但没有任何参数的节点只剩元数据可看，这种情况保持展开
  const metaExpanded = metaOverride ?? !hasParameters;
  const modified = catalogItem.parameters.filter((parameter) => parameters[parameter.name] !== undefined
    && parameter.defaultValue !== undefined
    && !sameValue(parameters[parameter.name], parameter.defaultValue));

  // 参数值预检（与“校验”问题列表共用同一套判定）：数字缺失/非法/越界、选择项越界
  const paramIssues = new Map(validateParameterValues(catalogItem.parameters, parameters).map((issue) => [issue.name, issue.message]));

  const toggleMeta = () => {
    const next = !metaExpanded;
    setMetaOverride(next);
    persistMetaExpanded(next);
  };

  const resetAll = () => modified.forEach((parameter) => onChange(parameter.name, parameter.defaultValue));

  const renderControl = (parameter: ParameterDescriptor) => {
    const issue = paramIssues.get(parameter.name);
    const status = issue ? ('error' as const) : undefined;
    if (parameter.type === 'number') {
      return (
        <InputNumber
          size="small"
          status={status}
          disabled={disabled}
          value={Number(parameters[parameter.name] ?? parameter.defaultValue ?? 0)}
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
          disabled={disabled}
          checked={Boolean(parameters[parameter.name])}
          onChange={(value) => onChange(parameter.name, value)}
        />
      );
    }
    return (
      <Select
        size="small"
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
          {parameter.description && <span className="property-description">{parameter.description}</span>}
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
      <div className="panel-title">属性</div>
      <div className="property-heading">{catalogItem.displayName}</div>
      <div className="property-subtitle">
        {catalogItem.type}
        {modified.length > 0 && (
          <> · <button type="button" className="text-button" disabled={disabled} onClick={resetAll}>恢复全部默认值（{modified.length}）</button></>
        )}
      </div>

      <div className="property-tech">
        <button type="button" className={`property-tech-toggle${metaExpanded ? ' open' : ''}`} aria-expanded={metaExpanded} onClick={toggleMeta}>
          <span className="panel-caret">{metaExpanded ? '▾' : '▸'}</span>
          <span>技术信息</span>
        </button>
        {metaExpanded && (
          <>
            <div className="property-meta">
              <Tag>{catalogItem.pluginId ?? 'builtin'}</Tag>
              {catalogItem.capabilities?.supportsRoi && <Tag color="blue">ROI</Tag>}
              {catalogItem.capabilities?.emitsOverlay && <Tag color="green">覆盖层</Tag>}
              {catalogItem.capabilities?.deterministic !== false && <Tag color="default">确定性执行</Tag>}
              {catalogItem.description && <Typography.Text type="secondary">{catalogItem.description}</Typography.Text>}
            </div>
            <div className="port-schema">
              <div><strong>IN</strong> {catalogItem.inputs.map((p) => `${p.name}:${p.dataType}`).join(' · ') || '—'}</div>
              <div><strong>OUT</strong> {catalogItem.outputs.map((p) => `${p.name}:${p.dataType}`).join(' · ') || '—'}</div>
            </div>
          </>
        )}
      </div>

      <div className="property-list">
        {parameters.roi && (
          <div className="roi-property">
            <div className="roi-property-title">感兴趣区域 · 在图像查看器中编辑</div>
            <code>{JSON.stringify(parameters.roi)}</code>
          </div>
        )}
        {catalogItem.parameters.length === 0 && !parameters.roi && <div className="property-empty">无可配置参数</div>}
        {sections.map((section, index) => (
          <div key={section.title ?? `section-${index}`}>
            {section.title && <div className="property-group">{section.title}</div>}
            {section.parameters.map(renderRow)}
          </div>
        ))}
      </div>
    </div>
  );
}