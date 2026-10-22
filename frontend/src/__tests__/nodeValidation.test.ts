import { describe, expect, it } from 'vitest';
import type { Edge, Node } from '@xyflow/react';
import { fallbackCatalogRaw, makeDefaultParameters } from '../catalog';
import { configurationIssues, cropRoiOutsideImageIssue } from '../nodeConfigurationState';
import { parameterPresentation, validateParameterValues, validateWorkflow, isParameterVisible } from '../validation';
import type { NodeCatalogItem, ParameterDescriptor } from '../types';

const item = (type: string) => fallbackCatalogRaw.find(entry => entry.type === type)!;
const asNode = (catalog: NodeCatalogItem, parameters = makeDefaultParameters(catalog)): Node => ({
  id: catalog.type, position: { x: 0, y: 0 },
  data: { typeKey: catalog.type, inputs: catalog.inputs, outputs: catalog.outputs, parameters }
});
const source: NodeCatalogItem = { type: 'test.source', displayName: 'Source', category: 'Test', inputs: [], parameters: [],
  outputs: [{ name: 'image', dataType: 'Image' }, { name: 'line', dataType: 'Line2D' }, { name: 'next', dataType: 'Control' }] };
const connect = (target: string, port: string, sourcePort = port): Edge => ({
  id: `${target}-${port}`, source: source.type, target, sourceHandle: sourcePort, targetHandle: port,
  data: { kind: port === 'exec' ? 'control' : 'data' }
});

describe('consistent node validation', () => {
  it.each([
    ['collection.detections.at', 'index', 0.4],
    ['collection.detections.sort', 'topN', 0.4],
    ['collection.detections.filter', 'classId', 1.5],
    ['flow.forEach', 'maxItems', 1.5]
  ] as const)('rejects fractional %s %s in parameter, configuration and workflow validation', (type, name, value) => {
    const catalog = item(type);
    const parameters = { ...makeDefaultParameters(catalog), [name]: value };
    expect(validateParameterValues(catalog.parameters, parameters))
      .toContainEqual({ name, message: '必须为整数' });
    expect(configurationIssues(catalog, parameters).join(' ')).toContain('必须为整数');
    expect(validateWorkflow([asNode(catalog, parameters)], [catalog], [])
      .some(issue => issue.parameter === name && issue.message.includes('必须为整数'))).toBe(true);
  });

  it.each([
    ['collection.detections.at', 'index', 0, 199],
    ['collection.detections.sort', 'topN', 0, 10000],
    ['collection.detections.filter', 'classId', -1, 999999999],
    ['flow.forEach', 'maxItems', 1, 500]
  ] as const)('preserves %s %s defaults, integer boundaries and sentinels', (type, name, minimum, maximum) => {
    const catalog = item(type);
    for (const parameters of [{}, { [name]: minimum }, { [name]: maximum }])
      expect(configurationIssues(catalog, parameters), JSON.stringify(parameters)).toEqual([]);
  });

  it('does not interpret the numeric step as an integer constraint', () => {
    const catalog = item('collection.detections.filter');
    expect(catalog.parameters.find(parameter => parameter.name === 'minArea')?.step).toBe(1);
    expect(configurationIssues(catalog, { minArea: 0.5 })).toEqual([]);
  });

  it.each([
    ['Control', 'Image', 'control'], ['Image', 'Control', 'data'],
    ['String', 'Image', 'data'], ['Image', 'Image', 'control']
  ])('keeps required %s -> %s inputs unsatisfied for %s edges', (sourceType, targetType, kind) => {
    const upstream = { ...source, outputs: [{ name: 'out', dataType: sourceType }] };
    const target: NodeCatalogItem = { type: 'test.target', displayName: 'Target', category: 'Test', parameters: [],
      inputs: [{ name: 'value', dataType: targetType, required: true }], outputs: [] };
    const edge: Edge = { id: 'invalid', source: upstream.type, sourceHandle: 'out', target: target.type, targetHandle: 'value', data: { kind } };
    expect(validateWorkflow([asNode(upstream), asNode(target)], [upstream, target], [edge])
      .some(issue => issue.id === `port-missing-${target.type}-value`)).toBe(true);
  });

  it.each(['node', 'port'])('does not satisfy required inputs from a missing source %s', (missing) => {
    const target: NodeCatalogItem = { type: 'test.target', displayName: 'Target', category: 'Test', parameters: [],
      inputs: [{ name: 'image', dataType: 'Image', required: true }], outputs: [] };
    const edge = connect(target.type, 'image', missing === 'port' ? 'unknown' : 'image');
    const nodes = missing === 'node' ? [asNode(target)] : [asNode(source), asNode(target)];
    expect(validateWorkflow(nodes, [source, target], [edge])
      .some(issue => issue.id === `port-missing-${target.type}-image`)).toBe(true);
  });

  it('keeps the catalog defaults valid for every built-in node', () => {
    for (const catalog of fallbackCatalogRaw)
      expect(validateParameterValues(catalog.parameters, makeDefaultParameters(catalog)), catalog.type).toEqual([]);
  });

  it.each(['42', true, [], {}, NaN, Infinity])('rejects a nonnumeric parameter without coercion: %j', value => {
    expect(validateParameterValues([{ name: 'n', label: 'Number', type: 'number' }], { n: value }))
      .toEqual([{ name: 'n', message: '不是有效数字' }]);
  });

  it('validates booleans and text using the same types as the backend', () => {
    const descriptors: ParameterDescriptor[] = [
      { name: 'flag', label: 'Flag', type: 'boolean', defaultValue: false },
      { name: 'text', label: 'Text', type: 'textarea', defaultValue: '' }
    ];
    expect(validateParameterValues(descriptors, { flag: 'false', text: 123 }).map(issue => issue.name)).toEqual(['flag', 'text']);
    expect(validateParameterValues(descriptors, {})).toEqual([]);
    expect(validateParameterValues(descriptors, { flag: false, text: '' })).toEqual([]);
  });

  it('validates only active number and arithmetic bounds', () => {
    const number = item('value.number');
    expect(configurationIssues(number, { ...makeDefaultParameters(number), min: null, max: 'unused' })).toEqual([]);
    expect(configurationIssues(number, { ...makeDefaultParameters(number), enforceBounds: true, min: null }).length).toBeGreaterThan(0);
    const arithmetic = item('value.arithmetic');
    expect(configurationIssues(arithmetic, { ...makeDefaultParameters(arithmetic), op: 'abs', clampMin: null, clampMax: 'unused' })).toEqual([]);
    expect(configurationIssues(arithmetic, { ...makeDefaultParameters(arithmetic), op: 'clamp', clampMin: null }).length).toBeGreaterThan(0);
  });

  it.each(['text.match', 'text.validate'])('uses connected OCR text instead of stale %s fallback parameters', type => {
    const catalog = item(type);
    const parameters = { ...makeDefaultParameters(catalog), text: 123, expected: false };
    expect(configurationIssues(catalog, parameters, { connectedInputPorts: ['text', 'expected'] })).toEqual([]);
    expect(configurationIssues(catalog, parameters, { connectedInputPorts: ['text'] }).join(' ')).toContain('需要文本');
    expect(configurationIssues(catalog, parameters).length).toBe(2);
  });

  it('accepts omitted defaults and optional nulls while retaining range and required-value checks', () => {
    const descriptors: ParameterDescriptor[] = [
      { name: 'width', label: 'Width', type: 'number', defaultValue: null, min: 1, optional: true },
      { name: 'threshold', label: 'Threshold', type: 'number', defaultValue: 128, min: 0, max: 255 }
    ];
    expect(validateParameterValues(descriptors, {})).toEqual([]);
    expect(validateParameterValues(descriptors, { width: null })).toEqual([]);
    expect(validateParameterValues(descriptors, { threshold: null })).toHaveLength(1);
    expect(validateParameterValues(descriptors, { width: 0 })).toHaveLength(1);
    expect(validateParameterValues(descriptors, { width: '10' })).toHaveLength(1);
  });

  it('applies select options and variant ranges without case-sensitive gaps', () => {
    const descriptors: ParameterDescriptor[] = [
      { name: 'operator', label: 'Operator', type: 'select', defaultValue: 'zoom', options: [{ label: 'Zoom', value: 'zoom' }] },
      { name: 'factor', label: 'Factor', type: 'number', min: 1, defaultValue: 1, variantParameter: 'operator',
        variants: { zoom: { min: 0.01 } } }
    ];
    expect(parameterPresentation(descriptors[1], descriptors, { operator: 'ZOOM' }).min).toBe(0.01);
    expect(validateParameterValues(descriptors, { operator: 'ZOOM', factor: 0.5 })).toEqual([]);
    expect(validateParameterValues(descriptors, { operator: 1 })).toHaveLength(1);
  });

  it('hides engine- and mode-specific advanced controls until they apply', () => {
    const catalog = item('match.shape');
    const defaults = makeDefaultParameters(catalog);
    const visible = (patch: Record<string, unknown> = {}) => {
      const values = { ...defaults, ...patch };
      return catalog.parameters.filter((parameter) =>
        isParameterVisible(parameter, catalog.parameters, values)).map((parameter) => parameter.name);
    };

    expect(visible()).not.toContain('chamferTolerancePx');
    expect(visible()).not.toContain('trainingOriginX');
    expect(visible({ engine: 'managedChamfer' })).toContain('chamferTolerancePx');
    expect(visible({ trainingOriginMode: 'manual' })).toContain('trainingOriginX');

    const template = item('match.template');
    const templateDefaults = makeDefaultParameters(template);
    const templateVisible = (patch: Record<string, unknown> = {}) => {
      const values = { ...templateDefaults, ...patch };
      return template.parameters.filter((parameter) =>
        isParameterVisible(parameter, template.parameters, values)).map((parameter) => parameter.name);
    };
    expect(templateVisible()).not.toContain('scoreMapWeighting');
    expect(templateVisible({ emitScoreMap: true })).toContain('scoreMapWeighting');
    expect(templateVisible()).not.toContain('ignoreSoftWeight');
    expect(templateVisible({ ignoreMode: 'soft' })).toContain('ignoreSoftWeight');
  });

  it('splits every parameter-heavy built-in node into at least two sections', () => {
    for (const catalog of fallbackCatalogRaw) {
      const parameters = catalog.parameters.filter((parameter) => !parameter.internal);
      if (parameters.length < 8) continue;
      const groups = new Set(parameters.map((parameter) => parameter.group).filter(Boolean));
      expect(groups.size, catalog.type).toBeGreaterThan(1);
    }
  });

  it('does not expose generated placeholder parameter help', () => {
    for (const catalog of fallbackCatalogRaw) {
      for (const parameter of catalog.parameters) {
        expect(parameter.description ?? '', `${catalog.type}.${parameter.name}`)
          .not.toMatch(/parameter for [a-z]+\./i);
      }
    }
  });

  it('hides preprocessing and diagnostic controls that do not apply', () => {
    const visibleNames = (type: string, patch: Record<string, unknown>) => {
      const catalog = item(type);
      const values = { ...makeDefaultParameters(catalog), ...patch };
      return catalog.parameters.filter((parameter) =>
        isParameterVisible(parameter, catalog.parameters, values)).map((parameter) => parameter.name);
    };
    expect(visibleNames('recognition.barcode', { preprocess: 'none' })).not.toContain('adaptiveBlockSize');
    expect(visibleNames('recognition.barcode', { preprocess: 'adaptive' })).toContain('adaptiveBlockSize');
    expect(visibleNames('calibration.detectBoard', { rejectWeakViews: false })).not.toContain('minBlurVariance');
    expect(visibleNames('calibration.detectBoard', { rejectWeakViews: true })).toContain('minBlurVariance');
    expect(visibleNames('measure.caliperArray', { includeProfiles: false })).not.toContain('profileSampleCap');
    expect(visibleNames('measure.caliperArray', { includeProfiles: true })).toContain('profileSampleCap');
    expect(visibleNames('calibration.cameraIntrinsics', { cameraModel: 'pinhole' })).not.toContain('telecentricMagnification');
    expect(visibleNames('calibration.cameraIntrinsics', { cameraModel: 'telecentric' })).toContain('telecentricMagnification');
  });

  it('rejects malformed switch case lists before apply', () => {
    const catalog = item('flow.switch');
    expect(configurationIssues(catalog, { casesJson: '["A","B"]' })).toEqual([]);
    expect(configurationIssues(catalog, { casesJson: 'not-json' }).join(' ')).toContain('JSON 字符串数组');
    expect(configurationIssues(catalog, { casesJson: '["A","A"]' }).join(' ')).toContain('唯一');
    expect(configurationIssues(catalog, { casesJson: '["A",""]' }).join(' ')).toContain('不能为空');
    expect(configurationIssues(catalog, { casesJson: '[]' }).join(' ')).toContain('1–16');
  });

  it('uses the same default switch cases as the executor when the parameter is omitted', () => {
    const catalog = item('flow.switch');
    expect(configurationIssues(catalog, {})).toEqual([]);
    expect(configurationIssues(catalog, { casesJson: undefined })).toEqual([]);
  });

  it.each([null, '', '[]', 'not-json', '["A","A"]', '["A",""]'])('does not hide an explicit invalid switch case list: %j', (casesJson) => {
    expect(configurationIssues(item('flow.switch'), { casesJson }).length).toBeGreaterThan(0);
  });

  it('keeps switch labels case-sensitive and accepts the 16-case boundary', () => {
    const catalog = item('flow.switch');
    expect(configurationIssues(catalog, { casesJson: '["A","a"]' })).toEqual([]);
    expect(configurationIssues(catalog, { casesJson: JSON.stringify(Array.from({ length: 16 }, (_, index) => `case-${index}`)) })).toEqual([]);
    expect(configurationIssues(catalog, { casesJson: JSON.stringify(Array.from({ length: 17 }, (_, index) => `case-${index}`)) }).join(' ')).toContain('1–16');
  });

  it('hides image-saving codec options for the other format', () => {
    const catalog = item('output.saveImage');
    const defaults = makeDefaultParameters(catalog);
    const visible = (patch: Record<string, unknown>) => {
      const values = { ...defaults, ...patch };
      return catalog.parameters.filter((parameter) =>
        isParameterVisible(parameter, catalog.parameters, values)).map((parameter) => parameter.name);
    };
    expect(visible({ format: 'jpeg' })).toContain('jpegQuality');
    expect(visible({ format: 'jpeg' })).not.toContain('pngCompression');
    expect(visible({ format: 'png' })).toContain('pngCompression');
    expect(visible({ format: 'png' })).not.toContain('jpegQuality');
    expect(visible({ format: 'bmp' })).not.toContain('jpegQuality');
    expect(visible({ format: 'bmp' })).not.toContain('pngCompression');
  });

  it('marks perspective output size optional and enables corner editing', () => {
    const catalog = item('image.perspective');
    const width = catalog.parameters.find((parameter) => parameter.name === 'width')!;
    const height = catalog.parameters.find((parameter) => parameter.name === 'height')!;
    expect(catalog.capabilities?.supportsRoi).toBe(true);
    expect(width.optional).toBe(true);
    expect(height.optional).toBe(true);
    expect(validateParameterValues(catalog.parameters, makeDefaultParameters(catalog))).toEqual([]);
    expect(validateParameterValues(catalog.parameters, { width: 1 })).toHaveLength(1);
    expect(validateParameterValues(catalog.parameters, { width: null, height: null })).toEqual([]);
  });

  it.each(['image.resize', 'jlvision.measure1d'])('allows a newly created %s node to use automatic dimensions or center', type => {
    const catalog = item(type);
    const parameters = makeDefaultParameters(catalog);
    expect(configurationIssues(catalog, parameters)).toEqual([]);
    expect(validateWorkflow([asNode(source), asNode(catalog)], [...fallbackCatalogRaw, source], [
      connect(type, 'image'), connect(type, 'exec', 'next')
    ])).toEqual([]);
  });

  it('uses connected geometry in full-workflow validation, retaining other active parameter errors', () => {
    const catalog = item('measure.caliperLine');
    const parameters = { ...makeDefaultParameters(catalog), startX: 'inactive', endY: null };
    const edges = [connect(catalog.type, 'image'), connect(catalog.type, 'exec', 'next'), connect(catalog.type, 'searchLine', 'line')];
    const validate = (values: Record<string, unknown>, connections = edges) => validateWorkflow(
      [asNode(source), asNode(catalog, values)], [...fallbackCatalogRaw, source], connections);
    expect(validate(parameters)).toEqual([]);
    expect(validate({ ...parameters, minContrast: -1 }).some(issue => issue.parameter === 'minContrast')).toBe(true);
    expect(validate(parameters, edges.map(edge => edge.targetHandle === 'searchLine' ? { ...edge, sourceHandle: 'image' } : edge))
      .some(issue => issue.parameter === 'startX')).toBe(true);
  });

  it('reports impossible combined settings from the same rules used by the configuration dialog', () => {
    const catalog = item('measure.caliperLine');
    const parameters = { ...makeDefaultParameters(catalog), caliperCount: 5, minValidCalipers: 6 };
    const issues = validateWorkflow([asNode(source), asNode(catalog, parameters)], [...fallbackCatalogRaw, source], [
      connect(catalog.type, 'image'), connect(catalog.type, 'exec', 'next')
    ]);
    const message = configurationIssues(catalog, parameters).find(issue => issue.includes('最少有效卡尺数'))!;
    expect(message).toBeTruthy();
    expect(issues.some(issue => issue.nodeId === catalog.type && issue.message.includes(message))).toBe(true);
  });

  it('accepts legacy ROI enum casing while rejecting invalid geometry', () => {
    const catalog = item('region.fromRoi');
    const parameters = { ...makeDefaultParameters(catalog), roi: { type: 'rectangle', x: 0, y: 0, width: 10, height: 10 } };
    expect(configurationIssues(catalog, parameters)).toEqual([]);
    expect(configurationIssues(catalog, { ...parameters, roi: { ...parameters.roi, width: 0 } }).join(' ')).toContain('正尺寸');
    expect(configurationIssues(catalog, { ...parameters, roi: { type: 'brush', strokes: [
      { mode: 'add', width: 2, points: [{ x: 1, y: 1 }] }
    ] } })).toEqual([]);
    expect(cropRoiOutsideImageIssue({ ...parameters.roi, x: 100 }, 80, 60)).toContain('没有像素交集');
  });
});
