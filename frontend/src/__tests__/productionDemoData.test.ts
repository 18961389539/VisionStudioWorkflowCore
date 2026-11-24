import { describe, expect, it } from 'vitest';
import { demoInspectionPreviews, demoRecipeDeliveries, demoStationRollouts, demoStations } from '../productionDemoData';
import { stationRolloutState } from '../stationRolloutStatus';

describe('production simulation data', () => {
  it('covers healthy, awaiting-switch and unavailable stations', () => {
    const stations = demoStations();
    expect(stations).toHaveLength(3);
    expect(stations.map((station) => station.connection)).toEqual(['Online', 'Online', 'Timeout']);
    expect(stations.map((station) => station.versionCheck)).toEqual(['Matches', 'PublishedOnly', 'Unverified']);

    const rollouts = demoStationRollouts();
    expect(stationRolloutState(stations[0]!, rollouts[0]!)).toBe('Running');
    expect(stationRolloutState(stations[1]!, rollouts[1]!)).toBe('AwaitingSwitch');
    expect(rollouts[2]!.delivery.outcome).toBe('Unconfirmed');
  });

  it('covers pending, validation and published recipe receipts', () => {
    const receipts = demoRecipeDeliveries();
    expect(receipts).toHaveLength(3);
    expect(receipts[0]!.publishedVersion).not.toBe(receipts[0]!.localVersion);
    expect(receipts[1]!.requiresValidation).toBe(true);
    expect(receipts[2]!.publishedVersion).toBe(receipts[2]!.localVersion);
  });

  it('provides three distinct inspection images with OK and NG results', () => {
    const previews = demoInspectionPreviews();
    expect(previews).toHaveLength(3);
    expect(previews.map((preview) => preview.disposition)).toEqual(['OK', 'OK', 'NG']);
    expect(new Set(previews.map((preview) => preview.imageUrl)).size).toBe(3);
    expect(previews.every((preview) => preview.imageUrl.startsWith('data:image/svg+xml'))).toBe(true);
  });
});
