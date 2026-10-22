import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Button, Card, Space, Typography } from 'antd';

type Props = {
  children: ReactNode;
  /**
   * page（默认）：整页恢复面，用于应用根节点。
   * panel：只替换出错的那一个面板，画布和其它面板继续工作——现场最怕的是打开一个次要面板
   * 触发渲染异常，把整个设计器（含未保存修改）一起打掉。
   */
  variant?: 'page' | 'panel';
  /** 面板名称，例如「相机」。仅 panel 模式使用。 */
  scope?: string;
  /** panel 模式下的关闭入口（通常把对应 open 置为 false），让用户不必刷新就能退出。 */
  onDismiss?: () => void;
};
type State = { error?: Error; report?: string };

/**
 * 错误边界：组件抛错时给出可恢复入口（导出诊断 / 尝试恢复 / 关闭或重新加载），而不是白屏。
 * 现场操作员无需开发者工具即可继续工作。
 *
 * 诊断不再只写 console：崩溃后把完整上下文（错误、栈、组件栈、时间、UA）保存在内存中并可
 * 一键导出为文本——现场 console 无法直接导出，UI 故障必须留下可交给后端工程师的耐久证据。
 */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = {};

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    const report = [
      'VisionStudio UI crash report',
      `scope: ${this.props.scope ?? 'application root'}`,
      `time: ${new Date().toISOString()}`,
      `url: ${window.location.href}`,
      `userAgent: ${navigator.userAgent}`,
      `error: ${error.name}: ${error.message}`,
      '',
      'stack:',
      error.stack ?? '<none>',
      '',
      'componentStack:',
      info.componentStack ?? '<none>'
    ].join('\n');
    this.setState({ error, report });
    // 保留控制台诊断痕迹（供现场远程调试使用）。
    console.error('VisionStudio UI crashed:', error, info.componentStack);
  }

  private exportReport = () => {
    const blob = new Blob([this.state.report ?? 'no diagnostic report captured'], { type: 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `visionstudio-crash-${new Date().toISOString().replace(/[:.]/g, '-')}.txt`;
    link.click();
    URL.revokeObjectURL(url);
  };

  private reset = () => this.setState({ error: undefined, report: undefined });

  private dismiss = () => {
    this.reset();
    this.props.onDismiss?.();
  };

  private renderPanelFallback(error: Error) {
    // 悬浮卡片而不是全屏遮罩：面板崩溃不应挡住画布和其它面板，操作员可以先继续手头工作，
    // 有空时再导出诊断/重试/关闭。
    return (
      <div
        role="alert"
        style={{ position: 'fixed', right: 16, bottom: 16, zIndex: 1100, width: 'min(520px, calc(100vw - 32px))' }}
      >
        <Card
          size="small"
          title={`${this.props.scope ?? '面板'}无法显示`}
          styles={{ body: { maxHeight: '50vh', overflow: 'auto' } }}
        >
          <Typography.Paragraph type="secondary" style={{ wordBreak: 'break-all' }}>{error.message}</Typography.Paragraph>
          <Typography.Paragraph type="secondary">
            该面板已单独停止渲染，画布和已保存的修改不受影响。建议先导出诊断交给工程支持，再重试或关闭面板。
          </Typography.Paragraph>
          <Space wrap>
            <Button onClick={this.exportReport}>导出诊断</Button>
            <Button type="primary" onClick={this.reset}>重试</Button>
            {this.props.onDismiss && <Button onClick={this.dismiss}>关闭面板</Button>}
          </Space>
        </Card>
      </div>
    );
  }

  render() {
    if (this.state.error) {
      if (this.props.variant === 'panel') return this.renderPanelFallback(this.state.error);
      return (
        <div style={{ padding: 32, fontFamily: 'sans-serif', lineHeight: 1.7 }}>
          <h2>界面遇到未处理的错误</h2>
          <p style={{ color: '#888', wordBreak: 'break-all' }}>{this.state.error.message}</p>
          <p style={{ color: '#888' }}>
            建议先「导出诊断」把故障上下文保存下来交给工程支持，再尝试恢复；
            若刚才有未保存的工作流修改，请勿直接刷新，先与工程师确认草稿/备份恢复方式。
          </p>
          <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
            <button className="ant-btn" onClick={this.exportReport}>导出诊断</button>
            <button className="ant-btn ant-btn-primary" onClick={this.reset}>尝试恢复</button>
            <button className="ant-btn" onClick={() => window.location.reload()}>重新加载</button>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}
