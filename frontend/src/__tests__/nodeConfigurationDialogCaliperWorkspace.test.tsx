/**
 * 卡尺几何写回/校验域（上） —— 从 nodeConfigurationDialog.test.tsx 拆分（2026-10-23，测试并行化）。
 */
import './nodeConfigurationDialogHarness';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';
import { catalog, node, report, mount } from './nodeConfigurationDialogHarness';

describe('caliper geometry input guidance', () => {
  const mountCropFrameMeasurement = (sourceType: string, targetType: string, outputPort: string, inputPort: string, separateGeometryFrame: boolean) => {
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const geometryCatalog = fallbackCatalog.find(item => item.type === sourceType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceType, label: '上游几何', inputs: geometryCatalog.inputs, parameters: {}
    } };
    const measurementCrop: Node = { id: 'measurement-crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '测量图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const geometryCrop: Node = { id: 'geometry-crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '几何图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const sourceImage: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '源图像' } };
    const geometryFrame = separateGeometryFrame ? geometryCrop : measurementCrop;
    const edges = [
      { id: 'measurement-crop-image', source: sourceImage.id, sourceHandle: 'image', target: measurementCrop.id, targetHandle: 'image' },
      ...(separateGeometryFrame ? [{ id: 'geometry-crop-image', source: sourceImage.id, sourceHandle: 'image', target: geometryCrop.id, targetHandle: 'image' }] : []),
      { id: 'geometry-image', source: geometryFrame.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: measurementCrop.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort }
    ];
    return mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, measurementCrop, ...(separateGeometryFrame ? [geometryCrop] : []), sourceImage], edges });
  };
  const mountCropTransformSourceMeasurement = (sourceType: string, targetType: string, outputPort: string, inputPort: string, separateGeometrySource: boolean) => {
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const geometryCatalog = fallbackCatalog.find(item => item.type === sourceType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceType, label: '上游几何', inputs: geometryCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const sourceImage: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '裁剪输入图像' } };
    const otherImage: Node = { id: 'other-image', position: { x: 0, y: 0 }, data: { label: '另一张图像' } };
    const geometryImage = separateGeometrySource ? otherImage : sourceImage;
    const edges = [
      { id: 'crop-image', source: sourceImage.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'geometry-image', source: geometryImage.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: cropNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    return mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, cropNode, sourceImage, ...(separateGeometrySource ? [otherImage] : [])], edges });
  };



  it('writes an interactively adjusted search line back into the staged parameters', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report });

    fireEvent.click(await dialog.findByRole('button', { name: '模拟拖动搜索线' }));
    await waitFor(() => {
      expect((dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'startX')!.label }) as HTMLInputElement).value).toBe('23');
      expect((dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'endY')!.label }) as HTMLInputElement).value).toBe('47');
    });
  });

  it('writes an interactively adjusted caliper profile span back into the staged edge length', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20, edgeLength: 0 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report });

    fireEvent.click(await dialog.findByRole('button', { name: '模拟调整采样跨度' }));
    await waitFor(() => {
      expect((dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'edgeLength')!.label }) as HTMLInputElement).value).toBe('45');
    });
  });

  it('passes the backend-effective projection width into the circle-caliper preview', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 50, centerY: 50, radius: 20, radialBand: 5, angleStartDeg: 0, angleEndDeg: 90,
        samplingMode: 'count', caliperCount: 3, sampleWidth: 4 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges, result: report });

    expect((await dialog.findByTestId('circle-geometry')).getAttribute('data-profile-width')).toBe('5');
    fireEvent.click(dialog.getByRole('button', { name: '模拟调整投影宽度' }));
    await waitFor(() => {
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'sampleWidth')!.label }) as HTMLInputElement).value).toBe('11');
      expect(dialog.getByTestId('circle-geometry').getAttribute('data-profile-width')).toBe('11');
    });
  });

  // 圆快捷设置与画布/参数/应用流程的联动需要多次完整渲染，满载时接近 5s 默认期限。
  it('synchronizes circle shortcuts with the canvas, parameter fields and the applied workflow', { timeout: 15_000 }, async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 50, centerY: 50, radius: 20, radialBand: 5, minContrast: 18, samplingMode: 'step', angleStepDeg: 10 } } };
    const onApply = vi.fn();
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, onApply, result: report });
    fireEvent.click(dialog.getByRole('button', { name: '上半圆' }));
    fireEvent.click(dialog.getByRole('button', { name: '144 条' }));
    fireEvent.click(dialog.getByRole('radio', { name: '由外向内' }));
    fireEvent.change(dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radialBand')!.label }), { target: { value: '11' } });
    fireEvent.change(dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'minContrast')!.label }), { target: { value: '24' } });
    fireEvent.change(dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'sampleWidth')!.label }), { target: { value: '7' } });
    const geometry = await dialog.findByTestId('circle-geometry');
    expect(geometry.getAttribute('data-angle-start')).toBe('-180');
    expect(geometry.getAttribute('data-angle-end')).toBe('0');
    expect(geometry.getAttribute('data-search-direction')).toBe('firstfromend');
    expect(geometry.getAttribute('data-band')).toBe('11');
    expect(geometry.getAttribute('data-profile-width')).toBe('7');
    expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'caliperCount')!.label }) as HTMLInputElement).value).toBe('144');
    expect(onApply).not.toHaveBeenCalled();
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
    expect(onApply.mock.calls[0][0]).toMatchObject({ centerX: 50, centerY: 50, radius: 20, radialBand: 11, minContrast: 24, sampleWidth: 7,
      angleStartDeg: -180, angleEndDeg: 0, samplingMode: 'count', caliperCount: 144, searchDirection: 'firstfromend' });
  });

  it('validates and resets sampling fields moved into circle quick settings without duplicate inputs', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 50, centerY: 50, radius: 20, radialBand: 5, minContrast: 18, sampleWidth: 4 } } };
    const { dialog, props } = mount({ node: circleNode, catalogItem: circleCatalog, result: report });
    const input = (name: string) => dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === name)!.label });
    for (const name of ['radialBand', 'minContrast', 'sampleWidth']) {
      expect(dialog.getAllByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === name)!.label })).toHaveLength(1);
    }
    fireEvent.change(input('radialBand'), { target: { value: '11' } });
    fireEvent.change(input('minContrast'), { target: { value: '' } });
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
    expect((dialog.getByRole('button', { name: /应\s*用/ }) as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(dialog.getByRole('button', { name: /恢复出厂默认/ }));
    for (const name of ['radialBand', 'minContrast', 'sampleWidth']) {
      expect((input(name) as HTMLInputElement).value).toBe(String(circleCatalog.parameters.find(parameter => parameter.name === name)!.defaultValue));
    }
    await waitFor(() => expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false));
    expect(props.onPreview).not.toHaveBeenCalled();
    expect(props.onApply).not.toHaveBeenCalled();
  });

  it('writes a dragged circle and wraparound arc back into staged parameters', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 20, centerY: 30, radius: 10, radialBand: 8, angleStartDeg: 350, angleEndDeg: 10 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges, result: report });

    fireEvent.click(await dialog.findByRole('button', { name: '模拟调整标称圆' }));
    await waitFor(() => {
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'centerX')!.label }) as HTMLInputElement).value).toBe('42');
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radius')!.label }) as HTMLInputElement).value).toBe('17');
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'angleStartDeg')!.label }) as HTMLInputElement).value).toBe('-10');
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'angleEndDeg')!.label }) as HTMLInputElement).value).toBe('90');
      expect((dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radialBand')!.label }) as HTMLInputElement).value).toBe('14');
    });
  });

  it('keeps an upstream circle fixed while allowing image-based arc and radial-band edits', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 20, centerY: 30, radius: 10, radialBand: 6, angleStartDeg: 0, angleEndDeg: 60 } } };
    const circleSource: Node = { id: 'circle-source', position: { x: 0, y: 0 }, data: { typeKey: 'geometry.circle', label: '上游拟合圆' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-override', source: circleSource.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'CIRCLE' }
    ];
    const circleResult: RunResult = { ...report, nodeReports: [{ nodeId: circleNode.id, nodeType: circleCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { CIRCLE: { type: 'Circle', display: '(68, 42, r=24)', value: { x: 68, y: 42, radius: 24 } } } }] };
    const onApply = vi.fn();
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, circleSource, imageNode], edges, result: circleResult, onApply });

    expect(dialog.getByText(/圆心和标称半径锁定为本次运行的上游输入/)).toBeTruthy();
    expect(dialog.queryByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'centerX')!.label })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'centerY')!.label })).toBeNull();
    expect(dialog.queryByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radius')!.label })).toBeNull();
    const geometry = await dialog.findByTestId('circle-geometry');
    expect(geometry.getAttribute('data-center-x')).toBe('68');
    expect(geometry.getAttribute('data-center-y')).toBe('42');
    expect(geometry.getAttribute('data-radius')).toBe('24');
    expect(geometry.getAttribute('data-center-editable')).toBe('false');
    expect(geometry.getAttribute('data-radius-editable')).toBe('false');

    fireEvent.click(dialog.getByRole('button', { name: '模拟调整圆弧' }));
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
    expect(onApply.mock.calls[0][0]).toMatchObject({ centerX: 20, centerY: 30, radius: 10, radialBand: 14, angleStartDeg: -10, angleEndDeg: 90 });
  });

  it('uses the current workflow circle after a changed local preview fails', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { radialBand: 6 } } };
    const circleSource: Node = { id: 'circle-source', position: { x: 0, y: 0 }, data: { typeKey: 'geometry.circle', label: '上游拟合圆' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-override', source: circleSource.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' }
    ];
    const withCircle = (x: number, y: number, radius: number): RunResult => ({ ...report, nodeReports: [{ nodeId: circleNode.id,
      nodeType: circleCatalog.type, success: true, durationMs: 1, summary: {},
      inputs: { circle: { type: 'Circle', display: `(${x}, ${y}, r=${radius})`, value: { x, y, radius } } } }] });
    const onPreview = vi.fn().mockResolvedValueOnce(withCircle(22, 28, 12)).mockRejectedValueOnce(new Error('local preview failed'));
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, circleSource, imageNode], edges,
      result: withCircle(68, 42, 24), onPreview });

    await waitFor(() => expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('68'));
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('22'));
    fireEvent.change(dialog.getByRole('spinbutton', { name: circleCatalog.parameters.find(parameter => parameter.name === 'radialBand')!.label }),
      { target: { value: '12' } });
    await waitFor(() => expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('68'));

    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(dialog.getByText('本次节点测试未完成')).toBeTruthy());
    expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('68');
  });

  it('allows arc and search-band edits for a connected circle in the saved transform reference frame', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 20, centerY: 30, radius: 10, radialBand: 6, angleStartDeg: 0, angleEndDeg: 60 } } };
    const circleSource: Node = { id: 'circle-source', position: { x: 0, y: 0 }, data: { typeKey: 'measure.fitCircle', label: '上游拟合圆' } };
    const roiNode: Node = { id: 'pose-correction', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-input', source: circleSource.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' },
      { id: 'pose-transform', source: roiNode.id, sourceHandle: 'transform', target: circleNode.id, targetHandle: 'transform' }
    ];
    const circleResult: RunResult = { ...report, nodeReports: [{ nodeId: circleNode.id, nodeType: circleCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { circle: { type: 'Circle', display: '(68, 42, r=24)', value: { x: 68, y: 42, radius: 24 } } } }] };
    const onApply = vi.fn();
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, circleSource, roiNode, imageNode], edges, result: circleResult, onApply });

    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,reference-png');
    expect(dialog.getByText(/图像预览显示对应基准图像/)).toBeTruthy();
    const geometry = await dialog.findByTestId('circle-geometry');
    expect(geometry.getAttribute('data-center-x')).toBe('68');
    expect(geometry.getAttribute('data-center-y')).toBe('42');
    expect(geometry.getAttribute('data-radius')).toBe('24');
    expect(geometry.getAttribute('data-center-editable')).toBe('false');

    fireEvent.click(dialog.getByRole('button', { name: '模拟调整圆弧' }));
    fireEvent.click(dialog.getByRole('button', { name: /应\s*用/ }));
    await waitFor(() => expect(onApply).toHaveBeenCalledOnce());
    expect(onApply.mock.calls[0][0]).toMatchObject({ centerX: 20, centerY: 30, radius: 10, radialBand: 14, angleStartDeg: -10, angleEndDeg: 90 });
  });

  it('blocks a zero-span circle arc before running the measurement', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 50, centerY: 50, radius: 20, angleStartDeg: 30, angleEndDeg: 30 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges, result: report });

    expect(dialog.getByText(/单条径向卡尺，无法拟合圆/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('explains when a dense step-mode circle sampling is raised to the spoke limit', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'dense-circle', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 50, centerY: 50, radius: 20, samplingMode: 'step',
        angleStartDeg: -180, angleEndDeg: 180, angleStepDeg: 0.5 } } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges });

    expect(dialog.getByText(/将使用 360 条卡尺，实际步长约 1°/)).toBeTruthy();
    expect(dialog.queryByText('请修正参数后再运行或应用')).toBeNull();
  });

  it('warns when many circle calipers still cover too narrow an arc for a stable fit', () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'narrow-circle', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆',
      inputs: circleCatalog.inputs, parameters: { centerX: 50, centerY: 50, radius: 20, samplingMode: 'count',
        angleStartDeg: 0, angleEndDeg: 10, caliperCount: 72, minValidCalipers: 3 } } };
    const imageNode: Node = { id: 'circle-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, imageNode], edges });

    expect(dialog.getByText(/理论测点分布接近直线/)).toBeTruthy();
    expect(dialog.queryByText('请修正参数后再运行或应用')).toBeNull();
  });

  it('allows preview when a connected searchLine replaces malformed local endpoints', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 'inactive', startY: 20, endX: 80, endY: null } } };
    const lineSource: Node = { id: 'line-source', position: { x: 0, y: 0 }, data: { typeKey: 'feature.line', label: '上游搜索线' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'line-override', source: lineSource.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' }
    ];
    const onPreview = vi.fn(async () => report);
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, lineSource, imageNode], edges, result: report, onPreview });

    expect(dialog.queryByText(/请修正参数后再运行或应用/)).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledOnce());
  });

  it('uses the current workflow search line after a changed local preview fails', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: {} } };
    const lineSource: Node = { id: 'line-source', position: { x: 0, y: 0 }, data: { typeKey: 'feature.line', label: '上游搜索线' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'line-override', source: lineSource.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' }
    ];
    const withLine = (start: { x: number; y: number }, end: { x: number; y: number }): RunResult => ({ ...report, nodeReports: [{ nodeId: lineNode.id,
      nodeType: lineCatalog.type, success: true, durationMs: 1, summary: {},
      inputs: { searchLine: { type: 'Line2D', display: 'search line', value: { start, end } } } }] });
    const onPreview = vi.fn().mockResolvedValueOnce(withLine({ x: 20, y: 30 }, { x: 60, y: 30 }))
      .mockRejectedValueOnce(new Error('local preview failed'));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, lineSource, imageNode], edges,
      result: withLine({ x: 70, y: 42 }, { x: 90, y: 42 }), onPreview });

    await waitFor(() => expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('70'));
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('20'));
    fireEvent.change(dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'sampleWidth')!.label }),
      { target: { value: '7' } });
    await waitFor(() => {
      expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('70');
      expect(dialog.getByTestId('line-geometry').getAttribute('data-profile-width')).toBe('7');
    });

    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(dialog.getByText('本次节点测试未完成')).toBeTruthy());
    expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('70');
  });

  it('blocks preview and identifies endpoints when the local search line is too short', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 20, startY: 30, endX: 21, endY: 30 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report });

    expect(dialog.getByText(/搜索线长度至少需要 2 px/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('allows preview when a connected circle replaces malformed local center and radius', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const circleNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 'inactive', centerY: 30, radius: 0 } } };
    const circleSource: Node = { id: 'circle-source', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '上游标称圆' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'circle-image-input', source: imageNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'circle-override', source: circleSource.id, sourceHandle: 'circle', target: circleNode.id, targetHandle: 'circle' }
    ];
    const onPreview = vi.fn(async () => report);
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, circleSource, imageNode], edges, result: report, onPreview });

    expect(dialog.queryByText(/请修正参数后再运行或应用/)).toBeNull();
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect(onPreview).toHaveBeenCalledOnce());
  });

  it('makes caliper geometry read-only when the dialog becomes disabled', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    const view = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report });

    expect(await view.dialog.findByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
    view.rerender(<NodeConfigurationDialog {...view.props} disabled />);
    expect(view.dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();
  });

  it('locks caliper geometry editing while a preview is running', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'line-image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];
    let finishPreview!: (value: RunResult) => void;
    const onPreview = vi.fn(() => new Promise<RunResult>(resolve => { finishPreview = resolve; }));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, imageNode], edges, result: report, onPreview });

    expect(await dialog.findByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
    fireEvent.click(dialog.getByRole('button', { name: '运行此节点' }));
    await waitFor(() => expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true));
    expect(dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();

    finishPreview(report);
    await waitFor(() => expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false));
    expect(dialog.getByRole('button', { name: '模拟拖动搜索线' })).toBeTruthy();
  });

  it('locks a local ROI when the caliper receives an upstream current-image mask', async () => {
    const caliperCatalog = fallbackCatalog.find(item => item.type === 'measure.caliper')!;
    const caliperNode: Node = { id: 'caliper', position: { x: 0, y: 0 }, data: { typeKey: caliperCatalog.type, label: '卡尺测量',
      inputs: caliperCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 2, y: 3, width: 20, height: 15 } } } };
    const roiNode: Node = { id: 'corrected-mask', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI' } };
    const imageNode: Node = { id: 'caliper-image', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'caliper-image-input', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' },
      { id: 'caliper-roi-input', source: roiNode.id, sourceHandle: 'roi', target: caliperNode.id, targetHandle: 'roi' },
      { id: 'roi-debug-image', source: imageNode.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' }
    ];
    const run = { ...report, runId: 'caliper-roi-run', overlays: [
      { id: 'roi-transform-corrected-shape', nodeId: roiNode.id, type: 'Circle' as const, x: 32, y: 30, radius: 10 }
    ] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: caliperNode.id, portName: 'image', width: 100, height: 100 }, { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: caliperNode, catalogItem: caliperCatalog, nodes: [caliperNode, roiNode, imageNode], edges, result: run });

    expect(dialog.getByText('ROI 掩膜来自上游：定位修正 ROI')).toBeTruthy();
    expect(dialog.getByText(/按当前图像坐标解释/)).toBeTruthy();
    expect(dialog.queryByRole('button', { name: '清除测试 ROI' })).toBeNull();
    fireEvent.click(await dialog.findByRole('radio', { name: '输入' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/caliper-image/image'));
    expect(dialog.getByTestId('overlay-ids').textContent).toContain('roi-transform-corrected-shape');
  });
  });
