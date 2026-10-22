import type { NodeCatalogItem, PortDescriptor } from './types';

export function readSwitchCases(value: unknown): { labels: string[]; issue?: undefined } | { labels?: undefined; issue: string } {
  let labels: unknown = value;
  if (typeof value === 'string') {
    try { labels = JSON.parse(value); } catch { labels = undefined; }
  }
  if (!Array.isArray(labels) || !labels.every((label): label is string => typeof label === 'string'))
    return { issue: '分支列表必须是 JSON 字符串数组。' };
  if (labels.length < 1 || labels.length > 16) return { issue: '分支列表需要 1–16 个标签。' };
  if (labels.some((label) => !label.trim())) return { issue: '分支标签不能为空。' };
  if (new Set(labels).size !== labels.length) return { issue: '分支标签必须唯一。' };
  return { labels };
}

/** Switch 只暴露 casesJson 实际声明的分支端口；未配置的 caseN 不应出现在画布上。 */
export function switchPorts(outputs: PortDescriptor[], parameters: Record<string, unknown> | undefined): PortDescriptor[] {
  const caseCount = readSwitchCases(parameters?.casesJson).labels?.length ?? 0;
  return outputs.filter((port) => {
    const match = /^case(\d+)$/i.exec(port.name);
    return !match || Number(match[1]) <= caseCount;
  });
}

export function withSwitchPorts<T extends NodeCatalogItem>(item: T, parameters: Record<string, unknown> | undefined): T {
  if (item.type !== 'flow.switch') return item;
  const casesJson = parameters?.casesJson === undefined
    ? item.parameters.find(parameter => parameter.name === 'casesJson')?.defaultValue
    : parameters.casesJson;
  const outputs = switchPorts(item.outputs, { casesJson });
  return outputs.length === item.outputs.length ? item : { ...item, outputs };
}
