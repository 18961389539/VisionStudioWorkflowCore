/**
 * 编辑器顶栏（App.tsx `Editor` 的 header）：菜单行 + 工作流身份行。
 * 从 App.tsx 拆出（巨型组件拆分，2026-10-23）；纯展示组件，闭包状态经分组 props 注入，
 * JSX 与原实现逐行一致。
 */
import { useEffect, useState } from 'react';
import { Badge, Button, Dropdown, Tag, Tooltip, Typography, type MenuProps } from 'antd';
import { SecurityAccountBadge } from '../security';
import { APP_VERSION } from '../version';
import { dateLocale, getInitialLang } from '../i18n';
import { renameDecision } from '../documentSync';
import { type LayoutPreset, type LayoutState } from '../layout';

type EditorHeaderProps = {
  menus: {
    debug: MenuProps['items'];
    device: MenuProps['items'];
    production: MenuProps['items'];
    tools: MenuProps['items'];
    more: MenuProps;
  };
  /** 当前布局预设（工具菜单的选中态；custom 表示手动调整过） */
  layoutPreset: LayoutState['preset'];
  onApplyLayoutPreset: (preset: LayoutPreset) => void;
  templates: { show: () => void; openSaved: () => void };
  debug: { inSession: boolean; statusText: string };
  run: { running: boolean; debugBusy: boolean; onRun: () => void; onAbort: () => void; startedAt?: number };
  workflow: { name: string; id: string; onRename: (name: string) => void; dirty: boolean; lastSavedAt?: Date };
  validation: { status: string; summary: string; needsRevalidate: boolean; issueCount: number; busy: boolean; onValidate: () => void };
  catalogError?: string;
  save: { busy: boolean; onSave: () => void };
};

/**
 * 运行中显示已用时间（2026-10-25）。长任务此前只有一个"中止"按钮：
 * 用户无法判断"后端在算"还是"连接掉了"，只能中止重来。
 */
function RunClock({ startedAt }: { startedAt?: number }) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);
  const seconds = startedAt ? Math.max(0, Math.floor((now - startedAt) / 1000)) : 0;
  const text = seconds >= 3600
    ? `${Math.floor(seconds / 3600)}:${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
    : `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
  return <span className="run-elapsed" title="本次运行已用时间">已用 {text}</span>;
}

export default function EditorHeader({ menus, layoutPreset, onApplyLayoutPreset, templates, debug, run, workflow, validation, catalogError, save }: EditorHeaderProps) {
  return (
    <header className="topbar">
      <div className="topbar-menu-row">
        <div className="brand">VisionStudio <span>V{APP_VERSION}</span></div>

        <div className="topbar-center">
          <Button className="topbar-file-action" type="text" onClick={templates.show}>从模板新建</Button>
            <Button className="topbar-file-action" type="text" onClick={() => void templates.openSaved()}>打开流程</Button>
          <span className="topbar-divider" />
          <Dropdown menu={{ items: menus.debug }} trigger={['click']}>
            <Button className="topbar-domain-action" type={debug.inSession ? 'primary' : 'default'} title={debug.inSession ? '打开调试菜单继续、运行节点或结束会话' : '节点运行、断点及调试会话'}>{debug.inSession ? `调试 · ${debug.statusText}` : '运行与调试'} <span className="menu-caret">▾</span></Button>
          </Dropdown>
          <Dropdown menu={{ items: menus.device }} trigger={['click']}>
            <Button type="text" className="topbar-domain-action">设备 <span className="menu-caret">▾</span></Button>
          </Dropdown>
          <Dropdown menu={{ items: menus.production }} trigger={['click']}>
            <Button type="text" className="topbar-domain-action">生产 <span className="menu-caret">▾</span></Button>
          </Dropdown>
          <Dropdown
            trigger={['click']}
            menu={{
              items: menus.tools,
              selectable: true,
              selectedKeys: layoutPreset === 'custom' ? [] : [layoutPreset],
              onClick: ({ key }) => {
                if (key === 'layout-edit') onApplyLayoutPreset('edit');
                else if (key === 'layout-image') onApplyLayoutPreset('image');
                else if (key === 'layout-reset') onApplyLayoutPreset(layoutPreset === 'custom' ? 'edit' : layoutPreset);
              }
            }}
          >
            <Button type="text" className="topbar-domain-action">工具 <span className="menu-caret">▾</span></Button>
          </Dropdown>
        </div>
        <Dropdown trigger={['click']} menu={menus.more}>
          <Button className="topbar-more">更多 <span className="menu-caret">▾</span></Button>
        </Dropdown>
        <div className="topbar-account"><SecurityAccountBadge /></div>
      </div>
      <div className="topbar-workflow-row">
        <div className="topbar-left">
          <div className="workflow-identity" title={`${workflow.name} · ${workflow.id}`}>
            {/* 名称可点击重命名；技术 ID 收进悬停详情，不再常驻占位 */}
            <Typography.Text
              className="workflow-name"
              editable={{
                onChange: (value) => {
                  // 重命名属于文档内容；统一载入入口比较完整快照，使旧响应失效。
                  const decision = renameDecision(value, workflow.name);
                  if (decision.changed) {
                    workflow.onRename(decision.name);
                  }
                },
                tooltip: '点击重命名工作流',
                icon: <span className="workflow-rename-icon">✎</span>
              }}
            >{workflow.name}</Typography.Text>
            <Tag
              className="workflow-dirty"
              color={workflow.dirty ? 'gold' : 'default'}
              title={workflow.dirty
                ? '有未保存的修改（Ctrl+S 保存）'
                : workflow.lastSavedAt
                  ? `已保存 · ${workflow.lastSavedAt.toLocaleTimeString(dateLocale(getInitialLang()))}`
                  : '自上次保存或加载以来没有修改'}
            >
              {workflow.dirty ? '未保存' : workflow.lastSavedAt ? `已保存 ${workflow.lastSavedAt.toLocaleTimeString(dateLocale(getInitialLang()), { hour: '2-digit', minute: '2-digit' })}` : '已保存'}
            </Tag>
          </div>
        </div>
        <div className="topbar-right">
          {/* 目录回退常驻标签（P0）：后端节点目录不可用时只有一个转瞬即逝的提示——参数可能与运行时不一致，
              用户需要随时可见的状态；恢复成功后自动消失 */}
          {catalogError && (
            <Tooltip title={`后端节点目录不可用，正在使用本地回退目录（节点参数可能与运行时不一致）：${catalogError}`}>
              <Tag color="orange" className="catalog-fallback-tag">目录回退</Tag>
            </Tooltip>
          )}

          <Tooltip title={validation.status === 'ok' && !validation.needsRevalidate ? validation.summary : undefined}>
            <Badge
              count={validation.status === 'issues' && !validation.needsRevalidate ? validation.issueCount : 0}
              dot={validation.needsRevalidate}
              color={validation.needsRevalidate ? 'gold' : undefined}
              size="small"
              offset={[10, 0]}
            >
              <Button onClick={() => void validation.onValidate()} loading={validation.busy}>
                {validation.needsRevalidate
                  ? '需要重新校验'
                  : validation.status === 'ok'
                    ? '校验通过 ✓'
                    : validation.status === 'issues'
                      ? `校验 · ${validation.issueCount} 项问题`
                      : '校验'}
              </Button>
            </Badge>
          </Tooltip>
          <Button onClick={save.onSave} loading={save.busy}>保存</Button>
          {/* P1：运行中把主按钮切换为“中止”——前端断开请求即触发后端取消（见 useRunController.abortRun） */}
          {run.running
            ? <>
                <RunClock startedAt={run.startedAt} />
                <Button danger onClick={run.onAbort} title="中止本次运行：断开请求并通知后端取消，设备动作按意图登记收尾">中止</Button>
              </>
            : <Button type="primary" onClick={run.onRun} disabled={run.debugBusy || debug.inSession} title={debug.inSession ? '请先结束调试会话，再运行流程' : undefined}>运行</Button>}
        </div>
      </div>
    </header>
  );
}
