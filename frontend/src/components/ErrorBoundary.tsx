import { Component, type ErrorInfo, type ReactNode } from 'react';

type Props = { children: ReactNode };
type State = { error?: Error };

/**
 * 顶层错误边界：组件抛错时给出可恢复入口（尝试恢复 / 重新加载），而不是整页白屏。
 * 现场操作员无需开发者工具即可继续工作。
 */
export default class ErrorBoundary extends Component<Props, State> {
  state: State = {};

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    // 保留控制台诊断痕迹（供现场导出日志使用）。
    console.error('VisionStudio UI crashed:', error, info.componentStack);
  }

  render() {
    if (this.state.error) {
      return (
        <div style={{ padding: 32, fontFamily: 'sans-serif', lineHeight: 1.7 }}>
          <h2>界面遇到未处理的错误</h2>
          <p style={{ color: '#888', wordBreak: 'break-all' }}>{this.state.error.message}</p>
          <div style={{ display: 'flex', gap: 8, marginTop: 12 }}>
            <button className="ant-btn ant-btn-primary" onClick={() => this.setState({ error: undefined })}>尝试恢复</button>
            <button className="ant-btn" onClick={() => window.location.reload()}>重新加载</button>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}
