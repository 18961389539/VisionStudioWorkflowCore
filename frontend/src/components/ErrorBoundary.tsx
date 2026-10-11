import { Component, type ErrorInfo, type ReactNode } from 'react';

type Props = { children: ReactNode };
type State = { error?: Error; report?: string };

/**
 * 顶层错误边界：组件抛错时给出可恢复入口（导出诊断 / 尝试恢复 / 重新加载），而不是整页白屏。
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

  render() {
    if (this.state.error) {
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
            <button className="ant-btn ant-btn-primary" onClick={() => this.setState({ error: undefined, report: undefined })}>尝试恢复</button>
            <button className="ant-btn" onClick={() => window.location.reload()}>重新加载</button>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}
