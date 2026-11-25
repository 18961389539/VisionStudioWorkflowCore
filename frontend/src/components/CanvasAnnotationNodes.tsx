import { NodeResizer, type NodeProps } from '@xyflow/react';
import { CANVAS_GROUP_COLORS } from '../canvasAnnotations';
import { useEditorGraph } from '../editorContexts';
import { useI18n } from '../i18n/provider';

export function CanvasGroupNode({ id, data, selected }: NodeProps) {
  const { updateCanvasAnnotation } = useEditorGraph();
  const { lang } = useI18n();
  const savedTitle = typeof data.title === 'string' ? data.title : '';
  const title = !savedTitle || savedTitle === '流程分组' ? lang === 'en' ? 'Workflow group' : '流程分组' : savedTitle;
  const savedColor = typeof data.color === 'string' ? data.color : CANVAS_GROUP_COLORS[0].value;
  const selectedColor = CANVAS_GROUP_COLORS.find((option) => option.value === savedColor);
  const color = selectedColor ? `var(--vs-canvas-group-${selectedColor.token})` : savedColor;
  return (
    <div className="canvas-group-node" style={{ borderColor: color, backgroundColor: `color-mix(in srgb, ${color} 7.06%, transparent)` }}>
      <NodeResizer isVisible={selected} minWidth={180} minHeight={100} color={color} />
      <div className="canvas-group-heading" style={{ backgroundColor: `color-mix(in srgb, ${color} 12.55%, transparent)` }}>
        <span className="canvas-group-grip" aria-hidden="true">⠿</span>
        <input className="nodrag nowheel" aria-label={lang === 'en' ? 'Group name' : '分组名称'} value={title}
          onChange={(event) => updateCanvasAnnotation(id, { title: event.target.value })} />
        <div className="canvas-group-colors" aria-label={lang === 'en' ? 'Group color' : '分组颜色'}>
          {CANVAS_GROUP_COLORS.map((option) => <button key={option.value} type="button" className="nodrag"
            aria-label={lang === 'en' ? `Select group color ${option.en}` : `选择分组颜色 ${option.zh}`} aria-pressed={option.value === savedColor}
            style={{ backgroundColor: `var(--vs-canvas-group-${option.token})` }} onClick={() => updateCanvasAnnotation(id, { color: option.value })} />)}
        </div>
      </div>
      <div className="canvas-group-caption">{lang === 'en' ? 'Local canvas group · excluded from execution' : '本机画布分组 · 不参与运行'}</div>
    </div>
  );
}

export function CanvasNoteNode({ id, data }: NodeProps) {
  const { updateCanvasAnnotation } = useEditorGraph();
  const { lang } = useI18n();
  const content = typeof data.content === 'string' ? data.content : '';
  return (
    <div className="canvas-note-node">
      <div className="canvas-note-heading"><span aria-hidden="true">✎</span> {lang === 'en' ? 'Workflow note' : '流程便签'}</div>
      <textarea className="nodrag nowheel" aria-label={lang === 'en' ? 'Workflow note content' : '流程便签内容'} value={content}
        placeholder={lang === 'en' ? 'Add purpose, cautions, or follow-ups…' : '记录流程目的、注意事项或待办…'}
        onChange={(event) => updateCanvasAnnotation(id, { content: event.target.value })} />
      <small>{lang === 'en' ? 'Local canvas note; excluded from execution' : '本机画布说明，不参与运行'}</small>
    </div>
  );
}
