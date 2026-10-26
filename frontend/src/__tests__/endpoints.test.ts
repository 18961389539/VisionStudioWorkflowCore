import { describe, expect, it } from 'vitest';
import {
  API_BASE, apiUrl, jobs, validation, cameras, debug, runs,
  cameraSync, calibration, calibrations, cameraIntrinsics,
  devices, robots, investigations, media, storage, alarms,
  modelPackages, modules, diagnostics, hardwareProvenance
} from '../endpoints';

describe('endpoints', () => {
  it('builds API paths from the base', () => {
    expect(API_BASE).toBe('/api');
    expect(apiUrl('jobs')).toBe('/api/jobs');
    expect(apiUrl('/jobs')).toBe('/api/jobs');
  });

  it('encodes ID segments', () => {
    expect(jobs.byId('my job')).toBe('/api/jobs/my%20job');
    expect(validation.dataset('ds/1')).toBe('/api/validation/datasets/ds%2F1');
    expect(cameras.action('cam-1', 'open')).toBe('/api/cameras/cam-1/open');
    expect(debug.session('s-1')).toBe('/api/debug/sessions/s-1');
  });

  it('builds parameterised paths', () => {
    expect(jobs.diff('j1', 1, 3)).toBe('/api/jobs/j1/versions/1/diff/3');
    expect(validation.datasetItem('ds1', 'item 1')).toBe('/api/validation/datasets/ds1/items/item%201');
    expect(runs.nodeImage('r1', 'n1', 'image')).toBe('/api/runs/r1/nodes/n1/image?portName=image');
    expect(runs.nodeImage('r1', 'n1')).toBe('/api/runs/r1/nodes/n1/image');
  });

  it('builds camera-sync paths', () => {
    expect(cameraSync.group('g1')).toBe('/api/camera-sync/groups/g1');
    expect(cameraSync.groupStatus('g1')).toBe('/api/camera-sync/groups/g1/status');
    expect(cameraSync.commissioningTestCancel('t1')).toBe('/api/camera-sync/commissioning-tests/t1/cancel');
    expect(cameraSync.commissioningTestReportHtml('t1')).toBe('/api/camera-sync/commissioning-tests/t1/report.html');
  });

  it('builds calibration wizard paths', () => {
    expect(calibration.wizardSession('s1')).toBe('/api/calibration/wizard/sessions/s1');
    expect(calibration.wizardSessionViewEnabled('s1', 2)).toBe('/api/calibration/wizard/sessions/s1/views/2/enabled');
    expect(calibration.wizardSessionExport('s1')).toBe('/api/calibration/wizard/sessions/s1/export');
    expect(calibrations.rollback('c1', 'v2')).toBe('/api/calibrations/c1/rollback/v2');
  });

  it('builds device / robot / media / storage / investigation paths', () => {
    expect(devices.tag('d1', 't1')).toBe('/api/devices/d1/tags/t1');
    expect(robots.ack('r1', 'cmd-1')).toBe('/api/robots/r1/ack/cmd-1');
    expect(media.preview('a/b.png')).toBe('/api/media/preview?path=a%2Fb.png');
    expect(storage.backups).toBe('/api/storage/backups');
    expect(investigations.forTrace('run-1')).toBe('/api/investigations/for-trace/run-1');
    expect(hardwareProvenance.byKindAndId('camera', 'c1')).toBe('/api/hardware-provenance/camera/c1');
    // 2026-10-26：服务端改为分页（默认 200 条），客户端显式声明 take
    expect(alarms.active).toBe('/api/alarms?activeOnly=false&take=200');
    expect(diagnostics.events).toBe('/api/diagnostics/events?take=250');
  });
});
