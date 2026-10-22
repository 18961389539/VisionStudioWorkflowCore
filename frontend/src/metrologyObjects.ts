export type MetrologyObject = { type: 'line' | 'circle' | 'ellipse' | 'rectangle2'; name: string; [key: string]: unknown };
export const metrologyFields: Record<MetrologyObject['type'], [string, string][]> = {
  line: [['columnBegin', '起点 X'], ['rowBegin', '起点 Y'], ['columnEnd', '终点 X'], ['rowEnd', '终点 Y']],
  circle: [['column', '圆心 X'], ['row', '圆心 Y'], ['radius', '半径']],
  ellipse: [['column', '中心 X'], ['row', '中心 Y'], ['radius1', '长半轴'], ['radius2', '短半轴'], ['phiDeg', '角度（度）']],
  rectangle2: [['column', '中心 X'], ['row', '中心 Y'], ['length1', '半宽'], ['length2', '半高'], ['phiDeg', '角度（度）']]
};

export function parseMetrologyObjects(value: unknown): { objects: MetrologyObject[]; error?: string } {
  try {
    const parsed: unknown = typeof value === 'string' ? JSON.parse(value || '[]') : value ?? [];
    if (!Array.isArray(parsed) || parsed.some(item => !item || typeof item !== 'object' || Array.isArray(item)))
      return { objects: [], error: '测量对象必须是 JSON 对象数组。' };
    return { objects: parsed.map((item, index) => {
      const type = typeof item.type === 'string' ? item.type.trim().toLowerCase() : item.type;
      return { ...item, type: type === 'rect' || type === 'rectangle' ? 'rectangle2' : type, name: item.name ?? `obj${index}` };
    }) as MetrologyObject[] };
  } catch { return { objects: [], error: '测量对象 JSON 无效；请修复原文后继续编辑。' }; }
}

export function metrologyObjectsIssue(value: unknown): string | undefined {
  const { objects, error } = parseMetrologyObjects(value);
  if (error) return error;
  if (!objects.length) return '请添加至少一个测量对象。';
  if (objects.length > 512) return '测量对象最多 512 个。';
  const names = new Set<string>();
  for (const object of objects) {
    if (typeof object.name !== 'string' || !object.name.trim() || object.name.length > 128 || object.name !== object.name.trim()) return '对象名称须为 1–128 个字符，且前后无空格。';
    if (names.has(object.name)) return `对象名称重复：${object.name}`;
    names.add(object.name);
    for (const [key, min, max] of [['sigma', 0.4, 100], ['threshold', 0, 255], ['measureLength1', 0.1, 10000], ['measureLength2', 0, 10000], ['minScore', 0, 1]] as const) {
      const value = object[key];
      if (value !== undefined && (typeof value !== 'number' || !Number.isFinite(value) || value < min || value > max))
        return `${object.name} 的 ${key} 须为 ${min}–${max} 的数字。`;
    }
    if (!Object.hasOwn(metrologyFields, object.type)) return `不支持的测量对象类型：${object.type}`;
    for (const [key] of metrologyFields[object.type]) {
      const number = object[key] ?? (key === 'phiDeg' ? 0 : undefined);
      if (typeof number !== 'number' || !Number.isFinite(number) || Math.abs(number) > 10_000_000) return `${object.name} 的 ${key} 须为有效数字。`;
      if (['radius', 'radius1', 'radius2', 'length1', 'length2'].includes(key) && number <= 0) return `${object.name} 的尺寸须大于 0。`;
    }
    if (object.type === 'line' && Math.hypot(Number(object.columnEnd) - Number(object.columnBegin), Number(object.rowEnd) - Number(object.rowBegin)) < 1e-6)
      return `${object.name} 的直线长度须大于 0。`;
  }
  return undefined;
}

export function metrologyObjectFromDrag(type: MetrologyObject['type'], start: { x: number; y: number }, end: { x: number; y: number }, name: string): MetrologyObject {
  if (type === 'line') return { type, name, columnBegin: start.x, rowBegin: start.y, columnEnd: end.x, rowEnd: end.y };
  if (type === 'circle') return { type, name, column: start.x, row: start.y, radius: Math.max(1, Math.hypot(end.x - start.x, end.y - start.y)) };
  return { type, name, column: (start.x + end.x) / 2, row: (start.y + end.y) / 2, phiDeg: 0,
    [type === 'ellipse' ? 'radius1' : 'length1']: Math.max(1, Math.abs(end.x - start.x) / 2),
    [type === 'ellipse' ? 'radius2' : 'length2']: Math.max(1, Math.abs(end.y - start.y) / 2) };
}
