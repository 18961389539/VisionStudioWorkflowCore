import type { VisionOverlay } from '../types';
import { useRef, useState, type PointerEvent } from 'react';
import { Alert, Button, Collapse, Input, InputNumber, Select, Space } from 'antd';
import { metrologyFields, metrologyObjectFromDrag, metrologyObjectsIssue, parseMetrologyObjects, type MetrologyObject } from '../metrologyObjects';
import JsonTextarea from './JsonTextarea';
import { jsonExampleFor } from '../jsonExamples';

const kinds = { line: '直线', circle: '圆', ellipse: '椭圆', rectangle2: '旋转矩形' };
type Point = { x: number; y: number };

export default function MetrologyObjectEditor({ value, onChange, imageSrc, width = 640, height = 480, disabled, aligned, overlays = [], onLoadImage }: {
  value: unknown; onChange: (value: string) => void; imageSrc?: string; width?: number; height?: number;
  disabled?: boolean; aligned?: boolean; overlays?: VisionOverlay[]; onLoadImage: () => void;
}) {
  const parsed = parseMetrologyObjects(value);
  const objects = parsed.objects;
  const [selected, setSelected] = useState(0);
  const [tool, setTool] = useState<MetrologyObject['type'] | 'select'>('select');
  const [drawing, setDrawing] = useState<{ start: Point; end: Point }>();
  const [undo, setUndo] = useState<string>();
  const drag = useRef<{ start: Point; object?: MetrologyObject; index: number; handle?: 'start' | 'end' | 'size'; original: string } | undefined>(undefined);
  const selectedIndex = Math.max(0, Math.min(selected, objects.length - 1));
  const active = objects[selectedIndex];
  const locked = disabled || !!parsed.error;
  const imageWidth = Math.max(1, width), imageHeight = Math.max(1, height);
  const nextName = () => { let index = objects.length; while (objects.some(item => item.name === `obj${index}`)) index++; return `obj${index}`; };
  const commit = (next: MetrologyObject[], remember = true) => { if (remember) setUndo(String(value ?? '[]')); onChange(JSON.stringify(next)); };
  const update = (patch: Record<string, unknown>) => commit(objects.map((item, index) => index === selectedIndex ? { ...item, ...patch } : item));
  const point = (event: PointerEvent<SVGSVGElement>): Point => {
    const bounds = event.currentTarget.getBoundingClientRect();
    return { x: Math.max(0, Math.min(imageWidth, (event.clientX - bounds.left) / bounds.width * imageWidth)),
      y: Math.max(0, Math.min(imageHeight, (event.clientY - bounds.top) / bounds.height * imageHeight)) };
  };
  const startDrag = (event: PointerEvent<SVGElement>, index: number, handle?: 'start' | 'end' | 'size') => {
    if (tool !== 'select') return;
    event.stopPropagation(); setSelected(index);
    if (locked || aligned || !imageSrc) return;
    const svg = event.currentTarget.ownerSVGElement!;
    const bounds = svg.getBoundingClientRect();
    drag.current = { start: { x: (event.clientX - bounds.left) / bounds.width * imageWidth,
      y: (event.clientY - bounds.top) / bounds.height * imageHeight }, object: { ...objects[index] }, index, handle, original: String(value ?? '[]') };
    svg.setPointerCapture(event.pointerId);
  };
  const move = (event: PointerEvent<SVGSVGElement>) => {
    const state = drag.current;
    if (!state || locked || aligned) return;
    const end = point(event);
    if (!state.object) { setDrawing({ start: state.start, end }); return; }
    const object = { ...state.object };
    const dx = end.x - state.start.x, dy = end.y - state.start.y;
    if (object.type === 'line') {
      for (const [x, y, endpoint] of [['columnBegin', 'rowBegin', 'start'], ['columnEnd', 'rowEnd', 'end']])
        if (!state.handle || state.handle === endpoint) { object[x] = Number(state.object[x]) + dx; object[y] = Number(state.object[y]) + dy; }
    } else if (state.handle === 'size') {
      const angle = Number(object.phiDeg ?? 0) * Math.PI / 180;
      const x = end.x - Number(object.column), y = end.y - Number(object.row);
      if (object.type === 'circle') object.radius = Math.max(1, Math.hypot(x, y));
      else { object[object.type === 'ellipse' ? 'radius1' : 'length1'] = Math.max(1, Math.abs(x * Math.cos(angle) + y * Math.sin(angle)));
        object[object.type === 'ellipse' ? 'radius2' : 'length2'] = Math.max(1, Math.abs(-x * Math.sin(angle) + y * Math.cos(angle))); }
    } else { object.column = Number(object.column) + dx; object.row = Number(object.row) + dy; }
    commit(objects.map((item, index) => index === state.index ? object : item), false);
  };
  const finish = (event: PointerEvent<SVGSVGElement>, cancel = false) => {
    const state = drag.current; if (!state) return;
    drag.current = undefined; setDrawing(undefined);
    if (cancel) { if (state.object) onChange(state.original); return; }
    if (state.object) { setUndo(state.original); return; }
    const end = point(event);
    if (tool !== 'select' && Math.hypot(end.x - state.start.x, end.y - state.start.y) >= 2) {
      commit([...objects, metrologyObjectFromDrag(tool, state.start, end, nextName())]); setSelected(objects.length); setTool('select');
    }
  };
  const scale = Math.max(imageWidth / 500, 0.5);
  const renderObject = (object: MetrologyObject, index: number, preview = false) => {
    const x = Number(object.column ?? 0), y = Number(object.row ?? 0), angle = Number(object.phiDeg ?? 0);
    const radius = Number(object.radius ?? 1), rx = Number(object.radius1 ?? object.length1 ?? 1), ry = Number(object.radius2 ?? object.length2 ?? 1);
    const selectedObject = index === selectedIndex && !preview;
    const color = selectedObject ? '#40a9ff' : '#52c41a';
    const handle = (px: number, py: number, which: 'start' | 'end' | 'size') => <circle cx={px} cy={py} r={5 * scale} fill="#fff" stroke={color}
      onPointerDown={event => startDrag(event, index, which)} />;
    return <g key={preview ? 'drawing' : index} stroke={color} strokeWidth={2 * scale} fill="transparent"
      onPointerDown={preview ? undefined : event => startDrag(event, index)} style={{ cursor: locked || aligned ? 'default' : 'move' }}>
      {object.type === 'line' ? <>
        <line x1={Number(object.columnBegin)} y1={Number(object.rowBegin)} x2={Number(object.columnEnd)} y2={Number(object.rowEnd)} strokeWidth={8 * scale} strokeOpacity={0.2} />
        <line x1={Number(object.columnBegin)} y1={Number(object.rowBegin)} x2={Number(object.columnEnd)} y2={Number(object.rowEnd)} />
        {selectedObject && <>{handle(Number(object.columnBegin), Number(object.rowBegin), 'start')}{handle(Number(object.columnEnd), Number(object.rowEnd), 'end')}</>}
      </> : <g transform={`rotate(${angle} ${x} ${y})`}>
        {object.type === 'circle' ? <circle cx={x} cy={y} r={radius} />
          : object.type === 'ellipse' ? <ellipse cx={x} cy={y} rx={rx} ry={ry} /> : <rect x={x - rx} y={y - ry} width={rx * 2} height={ry * 2} />}
        {selectedObject && handle(x + (object.type === 'circle' ? radius : rx), y + (object.type === 'circle' ? 0 : ry), 'size')}
      </g>}
      <text x={object.type === 'line' ? Number(object.columnBegin) : x} y={(object.type === 'line' ? Number(object.rowBegin) : y) - 8 * scale}
        fill={color} stroke="none" fontSize={13 * scale}>{object.name}</text>
    </g>;
  };
  return <section aria-label="计量对象编辑器" style={{ width: '100%', padding: 12, overflow: 'auto' }}>
    <Space wrap><Button onClick={onLoadImage} disabled={disabled}>获取输入图像</Button>
      <Select aria-label="绘制测量对象" value={tool} disabled={locked || aligned || !imageSrc || objects.length >= 512}
        options={[{ label: '选择 / 移动对象', value: 'select' }, ...Object.entries(kinds).map(([value, label]) => ({ value, label: `绘制${label}` }))]}
        onChange={value => setTool(value)} style={{ width: 190 }} />
      <Button disabled={locked || !undo} onClick={() => { onChange(undo!); setUndo(undefined); }}>撤销对象修改</Button></Space>
    {aligned && <Alert type="info" title="对象使用基准坐标；启用对齐时图上绘制锁定，可通过下方坐标编辑。当前图像只叠加实测结果。绘制基准对象时请先关闭对齐。" />}
    <svg aria-label="计量对象画布" viewBox={`0 0 ${imageWidth} ${imageHeight}`} preserveAspectRatio="none"
      style={{ display: 'block', width: '100%', aspectRatio: `${imageWidth}/${imageHeight}`, background: '#111827', touchAction: 'none', marginTop: 8 }}
      onPointerDown={event => { if (locked || aligned || !imageSrc || tool === 'select' || objects.length >= 512) return;
        event.currentTarget.setPointerCapture(event.pointerId); const start = point(event); drag.current = { start, index: objects.length, original: String(value ?? '[]') }; setDrawing({ start, end: start }); }}
      onPointerMove={move} onPointerUp={event => finish(event)} onPointerCancel={event => finish(event, true)}>
      {imageSrc && <image href={imageSrc} width={imageWidth} height={imageHeight} preserveAspectRatio="none" />}
      {!aligned && objects.map((object, index) => Object.hasOwn(kinds, object.type) ? renderObject(object, index) : null)}
      {overlays.map(overlay => <g key={overlay.id} stroke="#faad14" strokeWidth={2 * scale} fill="none" pointerEvents="none">
        {overlay.type === 'Line' && <line x1={overlay.x ?? 0} y1={overlay.y ?? 0} x2={overlay.x2 ?? 0} y2={overlay.y2 ?? 0} />}
        {overlay.type === 'Circle' && <circle cx={overlay.x ?? 0} cy={overlay.y ?? 0} r={overlay.radius ?? 0} />}
        {overlay.type === 'Rectangle' && <rect x={(overlay.x ?? 0) - (overlay.width ?? 0) / 2} y={(overlay.y ?? 0) - (overlay.height ?? 0) / 2}
          width={overlay.width ?? 0} height={overlay.height ?? 0} transform={`rotate(${overlay.angleDeg ?? 0} ${overlay.x ?? 0} ${overlay.y ?? 0})`} />}
        {['Polygon', 'Polyline', 'Contour'].includes(overlay.type) && <polyline points={(overlay.points ?? []).map(point => `${point.x},${point.y}`).join(' ')} />}
      </g>)}
      {drawing && tool !== 'select'  && renderObject(metrologyObjectFromDrag(tool, drawing.start, drawing.end, '新对象'), -1, true)}
    </svg>
    <Space wrap style={{ margin: '8px 0' }}>
      <Select aria-label="当前计量对象" value={active ? Math.min(selected, objects.length - 1) : undefined} placeholder="请选择或绘制对象" style={{ minWidth: 190 }}
        options={objects.map((item, index) => ({ value: index, label: `${index}: ${item.name} · ${kinds[item.type] ?? item.type}` }))} onChange={setSelected} />
      <Button disabled={locked || !active || objects.length >= 512} onClick={() => { commit([...objects, { ...active, name: nextName() }]); setSelected(objects.length); }}>复制对象</Button>
      <Button danger disabled={locked || !active} onClick={() => { commit(objects.filter((_, index) => index !== selectedIndex)); setSelected(Math.max(0, selected - 1)); }}>删除对象</Button>
      <Select aria-label="添加计量对象" value={undefined} placeholder="添加对象（填写坐标）" disabled={locked || objects.length >= 512} style={{ width: 200 }}
        options={Object.entries(kinds).map(([value, label]) => ({ value, label }))} onChange={kind => {
          commit([...objects, metrologyObjectFromDrag(kind, { x: imageWidth / 2, y: imageHeight / 2 }, { x: imageWidth / 2 + 40, y: imageHeight / 2 + 40 }, nextName())]); setSelected(objects.length);
        }} />
    </Space>
    {active && Object.hasOwn(metrologyFields, active.type) && <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
      <label>名称<Input aria-label="计量对象名称" value={active.name} disabled={locked} onChange={event => update({ name: event.target.value })} /></label>
      {metrologyFields[active.type].map(([key, label]) => <label key={key}>{label}<InputNumber aria-label={`计量对象${label}`} value={Number(active[key] ?? 0)}
        disabled={locked} onChange={number => update({ [key]: number })} /></label>)}
      {(['sigma', 'threshold', 'measureLength1', 'measureLength2', 'minScore'] as const).map(key => <label key={key}>{({ sigma: '对象平滑 σ', threshold: '对象边缘阈值', measureLength1: '对象搜索半长', measureLength2: '对象投影半宽', minScore: '对象最低评分' })[key]}
        <InputNumber aria-label={`计量对象${key}`} placeholder="使用全局值" value={active[key] as number | undefined} disabled={locked} onChange={number => {
          const next = { ...active }; if (number === null) delete next[key]; else next[key] = number; commit(objects.map((item, index) => index === selectedIndex ? next : item));
        }} /></label>)}
    </div>}
    {metrologyObjectsIssue(value) && <Alert style={{ marginTop: 8 }} type="warning" title={metrologyObjectsIssue(value)} />}
    <p>蓝色为选中对象，绿色为基准对象，黄色为本次实测结果。拖动可移动对象，白色控制点用于调整尺寸。</p>
    <p>“结果列表”接到“提取计量结果”，按名称或序号取出圆、线、中心和位姿。未找到的对象保留原序号。</p>
    <Collapse items={[{ key: 'json', label: '高级：对象 JSON（保留旧配方和每对象扩展参数）', children: <JsonTextarea label="计量对象 JSON"
      value={typeof value === 'string' ? value : JSON.stringify(value ?? [])} disabled={disabled}
      example={jsonExampleFor('measure.metrology', 'objects')} onChange={onChange} /> }]} />
  </section>;
}
