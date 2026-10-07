/** 摘要标量压缩：节点 footer 与运行结果面板共用同一套“标量内联”规则（数组/对象不入内联行）。 */

export const isScalar = (value: unknown): value is string | number | boolean | null =>
  value === null || typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean';

export function formatScalar(value: string | number | boolean | null) {
  if (value === null) return '—';
  if (typeof value === 'number') return Number.isInteger(value) ? String(value) : String(Number(value.toFixed(3)));
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  return value;
}

/** 取摘要中前 max 个标量键值对拼成一行；keyLabel 允许调用方本地化键名（如端口名中文映射）。 */
export function summarizeScalars(
  summary: Record<string, unknown> | undefined | null,
  max = 3,
  keyLabel?: (key: string) => string,
): string {
  return Object.entries(summary ?? {})
    .filter(([, value]) => isScalar(value))
    .slice(0, max)
    .map(([key, value]) => `${keyLabel ? keyLabel(key) : key}=${formatScalar(value as string | number | boolean | null)}`)
    .join(' · ');
}
