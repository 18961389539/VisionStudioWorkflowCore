import { Table } from 'antd';
import type { TableProps } from 'antd';

/**
 * 面板表格的统一壳（2026-10-25）。
 *
 * 背景：全仓 47 处 `pagination={false}`、0 处列排序、仅 8 处 `scroll`——长表在 1280 宽容器里
 * 会被固定列宽压成多行，且没有任何"按什么排"的入口。这里给一套默认值：
 * 小尺寸、超过一页才显示分页（hideOnSinglePage）、横向可滚动、空态可配。
 * 需要特殊行为时照常传 `pagination` / `scroll` / `size` 覆盖。
 */
type Props<T> = TableProps<T> & {
  /** 空态文案：写清"这里为什么是空的、下一步做什么" */
  emptyText?: string;
  pageSize?: number;
};

export default function PanelTable<T extends object>({
  emptyText = '暂无数据',
  pageSize = 20,
  size = 'small',
  pagination,
  scroll,
  locale,
  ...rest
}: Props<T>) {
  return (
    <Table<T>
      {...rest}
      size={size}
      scroll={scroll ?? { x: 'max-content' }}
      locale={{ emptyText, ...locale }}
      pagination={pagination ?? {
        defaultPageSize: pageSize,
        hideOnSinglePage: true,
        showSizeChanger: true,
        pageSizeOptions: [10, 20, 50, 100],
        showTotal: (total) => `共 ${total} 条`,
        size: 'small'
      }}
    />
  );
}
