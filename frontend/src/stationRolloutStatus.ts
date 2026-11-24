import type { ContractDto } from './types';

type Station = ContractDto<'ProductionStationOverview'>;
export type StationRollout = ContractDto<'StationRecipeRollout'>;
export type StationRolloutState = 'Unconfirmed' | 'Rejected' | 'Unverified' | 'OtherRecipe'
  | 'AwaitingPublish' | 'AwaitingSwitch' | 'Locked' | 'Running' | 'PublishedChanged' | 'HashMismatch';

export function stationRolloutState(station: Station, rollout: StationRollout, stale = false): StationRolloutState {
  const { delivery } = rollout;
  if (delivery.outcome !== 'Received') return delivery.outcome;
  const sampledAt = station.lastSeenAt ? Date.parse(station.lastSeenAt) : NaN;
  const { status } = station;
  if (stale || station.connection !== 'Online' || !status || !(sampledAt > Date.parse(rollout.createdAt)) || typeof delivery.localVersion !== 'number' || delivery.localVersion <= 0) {
    return 'Unverified';
  }
  if (status.jobId !== rollout.jobId) return 'OtherRecipe';
  if (!status.job) return 'Unverified';

  const hash = rollout.workflowHash.toLowerCase();
  const localVersion = delivery.localVersion;
  const published = status.job.publishedVersion === localVersion;
  const locked = status.lockedVersion === localVersion;
  if ((published && (typeof status.job.publishedWorkflowHash !== 'string' || !status.job.publishedWorkflowHash))
      || (locked && (typeof status.lockedWorkflowHash !== 'string' || !status.lockedWorkflowHash))) return 'Unverified';
  if ((published && status.job.publishedWorkflowHash?.toLowerCase() !== hash)
      || (locked && status.lockedWorkflowHash?.toLowerCase() !== hash)) return 'HashMismatch';
  if (locked && !published) return 'PublishedChanged';
  if (locked) return status.state === 'Running' ? 'Running' : 'Locked';
  return published ? 'AwaitingSwitch' : 'AwaitingPublish';
}
