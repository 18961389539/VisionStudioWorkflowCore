import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import NodeExecutionResult from '../components/NodeExecutionResult';
import type { NodeCatalogItem, RunResult } from '../types';

const catalog: NodeCatalogItem = {
  type: 'measure.caliperLine', displayName: '卡尺找线', category: 'Measurement', pluginId: 'builtin',
  inputs: [], outputs: [{ name: 'found', dataType: 'Boolean' }], parameters: []
};

function result(found: boolean, reason: string): RunResult {
  return {
    runId: 'run-1', success: true, totalDurationMs: 1, previewAvailable: false,
    nodeReports: [{ nodeId: 'caliper', nodeType: catalog.type, success: true, durationMs: 1,
      summary: { found, reason }, outputs: { found: { type: 'Boolean', display: String(found), value: found } } }]
  };
}

function failedResult(error: string): RunResult {
  const run = result(false, 'execution-error');
  run.success = false;
  run.nodeReports[0] = { ...run.nodeReports[0], success: false, error };
  return run;
}

function mount(type = catalog.type, runResult = result(false, 'too-few-edge-points')) {
  const selectedCatalog = { ...catalog, type };
  return render(<NodeExecutionResult nodeId="caliper" catalog={selectedCatalog} result={runResult} stale={false}
    expanded onExpandedChange={() => {}} />);
}

afterEach(cleanup);

describe('NodeExecutionResult measurement status', () => {
  it('shows a warning and a useful reason when the caliper ran but found too few edges', () => {
    mount();

    expect(screen.getByText('未找到有效结果')).toBeTruthy();
    expect(screen.getByText('节点已运行，但未检出目标')).toBeTruthy();
    expect(screen.getByText(/找到的边缘点不足 3 个，无法拟合/)).toBeTruthy();
    expect(screen.queryByText('执行成功')).toBeNull();
  });

  it('keeps the successful status when the measurement found an edge', () => {
    mount(catalog.type, result(true, 'ok'));

    expect(screen.getByText('执行成功')).toBeTruthy();
    expect(screen.queryByText('未找到有效结果')).toBeNull();
  });

  it('explains when the search geometry is clipped or excluded by the ROI', () => {
    mount(catalog.type, result(false, 'search-region-clipped-too-short'));
    expect(screen.getByText(/平行搜索剖面与图像相交的部分不足 2 px/)).toBeTruthy();

    cleanup();
    mount(catalog.type, result(false, 'profile-has-no-finite-samples'));
    expect(screen.getByText(/ROI 可能屏蔽了搜索线/)).toBeTruthy();
  });

  it('explains when circle points cannot support a stable fit', () => {
    mount('measure.caliperCircle', result(false, 'degenerate-circle-fit'));

    expect(screen.getByText(/边缘点分布接近直线，无法稳定拟合圆/)).toBeTruthy();

    cleanup();
    mount('measure.caliperCircle', result(false, 'too-few-edge-points'));
    expect(screen.getByText(/增加卡尺数量、扩大搜索弧角范围/)).toBeTruthy();
  });

  it('does not apply caliper detection status to unrelated nodes', () => {
    mount('measure.distance', result(false, 'no-edge-above-threshold'));

    expect(screen.getByText('执行成功')).toBeTruthy();
    expect(screen.queryByText('节点已运行，但未检出目标')).toBeNull();
  });

  it('keeps crop dimensions and the input-image origin visible beside image outputs', () => {
    const cropResult = result(true, 'ok');
    cropResult.nodeReports[0].summary = { roiType: 'Rectangle', width: 120, height: 80, cropOriginX: 25, cropOriginY: 40 };
    mount('image.crop', cropResult);

    const summary = screen.getByLabelText('关键测量结果');
    expect(summary.textContent).toContain('宽度 120');
    expect(summary.textContent).toContain('高度 80');
    expect(summary.textContent).toContain('裁剪原点 X 25');
    expect(summary.textContent).toContain('裁剪原点 Y 40');
  });

  it('shows the corrected angle and pose in the ROI transform result', () => {
    const roiResult = result(true, 'ok');
    roiResult.nodeReports[0].summary = { roiType: 'Polygon', thetaDeg: 12.5, basePose: '100,80,0', currentPose: '130,90,12.5' };
    mount('roi.transform', roiResult);

    const summary = screen.getByLabelText('关键测量结果');
    expect(summary.textContent).toContain('ROI 类型 Polygon');
    expect(summary.textContent).toContain('角度修正 12.5');
    expect(summary.textContent).toContain('基准位姿 100,80,0');
    expect(summary.textContent).toContain('当前位姿 130,90,12.5');
  });

  it('localizes common crop and ROI-transform errors while keeping raw diagnostics available', () => {
    mount('image.crop', failedResult('Image crop requires a non-empty image.'));
    expect(screen.getByText('节点执行失败')).toBeTruthy();
    expect(screen.getByText(/裁剪输入图像为空/)).toBeTruthy();
    expect(screen.getByText('Image crop requires a non-empty image.')).toBeTruthy();

    cleanup();
    mount('roi.transform', failedResult('Scene dimensions differ from the saved reference. Capture a new reference before correcting the ROI.'));
    expect(screen.getByText(/当前场景图像尺寸与已保存基准不一致/)).toBeTruthy();
    expect(screen.getByText(/重新获取基准/)).toBeTruthy();
  });

  it('explains invalid caliper geometry and connected circle inputs in Chinese', () => {
    mount('measure.caliperLine', failedResult('The search line is too short (minimum 2 px).'));
    expect(screen.getByText(/搜索线长度不足 2 px/)).toBeTruthy();
    expect(screen.getByText('The search line is too short (minimum 2 px).')).toBeTruthy();

    cleanup();
    mount('measure.caliperCircle', failedResult('The connected circle input must have a finite center and positive radius.'));
    expect(screen.getByText(/上游圆心或半径无效/)).toBeTruthy();
  });

  it('keeps unknown native errors intact for diagnosis', () => {
    const nativeError = 'HALCON error #3022: Wrong size of filter for Gauss in operator measure_pos';
    mount('measure.caliper', failedResult(nativeError));
    expect(screen.getByText(nativeError)).toBeTruthy();
    expect(screen.queryByText('节点执行失败')).toBeNull();
  });
});
