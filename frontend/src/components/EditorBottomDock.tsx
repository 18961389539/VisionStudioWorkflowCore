/**
 * 编辑器底部停靠区（App.tsx `Editor`）：结果区高度手柄 + 统一标签页
 * （运行结果 / 校验问题 / 运行观测，折叠态标签条保留为展开入口）。
 * 从 App.tsx 拆出（巨型组件拆分，2026-10-23）；纯展示组件，JSX 与原实现逐行一致。
 */
import { Button, Empty, Segmented, Tag } from 'antd';
import SplitHandle from './SplitHandle';
import RunPanel from './RunPanel';
import RunObservabilityPanel from './RunObservabilityPanel';
import { LAYOUT_LIMITS, type LayoutState } from '../layout';
import type { WorkflowIssue } from '../validation';
import type { BottomTab } from '../useRunController';
import type { RunResult } from '../types';

type EditorBottomDockProps = {
  layout: LayoutState;
  updateLayout: (patch: Partial<LayoutState>) => void;
  bottomMax: number;
  /** 当前布局预设下的默认底部高度（复位用） */
  presetBottom: number;
  shownBottomBase: number;
  bottomTab: BottomTab;
  onBottomTabChange: (tab: BottomTab) => void;
  /** 拖动手柄时的实时高度预览（写外壳 CSS 变量） */
  onPreviewHeight: (value: number) => void;
  validationIssues: WorkflowIssue[];
  locateIssue: (issue: WorkflowIssue) => void;
  result?: RunResult;
  resultStale: boolean;
  focusNode: (nodeId: string) => void;
  /** 打开追溯并定位到这次运行（失败排查的下一站，2026-10-25） */
  onOpenTrace?: (runId: string) => void;
  nodeLabels: Record<string, string>;
  nodeTypeLabels: Record<string, string>;
};

export default function EditorBottomDock({
  layout, updateLayout, bottomMax, presetBottom, shownBottomBase,
  bottomTab, onBottomTabChange, onPreviewHeight, validationIssues, locateIssue,
  result, resultStale, focusNode, onOpenTrace, nodeLabels, nodeTypeLabels
}: EditorBottomDockProps) {
  return (
    <>
      <SplitHandle
        axis="y"
        label="调整结果区高度"
        value={shownBottomBase}
        min={LAYOUT_LIMITS.bottom.min}
        max={bottomMax}
        invert
        collapsed={layout.bottomCollapsed}
        collapseGlyph="⌄"
        expandGlyph="⌃"
        maximize={{
          active: layout.bottomMaximized,
          title: layout.bottomMaximized ? '还原结果区高度' : '最大化结果区',
          onToggle: () => updateLayout({ bottomMaximized: !layout.bottomMaximized, bottomCollapsed: false })
        }}
        preview={(value) => onPreviewHeight(value)}
        commit={(value) => updateLayout({ bottom: value, bottomSizing: 'manual', bottomMaximized: false, bottomCollapsed: false, preset: 'custom' })}
        reset={() => updateLayout({ bottom: presetBottom, bottomSizing: 'auto', bottomMaximized: false, bottomCollapsed: false })}
        onToggleCollapse={() => updateLayout({ bottomCollapsed: !layout.bottomCollapsed })}
      />

      <footer className={`bottom-panel${layout.bottomCollapsed ? ' collapsed' : ''}`}>
        {/* 底部统一标签页：运行结果 / 校验问题（带数量）/ 运行观测；布局切换已移至画布右上角（P1）。
            折叠态（P1）：标签条保留为展开入口——点击任一标签即展开对应页签 */}
        <div className="bottom-tabs-bar">
          {/* 折叠态点击即展开：Segmented 点击“已选中项”不触发 onChange，必须由外层点击区域兜底 */}
          <span className="bottom-tabs-expand" title={layout.bottomCollapsed ? '展开结果区' : undefined}
            onClick={() => { if (layout.bottomCollapsed) updateLayout({ bottomCollapsed: false }); }}>
            <Segmented
              size="small"
              value={bottomTab}
              onChange={(value) => {
                onBottomTabChange(value as typeof bottomTab);
                if (layout.bottomCollapsed) updateLayout({ bottomCollapsed: false });
              }}
              options={[
                { label: '运行结果', value: 'result' },
                { label: `校验问题${validationIssues.length > 0 ? ` · ${validationIssues.length}` : ''}`, value: 'issues' },
                { label: '运行观测', value: 'observability' }
              ]}
            />
          </span>
          {result && !result.success && <Button size="small" danger onClick={() => {
            onBottomTabChange('result');
            updateLayout({ bottomCollapsed: false, bottomMaximized: false });
            const failed = result.nodeReports.find((report) => !report.success);
            if (failed) focusNode(failed.nodeId);
          }}>{resultStale ? '查看上次错误' : '查看运行错误'}{result.nodeReports.some((report) => !report.success) ? ` · ${result.nodeReports.filter((report) => !report.success).length}` : ''}</Button>}
          {result && onOpenTrace && <Button size="small" type="link" title="在追溯中打开这次运行：失败帧、节点时间线、诊断与调查"
            onClick={() => onOpenTrace(result.runId)}>在追溯中打开</Button>}
          {layout.bottomCollapsed && <span className="bottom-collapsed-hint">点击标签展开结果区</span>}
        </div>
        {!layout.bottomCollapsed && bottomTab === 'result' && (
          <RunPanel result={result} nodeLabels={nodeLabels} onSelectNode={focusNode} stale={resultStale} embedded />
        )}
        {!layout.bottomCollapsed && bottomTab === 'issues' && (
          validationIssues.length === 0 ? (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="点击顶部“校验”检查流程；发现的问题将显示在这里，点击可定位到画布" />
          ) : (
            <div className="bottom-issues">
              {validationIssues.map((issue) => {
                const locatable = Boolean(issue.nodeId || issue.edgeId);
                return (
                  <div
                    key={issue.id}
                    onClick={() => locateIssue(issue)}
                    title={locatable ? '点击定位到节点/连线' : '该问题无法自动定位，请根据描述排查'}
                    style={{ display: 'flex', gap: 6, alignItems: 'flex-start', padding: '4px 6px', borderRadius: 6, cursor: locatable ? 'pointer' : 'default', fontSize: 12, lineHeight: 1.55 }}
                  >
                    <Tag color={issue.source === 'server' ? 'red' : 'orange'} style={{ marginInlineEnd: 0, flexShrink: 0 }}>
                      {issue.source === 'server' ? '编译器' : '预检'}
                    </Tag>
                    <span>
                      {issue.message}
                      {locatable && <small style={{ color: 'var(--vs-text-4)' }}>{issue.edgeId ? `（连线 ${issue.edgeId}）` : `（节点 ${issue.nodeId}）`}</small>}
                    </span>
                  </div>
                );
              })}
            </div>
          )
        )}
        {!layout.bottomCollapsed && bottomTab === 'observability' && <RunObservabilityPanel runId={result?.runId} nodeLabels={nodeLabels} nodeTypeLabels={nodeTypeLabels} />}
      </footer>
    </>
  );
}
