import type { CameraSynchronizationGroup } from '../types';

/** 媒体标签的展示色（媒体库表格与预览共用）。 */
export const labelColor: Record<string, string> = { Unlabeled: 'default', OK: 'green', NG: 'red', Review: 'gold' };

/** 媒体标签的中文文案（表格、筛选器与预览共用）。 */
export const labelText: Record<string, string> = { Unlabeled: '未标注', OK: '合格', NG: '不合格', Review: '待复核' };

/** 字节数自适应单位；媒体库容量统计与列表共用同一口径。 */
export const formatBytes = (value: number) => value < 1024 ? `${value} B` : value < 1024 * 1024 ? `${(value / 1024).toFixed(1)} KiB` : `${(value / 1024 / 1024).toFixed(1)} MiB`;

/** 同步组表单值：由持久化模型派生，id 在新建时可为空。 */
export type SyncGroupFormValues = Omit<CameraSynchronizationGroup, 'id' | 'configurationHash' | 'createdAt' | 'updatedAt'> & { id?: string };

/** 设备状态标签色（采集列表与「采集」标签页共用）。 */
export const stateColor: Record<string, string> = { Closed: 'default', Open: 'blue', Streaming: 'green', Faulted: 'red' };

/** 采集状态标签色（采集列表与「采集」标签页共用）。 */
export const acquisitionColor: Record<string, string> = { Stopped: 'default', Starting: 'processing', Stopping: 'processing', StopUnconfirmed: 'red', Running: 'green', WaitingTrigger: 'gold', Reconnecting: 'orange', Faulted: 'red' };
