import { memo } from 'react';
import { Button, Segmented } from 'antd';
import type { FitViewOptions } from '@xyflow/react';
import { useEditorLayout } from '../editorContexts';
import { useI18n } from '../i18n/provider';
import { CANVAS_FIT_PADDING } from '../appEditorShared';
import type { LayoutPreset } from '../layout';

type Props = { hasNodes: boolean; anyBusy: boolean; autoLayout?: () => void; fitView: (options?: FitViewOptions) => Promise<boolean> };

export default memo(function CanvasLayoutToolbar({ hasNodes, anyBusy, autoLayout, fitView }: Props) {
  const { lang } = useI18n();
  const text = (zh: string, en: string) => lang === 'en' ? en : zh;
  const { layout, applyLayoutPreset, focusMode, toggleFocusMode } = useEditorLayout();
  return <div className="canvas-layout-toolbar">
    {autoLayout && <Button size="small" disabled={!hasNodes || anyBusy} onClick={autoLayout}
      title={text('按节点连线关系自动整理布局（可撤销）', 'Auto-arrange nodes by topology (undoable)')}>{text('自动排布', 'Auto layout')}</Button>}
    <Button size="small" onClick={() => void fitView({ padding: CANVAS_FIT_PADDING, duration: 180 })}
      title={text('缩放并居中显示整个流程（Ctrl+0）', 'Fit workflow to view (Ctrl+0)')}>{text('适应画布', 'Fit view')}</Button>
    <Segmented size="small" value={layout.preset}
      onChange={(value) => { if (value !== 'custom') applyLayoutPreset(value as LayoutPreset); }}
      options={[
        { label: text('标准布局', 'Standard'), value: 'edit' },
        { label: text('宽画布', 'Wide'), value: 'image' },
        ...(layout.preset === 'custom' ? [{ label: text('自定义', 'Custom'), value: 'custom' }] : [])
      ]} />
    <Button size="small" type={focusMode ? 'primary' : 'default'} onClick={toggleFocusMode}
      title={focusMode ? text('退出专注：还原进入前的面板布局', 'Exit focus mode') : text('专注画布：收起工具箱与结果区', 'Focus canvas')}>
      {focusMode ? text('退出专注', 'Exit focus') : text('专注', 'Focus')}
    </Button>
  </div>;
});
