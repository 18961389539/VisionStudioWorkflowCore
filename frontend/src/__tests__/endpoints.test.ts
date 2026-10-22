import { describe, expect, it } from 'vitest';
import { API_BASE, apiUrl, jobs, validation, cameras, debug, runs } from '../endpoints';

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
});
