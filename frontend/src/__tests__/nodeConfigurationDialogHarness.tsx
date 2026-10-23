/**
 * NodeConfigurationDialog 测试共享件：ImageViewer 桩、默认目录/节点/结果夹具、
 * mount() 包装与全局环境桩（ResizeObserver/matchMedia/fetch）。
 *
 * 从 nodeConfigurationDialog.test.tsx 拆出（2026-10-23，测试反馈环优化）：
 * 原单文件 190 项串行约 211s，按域拆分后由 vitest 多线程并行。
 * 注意：本模块必须被测试文件**首个**导入——vi.mock 的注册依赖模块求值顺序。
 */
import { cleanup, render, screen, within } from '@testing-library/react';
import { afterEach, beforeEach, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';

vi.mock('../components/ImageViewer', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../components/ImageViewer')>();
  return {
    ...actual,
    default: ({ previewUrl, showRoiTools, roiEnabled, roiTargetLabel, roi, onRoiChange, overlays = [], measurementGeometry, measurementGeometryEditable, onMeasurementGeometryChange }: {
      previewUrl?: string; showRoiTools?: boolean; roiEnabled: boolean; onRoiChange: (roi?: VisionRoi) => void; overlays?: VisionOverlay[];
      roi?: VisionRoi;
      roiTargetLabel?: string;
      measurementGeometry?: { type: 'line'; start: { x: number; y: number }; end: { x: number; y: number }; profileSpan?: number; profileCount?: number; profileWidth?: number; direction?: string }
        | { type: 'circle'; center: { x: number; y: number }; radius: number; band?: number; angleStartDeg?: number; angleEndDeg?: number; profileWidth?: number; searchDirection?: string; centerEditable?: boolean; radiusEditable?: boolean };
      measurementGeometryEditable?: boolean;
      onMeasurementGeometryChange?: (geometry: NonNullable<typeof measurementGeometry>) => void;
    }) =>
      <div><span data-testid="image-url" data-geometry={measurementGeometry?.type} data-geometry-editable={Boolean(measurementGeometryEditable)}>{previewUrl}</span><span data-testid="roi-enabled">{roiEnabled ? 'ROI 可编辑' : 'ROI 只读'}</span>
        <span data-testid="roi-preview-shape" data-type={roi?.type ?? 'none'} data-editable={roiEnabled ? 'true' : 'false'}
          data-x={roi && 'x' in roi ? roi.x : undefined} data-y={roi && 'y' in roi ? roi.y : undefined}
          data-width={roi && 'width' in roi ? roi.width : undefined} data-height={roi && 'height' in roi ? roi.height : undefined} />
        <span data-testid="roi-target">{roiTargetLabel}</span><span>{showRoiTools ? 'ROI 工具' : '纯图像预览'}</span>
        <span data-testid="overlay-ids">{overlays.map(overlay => overlay.id).join(',')}</span>
        {measurementGeometry?.type === 'circle' && <span data-testid="circle-geometry" data-center-x={measurementGeometry.center.x}
          data-center-y={measurementGeometry.center.y} data-radius={measurementGeometry.radius} data-band={measurementGeometry.band}
          data-profile-width={measurementGeometry.profileWidth}
          data-angle-start={measurementGeometry.angleStartDeg} data-angle-end={measurementGeometry.angleEndDeg}
          data-search-direction={measurementGeometry.searchDirection}
          data-editable={Boolean(measurementGeometryEditable)}
          data-center-editable={measurementGeometry.centerEditable !== false} data-radius-editable={measurementGeometry.radiusEditable !== false} />}
        {measurementGeometry?.type === 'line' && <span data-testid="line-geometry" data-start-x={measurementGeometry.start.x}
          data-start-y={measurementGeometry.start.y} data-end-x={measurementGeometry.end.x} data-end-y={measurementGeometry.end.y}
          data-profile-span={measurementGeometry.profileSpan} data-profile-width={measurementGeometry.profileWidth}
          data-direction={measurementGeometry.direction}
          data-editable={Boolean(measurementGeometryEditable)} />}
        {measurementGeometryEditable && measurementGeometry?.type === 'line' && <button onClick={() => onMeasurementGeometryChange?.({ type: 'line', start: { x: 23, y: 31 }, end: { x: 91, y: 47 } })}>模拟拖动搜索线</button>}
        {measurementGeometryEditable && measurementGeometry?.type === 'line' && measurementGeometry.profileSpan !== undefined
          && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry, profileSpan: 45 })}>模拟调整采样跨度</button>}
        {measurementGeometryEditable && measurementGeometry && measurementGeometry.profileWidth !== undefined
          && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry, profileWidth: 11 })}>模拟调整投影宽度</button>}
        {measurementGeometryEditable && measurementGeometry?.type === 'circle' && measurementGeometry.centerEditable !== false && measurementGeometry.radiusEditable !== false
          && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry,
            center: { x: 42, y: 51 }, radius: 17, band: 14, angleStartDeg: 350, angleEndDeg: 450 })}>模拟调整标称圆</button>}
        {measurementGeometryEditable && measurementGeometry?.type === 'circle' && (measurementGeometry.centerEditable === false || measurementGeometry.radiusEditable === false)
          && <button onClick={() => onMeasurementGeometryChange?.({ ...measurementGeometry, band: 14, angleStartDeg: 350, angleEndDeg: 450 })}>模拟调整圆弧</button>}
        {showRoiTools && roiEnabled && <button onClick={() => onRoiChange(undefined)}>清除测试 ROI</button>}</div>
  };
});

export const catalog: NodeCatalogItem = {
  type: 'text.validate', displayName: '文本校验', category: '校验', pluginId: 'builtin',
  description: '按预期内容校验文字。',
  inputs: [{ name: 'text', label: '文本', dataType: 'String', required: true }],
  outputs: [{ name: 'pass', label: '通过', dataType: 'Boolean' }],
  parameters: [{ name: 'expected', label: '预期内容', type: 'text', defaultValue: 'ABC' }]
};
export const node: Node = { id: 'test', position: { x: 0, y: 0 }, data: { label: '批号检查', typeKey: 'text.validate', parameters: { expected: 'ABC' } } };
export const report: RunResult = {
  runId: 'test-run', success: true, totalDurationMs: 2, previewAvailable: true, previewWidth: 100, previewHeight: 100,
  nodeReports: [{ nodeId: 'test', nodeType: 'text.validate', success: true, durationMs: 1.2, summary: { pass: true },
    inputs: { text: { type: 'String', display: 'ABC', value: 'ABC' } },
    outputs: { pass: { type: 'Boolean', display: 'true', value: true } } }]
};

export function mount(overrides: Partial<Parameters<typeof NodeConfigurationDialog>[0]> = {}) {
  const props = { open: true, node, catalogItem: catalog, nodes: [node], edges: [], disabled: false, onApply: vi.fn(), onClose: vi.fn(), onPreview: vi.fn(async () => report), ...overrides };
  const view = render(<NodeConfigurationDialog {...props} />);
  return { ...view, props, dialog: within(screen.getByRole('dialog')) };
}
beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
  vi.stubGlobal('matchMedia', vi.fn(() => ({ matches: false, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn() })));
  vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [{ nodeId: 'test', portName: 'image', width: 100, height: 100 }, { nodeId: 'other', portName: 'image', width: 100, height: 100 }] }) })));
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
