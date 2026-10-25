import { Alert, Button } from 'antd';

/**
 * 统一的"加载失败"提示（2026-10-25）。
 *
 * 背景：此前多个面板把 `!response.ok` 当成"没有数据"——`catch { setAssets([]) }`、
 * `if (!response.ok) return;`、`.catch(() => undefined)`——现场把后端故障读成
 * "设备没注册 / 没有标定 / 没有数据集"，进而重复操作。空态与失败态必须在视觉上区分开：
 * 空态用 Empty + 行动按钮，失败用本组件（红色 + 原因 + 重试）。
 */
type Props = {
  /** 出错的数据对象，用于标题：如"标定资产" */
  scope: string;
  /** 失败原因（含 HTTP 状态/业务码/关联号）；空值时不渲染 */
  error?: string | null;
  onRetry?: () => void;
  retrying?: boolean;
};

export default function DataErrorAlert({ scope, error, onRetry, retrying = false }: Props) {
  if (!error) return null;
  return (
    <Alert
      className="data-error-alert"
      type="error"
      showIcon
      title={`${scope}加载失败`}
      description={error}
      action={onRetry
        ? <Button size="small" ghost type="primary" loading={retrying} onClick={onRetry}>重试</Button>
        : undefined}
    />
  );
}
