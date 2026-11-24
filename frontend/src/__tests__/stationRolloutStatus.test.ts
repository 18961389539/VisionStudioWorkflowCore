import { describe, expect, it } from 'vitest';
import { stationRolloutState, type StationRollout } from '../stationRolloutStatus';
import type { ContractDto } from '../types';

const hash = 'a'.repeat(64);
const received: StationRollout = {
  batchId: 'batch-1', jobId: 'recipe', jobName: 'Recipe', sourceVersion: 4, workflowHash: hash,
  createdAt: '2026-11-23T08:31:00Z', delivery: { stationId: 'line-a', stationName: 'Line A', localVersion: 7, outcome: 'Received' },
};
const status: ContractDto<'ProductionStationStatus'> = {
  stationId: 'line-a', state: 'Running', jobId: 'recipe',
  job: { name: 'Recipe', publishedVersion: 5, publishedWorkflowHash: 'b'.repeat(64) },
  lockedVersion: 5, lockedWorkflowHash: 'b'.repeat(64),
  cycleCount: 1, okCount: 1, ngCount: 0, activeAlarms: 0, criticalAlarms: 0, pendingAlarms: 0,
};
const station: ContractDto<'ProductionStationOverview'> = {
  id: 'line-a', name: 'Line A', operatorUrl: 'https://example.com/', connection: 'Online',
  lastSeenAt: '2026-11-23T08:32:00Z', status, versionCheck: 'NotConfigured',
  recipeDeliveryEnabled: true, productionControlEnabled: false,
};

describe('station rollout progress', () => {
  it('requires both local version and fingerprint to verify publication and runtime', () => {
    expect(stationRolloutState(station, received)).toBe('AwaitingPublish');
    const published = { ...status, job: { name: 'Recipe', publishedVersion: 7, publishedWorkflowHash: hash.toUpperCase() } };
    expect(stationRolloutState({ ...station, status: published }, received)).toBe('AwaitingSwitch');
    const switched = { ...published, lockedVersion: 7, lockedWorkflowHash: hash };
    expect(stationRolloutState({ ...station, status: switched }, received)).toBe('Running');
    expect(stationRolloutState({ ...station, status: { ...switched, state: 'Faulted' } }, received)).toBe('Locked');
    expect(stationRolloutState({ ...station, status: { ...switched, job: status.job } }, received)).toBe('PublishedChanged');
    expect(stationRolloutState({ ...station, status: { ...switched, lockedWorkflowHash: 'wrong' } }, received)).toBe('HashMismatch');
    expect(stationRolloutState({ ...station, status: { ...switched, lockedWorkflowHash: null } }, received)).toBe('Unverified');
  });

  it('avoids attributing cached or differently scoped station data to a delivery', () => {
    expect(stationRolloutState({ ...station, connection: 'Offline' }, received)).toBe('Unverified');
    expect(stationRolloutState(station, received, true)).toBe('Unverified');
    expect(stationRolloutState({ ...station, lastSeenAt: '2026-11-23T08:30:00Z' }, received)).toBe('Unverified');
    expect(stationRolloutState({ ...station, status: { ...status, jobId: 'another' } }, received)).toBe('OtherRecipe');
    expect(stationRolloutState(station, {
      ...received, delivery: { ...received.delivery, outcome: 'Unconfirmed' },
    })).toBe('Unconfirmed');
  });
});
