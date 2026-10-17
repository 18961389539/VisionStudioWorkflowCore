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

function mount(type = catalog.type, runResult = result(false, 'too-few-edge-points'), parameters = catalog.parameters) {
  const selectedCatalog = { ...catalog, type, parameters };
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

  it('explains the minimum-valid-caliper gate and shows the fitted count', () => {
    const strictResult = result(false, 'insufficient-valid-calipers');
    strictResult.nodeReports[0].summary = {
      found: false, reason: 'insufficient-valid-calipers', sampleCount: 7, minValidCalipers: 8,
      rejectedCalipers: 2, ignoredByFit: 1
    };
    mount('measure.caliperLine', strictResult);

    expect(screen.getByText(/拟合后保留的有效卡尺数低于“最少有效卡尺数”/)).toBeTruthy();
    const summary = screen.getByLabelText('关键测量结果');
    expect(summary.textContent).toContain('有效采样数 7');
    expect(summary.textContent).toContain('最少有效卡尺数 8');

    cleanup();
    const circleResult = result(false, 'insufficient-valid-calipers');
    circleResult.nodeReports[0].nodeType = 'measure.caliperCircle';
    circleResult.nodeReports[0].summary = {
      found: false, reason: 'insufficient-valid-calipers', sampleCount: 5, minValidCalipers: 6,
      rejectedSpokes: 4, ignoredByFit: 1
    };
    mount('measure.caliperCircle', circleResult);
    expect(screen.getByLabelText('关键测量结果').textContent).toContain('有效采样数 5');
    expect(screen.getByLabelText('关键测量结果').textContent).toContain('最少有效卡尺数 6');
  });

  it('keeps the successful status when the measurement found an edge', () => {
    mount(catalog.type, result(true, 'ok'));

    expect(screen.getByText('执行成功')).toBeTruthy();
    expect(screen.queryByText('未找到有效结果')).toBeNull();
  });

  it('uses catalog labels for imported port snapshots regardless of port-name casing', () => {
    const runResult = result(true, 'ok');
    runResult.nodeReports[0].inputs = { SEARCHLINE: { type: 'Line2D', display: '[(10, 20), (80, 20)]' } };
    runResult.nodeReports[0].outputs = { FOUND: { type: 'Boolean', display: 'true', value: true } };
    const selectedCatalog = { ...catalog,
      inputs: [{ name: 'searchLine', label: '搜索线', dataType: 'Line2D' }],
      outputs: [{ name: 'found', label: '找到目标', dataType: 'Boolean' }] };

    render(<NodeExecutionResult nodeId="caliper" catalog={selectedCatalog} result={runResult} stale={false}
      expanded onExpandedChange={() => {}} />);

    expect(screen.getByText('[(10, 20), (80, 20)]').closest('table')?.textContent).toContain('搜索线');
    expect(screen.getByText('true').closest('table')?.textContent).toContain('找到目标');
    expect(screen.queryByText('SEARCHLINE')).toBeNull();
    expect(screen.queryByText('FOUND')).toBeNull();
  });

  it('puts the no-result state before empty single-caliper coordinates', () => {
    const caliperResult = result(false, 'no-edge-above-threshold');
    caliperResult.nodeReports[0].summary = { found: false, reason: 'no-edge-above-threshold', x: null, y: null, strength: null };
    mount('measure.caliper', caliperResult);

    const summary = screen.getByLabelText('关键测量结果');
    expect(summary.textContent).toBe('找到目标 否');
  });

  it('shows finite sample count when a single-caliper ROI fragments the profile', () => {
    const caliperResult = result(false, 'profile-has-insufficient-contiguous-samples');
    caliperResult.nodeReports[0].nodeType = 'measure.caliper';
    caliperResult.nodeReports[0].summary = {
      found: false, reason: 'profile-has-insufficient-contiguous-samples', sampleCount: 3
    };
    mount('measure.caliper', caliperResult);

    expect(screen.getByText(/ROI 将其切成了过短片段/)).toBeTruthy();
    expect(screen.getByLabelText('关键测量结果').textContent).toContain('有效采样数 3');
  });

  it('localizes and prioritizes the fitted radius and roundness metrics', () => {
    const circleResult = result(true, 'ok');
    circleResult.nodeReports[0].nodeType = 'measure.caliperCircle';
    circleResult.nodeReports[0].summary = { found: true, fittedRadius: 12.5, fitError: 0.2, radiusDelta: 0.5, radiusSpread: 1.2 };
    mount('measure.caliperCircle', circleResult);

    const summary = screen.getByLabelText('关键测量结果');
    expect(summary.textContent).toContain('拟合半径 12.5');
    expect(summary.textContent).toContain('半径偏差 0.5');
    expect(summary.textContent).toContain('半径极差 1.2');
    expect(summary.textContent).toContain('拟合误差 0.2');
    expect(summary.textContent).not.toContain('fittedRadius');
  });

  it('surfaces valid, rejected, and fit-ignored calipers in the line result summary', () => {
    const lineResult = result(true, 'ok');
    lineResult.nodeReports[0].summary = {
      found: true, sampleCount: 8, rejectedCalipers: 1, ignoredByFit: 1, fitError: 0.2
    };
    mount('measure.caliperLine', lineResult);

    const summary = screen.getByLabelText('关键测量结果');
    expect(summary.textContent).toContain('有效采样数 8');
    expect(summary.textContent).toContain('拒绝卡尺数 1');
    expect(summary.textContent).toContain('拟合剔除点数 1');
    expect(summary.textContent).toContain('拟合误差 0.2');
  });

  it('prioritizes the fragmented-profile counter for caliper line and circle failures', () => {
    const lineResult = result(false, 'profile-has-insufficient-contiguous-samples');
    lineResult.nodeReports[0].summary = {
      found: false, reason: 'profile-has-insufficient-contiguous-samples',
      profilesWithInsufficientContiguousSamples: 3, sampleCount: 0
    };
    mount('measure.caliperLine', lineResult);
    expect(screen.getByLabelText('关键测量结果').textContent).toContain('连续采样不足剖面数 3');

    cleanup();
    const circleResult = result(false, 'profile-has-insufficient-contiguous-samples');
    circleResult.nodeReports[0].nodeType = 'measure.caliperCircle';
    circleResult.nodeReports[0].summary = {
      found: false, reason: 'profile-has-insufficient-contiguous-samples',
      spokesWithInsufficientContiguousSamples: 12, sampleCount: 0
    };
    mount('measure.caliperCircle', circleResult);
    expect(screen.getByLabelText('关键测量结果').textContent).toContain('连续采样不足卡尺数 12');
  });

  it('explains when the search geometry is clipped or excluded by the ROI', () => {
    mount(catalog.type, result(false, 'search-region-clipped-too-short'));
    expect(screen.getByText(/平行搜索剖面与图像相交的部分不足 2 px/)).toBeTruthy();

    cleanup();
    mount(catalog.type, result(false, 'profile-has-no-finite-samples'));
    expect(screen.getByText(/ROI 可能屏蔽了搜索线/)).toBeTruthy();

    cleanup();
    mount(catalog.type, result(false, 'profile-has-insufficient-contiguous-samples'));
    expect(screen.getByText(/ROI 将其切成了过短片段/)).toBeTruthy();

    cleanup();
    mount('measure.caliperCircle', result(false, 'profile-has-insufficient-contiguous-samples'));
    expect(screen.getByText(/径向剖面有采样点，但 ROI 将其切成了过短片段/)).toBeTruthy();
  });

  it('tailors no-edge guidance to the caliper geometry', () => {
    mount('measure.caliperLine', result(false, 'no-edge-above-threshold'));
    expect(screen.getByText(/沿边缘布置的平行搜索剖面/)).toBeTruthy();
    expect(screen.queryByText(/标称圆/)).toBeNull();

    cleanup();
    mount('measure.caliperCircle', result(false, 'no-edge-above-threshold'));
    expect(screen.getByText(/标称圆的径向搜索弧/)).toBeTruthy();
    expect(screen.queryByText(/搜索线/)).toBeNull();

    cleanup();
    mount('measure.caliper', result(false, 'no-edge-above-threshold'));
    expect(screen.getByText(/搜索线内没有达到最小对比度/)).toBeTruthy();
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

  it('localizes the debug-image preview state for an ROI transform', () => {
    const roiResult = result(true, 'ok');
    roiResult.nodeReports[0].nodeType = 'roi.transform';
    roiResult.nodeReports[0].summary = { debugImageConnected: true, debugImagePreviewReady: true };
    delete roiResult.nodeReports[0].outputs;
    mount('roi.transform', roiResult);

    const summary = screen.getByLabelText('关键测量结果');
    expect(summary.textContent).toContain('已连接调试图像 是');
    expect(summary.textContent).toContain('调试图像预览就绪 是');
    expect(summary.textContent).not.toContain('debugImagePreviewReady');
  });

  it('summarizes captured reference pixels instead of dumping the image payload into result details', () => {
    const roiResult = result(true, 'ok');
    roiResult.nodeReports[0].nodeType = 'roi.transform';
    const referenceImage = 'encoded-reference-image-'.repeat(20);
    roiResult.nodeReports[0].summary = { referenceCaptured: true, referenceImage, referenceWidth: 640, referenceHeight: 480 };
    delete roiResult.nodeReports[0].outputs;
    mount('roi.transform', roiResult);

    expect(screen.getAllByText(/图像数据已隐藏，\d+ 个 Base64 字符/)).toHaveLength(2);
    expect(screen.queryByText(referenceImage)).toBeNull();
    expect(screen.getByLabelText('关键测量结果').textContent).not.toContain(referenceImage);
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

    cleanup();
    mount('measure.caliperCircle', failedResult('Caliper circle center after coordinate correction must be finite and within +/-10000000 pixels.'));
    expect(screen.getByText(/坐标修正后的圆心必须是有效坐标/)).toBeTruthy();
    expect(screen.getByText('Caliper circle center after coordinate correction must be finite and within +/-10000000 pixels.')).toBeTruthy();
  });

  it('uses localized parameter labels for invalid numeric execution errors', () => {
    mount('measure.caliperLine', failedResult("Parameter 'minContrast' must be a finite number."), [
      { name: 'minContrast', label: '最小对比度', type: 'number' }
    ]);
    expect(screen.getByText(/参数“最小对比度”必须是有限数值/)).toBeTruthy();
    expect(screen.getByText("Parameter 'minContrast' must be a finite number.")).toBeTruthy();
  });

  it('localizes invalid caliper choice parameters and lists their allowed choices', () => {
    mount('measure.caliperLine', failedResult("Parameter 'polarity' must be one of [any, rising, falling]."), [
      { name: 'polarity', label: '边缘极性', type: 'select', options: [
        { label: '任意', value: 'Any' }, { label: '由暗到亮', value: 'Rising' }, { label: '由亮到暗', value: 'Falling' }
      ] }
    ]);

    expect(screen.getByText(/参数“边缘极性”的选项值无效/)).toBeTruthy();
    expect(screen.getByText(/任意、由暗到亮、由亮到暗/)).toBeTruthy();
    expect(screen.getByText("Parameter 'polarity' must be one of [any, rising, falling].")).toBeTruthy();
  });

  it.each(['feature.edgeSubpixel', 'measure.caliperLine'])('localizes malformed ROI geometry errors from %s', type => {
    mount(type, failedResult('Polygon ROI requires 3..500000 finite vertices within ±10000000 px.'));

    expect(screen.getByText('多边形 ROI 至少需要 3 个有效顶点，且坐标在允许范围内。')).toBeTruthy();
  });

  it.each(['feature.edgeSubpixel', 'measure.caliperCircle'])('localizes oversized connected polygon ROI errors from %s', type => {
    mount(type, failedResult('Polygon ROI exceeds the 500000-point limit.'));

    expect(screen.getByText('多边形 ROI 最多允许 500000 个顶点。')).toBeTruthy();
  });

  it('keeps unknown native errors intact for diagnosis', () => {
    const nativeError = 'HALCON error #3022: Wrong size of filter for Gauss in operator measure_pos';
    mount('measure.caliper', failedResult(nativeError));
    expect(screen.getByText(nativeError)).toBeTruthy();
    expect(screen.queryByText('节点执行失败')).toBeNull();
  });
});
