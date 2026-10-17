import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import { fallbackCatalog } from '../catalog';
import type { RunResult } from '../types';
import { diagnosticManifest, diagnosticProfile, diagnosticReport, jsonResponse } from './fixtures/caliperDiagnostics';

vi.mock('../components/ImageViewer', async importOriginal => {
  const actual = await importOriginal<typeof import('../components/ImageViewer')>();
  return { ...actual, default: (props: Parameters<typeof actual.default>[0]) => <div>
    <output data-testid="image-url">{props.previewUrl}</output>
    <output data-testid="roi-editable">{String(props.roiEnabled)}</output>
    <output data-testid="geometry-editable">{String(props.measurementGeometryEditable)}</output>
    {props.caliperDiagnosticSelection && <div data-testid="diagnostic-geometry" data-center-x={props.caliperDiagnosticSelection.center.x}
      data-center-y={props.caliperDiagnosticSelection.center.y} data-index={props.caliperDiagnosticSelection.index}>
      <button onClick={() => props.caliperDiagnosticSelection!.onSelect(1)}>选择第二条诊断卡尺</button>
    </div>}
  </div> };
});

const catalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
const circle: Node = { id: 'circle', position: { x: 0, y: 0 }, data: { label: '卡尺找圆', typeKey: catalog.type,
  inputs: catalog.inputs, outputs: catalog.outputs, parameters: { centerX: 50, centerY: 50, radius: 20, radialBand: 4,
    caliperCount: 4, samplingMode: 'count', minContrast: 8, sampleWidth: 1 } } };
const source: Node = { id: 'source', position: { x: 0, y: 0 }, data: { label: '输入', typeKey: 'image.synthetic' } };
const edges = [{ id: 'image', source: 'source', sourceHandle: 'image', target: 'circle', targetHandle: 'image' }];
const run: RunResult = { runId: 'run-a', success: true, totalDurationMs: 5, previewAvailable: false, overlays: [],
  nodeReports: [diagnosticReport()] };
const catalogResponse = { images: ['source', 'circle'].map(nodeId => ({ nodeId, portName: 'image', width: 100, height: 100 })) };

beforeEach(() => {
  const getComputedStyle = window.getComputedStyle.bind(window);
  vi.spyOn(window, 'getComputedStyle').mockImplementation(element => getComputedStyle(element));
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} })));
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

function mount(manifest = diagnosticManifest(), result = run, additional: Partial<Parameters<typeof NodeConfigurationDialog>[0]> = {}) {
  const fetch = vi.fn(async (input: RequestInfo | URL) => {
    const url = String(input);
    if (url.endsWith('/images')) return jsonResponse(catalogResponse);
    return jsonResponse(url.includes('?') ? diagnosticProfile(Number(new URL(url, 'http://local.test').searchParams.get('spokeIndex'))) : manifest);
  });
  vi.stubGlobal('fetch', fetch);
  const props = { node: circle, catalogItem: catalog, nodes: [source, circle], edges, open: true, disabled: false,
    onClose: vi.fn(), onApply: vi.fn(), onPreview: vi.fn(async () => run), result, ...additional };
  return { ...render(<NodeConfigurationDialog {...props} />), props, fetch };
}
const openDiagnostics = () => fireEvent.click(screen.getByRole('radio', { name: '单卡尺诊断' }));

describe('caliper diagnostic configuration workspace', () => {
  it('opens an actual-run image and captured geometry in read-only mode and selects spokes without preview execution', async () => {
    const view = mount();
    await waitFor(() => expect(screen.getByTestId('image-url').textContent).toContain('/image'));
    expect(view.fetch.mock.calls.some(call => String(call[0]).includes('caliper-diagnostics'))).toBe(false);
    openDiagnostics();
    await waitFor(() => expect(screen.getByTestId('diagnostic-geometry').dataset.index).toBe('0'));
    expect(screen.getByTestId('image-url').textContent).toBe('/api/runs/run-a/nodes/circle/image?portName=image');
    expect(screen.getByTestId('roi-editable').textContent).toBe('false');
    expect(screen.getByTestId('geometry-editable').textContent).toBe('false');
    fireEvent.click(screen.getByRole('button', { name: '选择第二条诊断卡尺' }));
    await waitFor(() => expect(screen.getByText('拟合离群点')).toBeTruthy());
    expect(screen.getByTestId('diagnostic-geometry').dataset.index).toBe('1');
    expect(view.props.onPreview).not.toHaveBeenCalled(); expect(view.props.onApply).not.toHaveBeenCalled();
  });

  it('preserves the run snapshot after editing a threshold and marks it stale without automatically rerunning', async () => {
    const view = mount(); openDiagnostics();
    await waitFor(() => expect(screen.getByTestId('diagnostic-geometry')).toBeTruthy());
    const requests = view.fetch.mock.calls.length;
    fireEvent.change(screen.getByRole('spinbutton', { name: '最小边缘对比度' }), { target: { value: '40' } });
    fireEvent.blur(screen.getByRole('spinbutton', { name: '最小边缘对比度' }));
    await waitFor(() => expect(screen.getByText('诊断已过期 · 当前参数待验证')).toBeTruthy());
    expect(screen.getByText('64 / 8')).toBeTruthy();
    expect(screen.getByTestId('image-url').textContent).toContain('/run-a/nodes/circle/');
    expect(view.fetch).toHaveBeenCalledTimes(requests); expect(view.props.onPreview).not.toHaveBeenCalled();
  });

  it('uses captured current-frame coordinates rather than the editable reference circle', async () => {
    const manifest = diagnosticManifest(); manifest.center = { x: 30, y: 40 };
    const fixture: Node = { id: 'fixture', position: { x: 0, y: 0 }, data: { typeKey: 'coordinate.transformConstant', parameters: {} } };
    mount(manifest, run, { nodes: [source, fixture, circle], edges: [...edges,
      { id: 'transform', source: 'fixture', sourceHandle: 'transform', target: 'circle', targetHandle: 'transform' }] });
    openDiagnostics();
    await waitFor(() => expect(screen.getByTestId('diagnostic-geometry').dataset.centerX).toBe('30'));
    expect(screen.getByTestId('diagnostic-geometry').dataset.centerY).toBe('40');
    expect(screen.getByTestId('image-url').textContent).toBe('/api/runs/run-a/nodes/circle/image?portName=image');
  });

  it.each(['dimensions', 'execution'] as const)('does not put diagnostic geometry on an unverified image (%s mismatch)', async mismatch => {
    const manifest = diagnosticManifest();
    if (mismatch === 'dimensions') manifest.imageWidth = 200;
    else manifest.executionSequence = 6;
    mount(manifest); openDiagnostics();
    if (mismatch === 'execution') await waitFor(() => expect(screen.getByText(/诊断与当前运行报告不一致/)).toBeTruthy());
    // Manifest then profile are two sequential fetches inside the full configuration dialog.
    // The default 1s waitFor budget is enough locally and too short on a loaded Windows runner.
    else await waitFor(() => expect(screen.getByRole('img', { name: /所选卡尺灰度剖面/ })).toBeTruthy(), { timeout: 8_000 });
    expect(screen.queryByTestId('diagnostic-geometry')).toBeNull();
    expect(screen.getByTestId('image-url').textContent).toBe('');
  }, 20_000);

  it('removes old geometry and image after a failed newer execution even if its image catalog still has an old output', async () => {
    const view = mount(); openDiagnostics();
    await waitFor(() => expect(screen.getByTestId('diagnostic-geometry')).toBeTruthy());
    const failure = { ...run, runId: 'run-b', success: false, nodeReports: [{ ...diagnosticReport(8), success: false, summary: {}, error: 'failed' }] };
    view.rerender(<NodeConfigurationDialog {...view.props} result={failure} />);
    await waitFor(() => expect(screen.queryByTestId('diagnostic-geometry')).toBeNull());
    expect(screen.getByTestId('image-url').textContent).toBe('');
    expect(within(screen.getByRole('region', { name: '单卡尺诊断' })).getByText(/请运行此节点以采集诊断/)).toBeTruthy();
    expect(view.props.onPreview).not.toHaveBeenCalled();
  });
});
