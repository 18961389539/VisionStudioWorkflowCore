/**
 * 卡尺几何拟合与输入引导域（原 describe 上半段） —— 从 nodeConfigurationDialog.test.tsx 拆分（2026-10-23，测试并行化）。
 */
import './nodeConfigurationDialogHarness';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { Node } from '@xyflow/react';
import NodeConfigurationDialog from '../components/NodeConfigurationDialog';
import type { NodeCatalogItem, RunResult, VisionOverlay, VisionRoi } from '../types';
import { fallbackCatalog } from '../catalog';
import { catalog, node, report, mount } from './nodeConfigurationDialogHarness';

describe('caliper geometry fitting', () => {
  it.each([
    ['measure.caliper', 'line', 'direction', 'data-direction'],
    ['measure.caliperLine', 'line', 'direction', 'data-direction'],
    ['measure.caliperCircle', 'circle', 'searchDirection', 'data-search-direction']
  ] as const)('passes the configured search order to the %s canvas preview', async (type, geometryType, parameter, attribute) => {
    const item = fallbackCatalog.find(candidate => candidate.type === type)!;
    const parameters = type === 'measure.caliperCircle'
      ? { [parameter]: 'firstfromend', centerX: 50, centerY: 50, radius: 20 }
      : { [parameter]: 'firstfromend', startX: 10, startY: 20, endX: 80, endY: 20 };
    const caliperNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: {
      typeKey: type, label: item.displayName, inputs: item.inputs, parameters
    } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: caliperNode, catalogItem: item, nodes: [caliperNode, imageNode], edges, result: report });

    const geometry = await dialog.findByTestId(geometryType === 'line' ? 'line-geometry' : 'circle-geometry');
    expect(geometry.getAttribute(attribute)).toBe('firstfromend');
  });

  it.each(['measure.caliper', 'measure.caliperLine', 'measure.caliperArray'])('fits the %s search line into the loaded image', async (type) => {
    const item = fallbackCatalog.find(candidate => candidate.type === type)!;
    const caliperNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: {
      typeKey: type, label: item.displayName, inputs: item.inputs,
      parameters: ['measure.caliperLine', 'measure.caliperArray'].includes(type) ? { edgeLength: 200 } : {}
    } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: caliperNode, catalogItem: item, nodes: [caliperNode, imageNode], edges, result: report });

    const geometry = await dialog.findByTestId('line-geometry');
    expect(geometry.getAttribute('data-start-x')).toBe('120');
    expect(geometry.getAttribute('data-end-x')).toBe('520');
    fireEvent.click(dialog.getByRole('button', { name: '适配几何到图像' }));
    await waitFor(() => {
      expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('18.75');
      expect(dialog.getByTestId('line-geometry').getAttribute('data-start-y')).toBe('50');
      expect(dialog.getByTestId('line-geometry').getAttribute('data-end-x')).toBe('81.25');
      expect(dialog.getByTestId('line-geometry').getAttribute('data-end-y')).toBe('50');
      if (['measure.caliperLine', 'measure.caliperArray'].includes(type)) expect(dialog.getByTestId('line-geometry').getAttribute('data-profile-span')).toBe('62.5');
    });
    fireEvent.click(dialog.getByRole('button', { name: '撤销几何适配' }));
    await waitFor(() => {
      expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('120');
      expect(dialog.getByTestId('line-geometry').getAttribute('data-start-y')).toBe('240');
      expect(dialog.getByTestId('line-geometry').getAttribute('data-end-x')).toBe('520');
      expect(dialog.getByTestId('line-geometry').getAttribute('data-end-y')).toBe('240');
      if (['measure.caliperLine', 'measure.caliperArray'].includes(type)) expect(dialog.getByTestId('line-geometry').getAttribute('data-profile-span')).toBe('200');
    });
  });

  it('centers the nominal circle, limits an oversized search band, and preserves its arc', async () => {
    const item = fallbackCatalog.find(candidate => candidate.type === 'measure.caliperCircle')!;
    const caliperNode: Node = { id: 'test', position: { x: 0, y: 0 }, data: {
      typeKey: item.type, label: item.displayName, inputs: item.inputs, parameters: { radialBand: 60 }
    } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [{ id: 'caliper-image', source: imageNode.id, sourceHandle: 'image', target: caliperNode.id, targetHandle: 'image' }];
    const { dialog } = mount({ node: caliperNode, catalogItem: item, nodes: [caliperNode, imageNode], edges, result: report });

    const geometry = await dialog.findByTestId('circle-geometry');
    expect(geometry.getAttribute('data-center-x')).toBe('320');
    expect(geometry.getAttribute('data-radius')).toBe('100');
    expect(geometry.getAttribute('data-band')).toBe('60');
    fireEvent.click(dialog.getByRole('button', { name: '适配几何到图像' }));
    await waitFor(() => {
      const fitted = dialog.getByTestId('circle-geometry');
      expect(fitted.getAttribute('data-center-x')).toBe('50');
      expect(fitted.getAttribute('data-center-y')).toBe('50');
      expect(Number(fitted.getAttribute('data-radius'))).toBeCloseTo(100 / 4.8, 4);
      expect(fitted.getAttribute('data-band')).toBe('15');
      expect(fitted.getAttribute('data-angle-start')).toBe('-180');
      expect(fitted.getAttribute('data-angle-end')).toBe('180');
    });
    fireEvent.click(dialog.getByRole('button', { name: '撤销几何适配' }));
    await waitFor(() => {
      const restored = dialog.getByTestId('circle-geometry');
      expect(restored.getAttribute('data-center-x')).toBe('320');
      expect(restored.getAttribute('data-center-y')).toBe('240');
      expect(restored.getAttribute('data-radius')).toBe('100');
      expect(restored.getAttribute('data-band')).toBe('60');
    });
  });
});


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

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle']
  ])('blocks a connected current-image %s from being transformed a second time', (geometryLabel, sourceType, targetType, outputPort, inputPort) => {
    const sourceCatalog = fallbackCatalog.find(item => item.type === sourceType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceType, label: `当前图像${geometryLabel}`, inputs: sourceCatalog.inputs, parameters: {}
    } };
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const transformNode: Node = { id: 'transform', position: { x: 0, y: 0 }, data: {
      typeKey: 'roi.transform', label: '定位修正 ROI', parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 }
    } };
    const imageNode: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      { id: 'geometry-image', source: imageNode.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: imageNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'transform-image', source: imageNode.id, sourceHandle: 'image', target: transformNode.id, targetHandle: 'debugImage' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, transformNode, imageNode], edges });

    expect(dialog.getByText('上游几何已处于变换目标坐标系')).toBeTruthy();
    expect(dialog.getByText(/断开 transform，或改接基准图像坐标/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('allows a connected search line measured in a separate reference image to follow the ROI transform', () => {
    const sourceCatalog = fallbackCatalog.find(item => item.type === 'feature.line')!;
    const targetCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const geometryNode: Node = { id: 'reference-line', position: { x: 0, y: 0 }, data: {
      typeKey: sourceCatalog.type, label: '基准图像直线', inputs: sourceCatalog.inputs, parameters: {}
    } };
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetCatalog.type, label: '卡尺找线', inputs: targetCatalog.inputs, parameters: {}
    } };
    const transformNode: Node = { id: 'transform', position: { x: 0, y: 0 }, data: {
      typeKey: 'roi.transform', label: '定位修正 ROI', parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 }
    } };
    const referenceImage: Node = { id: 'reference-image', position: { x: 0, y: 0 }, data: { label: '基准图像' } };
    const currentImage: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      { id: 'geometry-image', source: referenceImage.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: currentImage.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'transform-image', source: currentImage.id, sourceHandle: 'image', target: transformNode.id, targetHandle: 'debugImage' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: 'line', target: measurementNode.id, targetHandle: 'searchLine' },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, transformNode, referenceImage, currentImage], edges });

    expect(dialog.queryByText('上游几何已处于变换目标坐标系')).toBeNull();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it('blocks a search line from the cropped image when the crop transform is connected too', () => {
    const sourceCatalog = fallbackCatalog.find(item => item.type === 'feature.line')!;
    const targetCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceCatalog.type, label: '裁剪图像直线', inputs: sourceCatalog.inputs, parameters: {}
    } };
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetCatalog.type, label: '卡尺找线', inputs: targetCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs, parameters: {}
    } };
    const sourceImage: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '输入图像' } };
    const edges = [
      { id: 'crop-image', source: sourceImage.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'geometry-image', source: cropNode.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'measurement-image', source: cropNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'geometry-input', source: geometryNode.id, sourceHandle: 'line', target: measurementNode.id, targetHandle: 'searchLine' },
      { id: 'transform-input', source: cropNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, cropNode, sourceImage], edges });

    expect(dialog.getByText('上游几何已处于变换目标坐标系')).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle']
  ])('blocks a connected %s from a different crop frame when no transform is connected', (geometryLabel, sourceType, targetType, outputPort, inputPort) => {
    const { dialog } = mountCropFrameMeasurement(sourceType, targetType, outputPort, inputPort, true);

    expect(dialog.getByText(`上游${geometryLabel === 'circle' ? '圆' : '搜索线'}与测量图像坐标不匹配`)).toBeTruthy();
    expect(dialog.getByText(/将.*和图像接到同一裁剪\/变换分支/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine', '上游搜索线与测量图像坐标不匹配'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle', '上游圆与测量图像坐标不匹配']
  ])('blocks connected %s geometry from before an affine image warp', (_geometryLabel, sourceType, targetType, outputPort, inputPort, mismatchTitle) => {
    const geometryCatalog = fallbackCatalog.find(item => item.type === sourceType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const warpCatalog = fallbackCatalog.find(item => item.type === 'image.warpAffine')!;
    const transformCatalog = fallbackCatalog.find(item => item.type === 'coordinate.transformConstant')!;
    const geometryNode: Node = { id: 'geometry', position: { x: 0, y: 0 }, data: {
      typeKey: sourceType, label: '变换前几何', inputs: geometryCatalog.inputs, parameters: {}
    } };
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const warpNode: Node = { id: 'warp', position: { x: 0, y: 0 }, data: {
      typeKey: warpCatalog.type, label: '仿射变换图像', inputs: warpCatalog.inputs, parameters: {}
    } };
    const transformNode: Node = { id: 'transform', position: { x: 0, y: 0 }, data: {
      typeKey: transformCatalog.type, label: '像素仿射变换', inputs: transformCatalog.inputs,
      parameters: { sourceFrame: 'px', targetFrame: 'px', unit: 'px', x: 10, y: 0, angleDeg: 0 }
    } };
    const sourceImage: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '变换前图像' } };
    const edges = [
      { id: 'geometry-image', source: sourceImage.id, sourceHandle: 'image', target: geometryNode.id, targetHandle: 'image' },
      { id: 'warp-image', source: sourceImage.id, sourceHandle: 'image', target: warpNode.id, targetHandle: 'image' },
      { id: 'warp-transform', source: transformNode.id, sourceHandle: 'transform', target: warpNode.id, targetHandle: 'transform' },
      { id: 'measurement-image', source: warpNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'measurement-geometry', source: geometryNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, geometryNode, warpNode, transformNode, sourceImage], edges });

    expect(dialog.getByText(mismatchTitle)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine', '上游搜索线与测量图像坐标不匹配'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle', '上游圆与测量图像坐标不匹配']
  ])('allows connected %s geometry and measurement images from the same crop frame without a transform', (_geometryLabel, sourceType, targetType, outputPort, inputPort, mismatchTitle) => {
    const { dialog } = mountCropFrameMeasurement(sourceType, targetType, outputPort, inputPort, false);

    expect(dialog.queryByText(mismatchTitle)).toBeNull();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle']
  ])('blocks a connected %s from the wrong source frame of a crop transform', (geometryLabel, sourceType, targetType, outputPort, inputPort) => {
    const { dialog } = mountCropTransformSourceMeasurement(sourceType, targetType, outputPort, inputPort, true);

    expect(dialog.getByText(`上游${geometryLabel === 'circle' ? '圆' : '搜索线'}与 transform 源坐标系不匹配`)).toBeTruthy();
    expect(dialog.getByText(/来自 transform 源图像/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it.each([
    ['search line', 'feature.line', 'measure.caliperLine', 'line', 'searchLine'],
    ['circle', 'feature.circle', 'measure.caliperCircle', 'circle', 'circle']
  ])('allows a connected %s from the crop transform source frame', (_geometryLabel, sourceType, targetType, outputPort, inputPort) => {
    const { dialog } = mountCropTransformSourceMeasurement(sourceType, targetType, outputPort, inputPort, false);

    expect(dialog.queryByText(/与 transform 源坐标系不匹配/)).toBeNull();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(false);
  });

  it.each([
    ['fitted line', 'measure.fitLine', 'measure.caliperLine', 'line', 'searchLine'],
    ['fitted circle', 'measure.fitCircle', 'measure.caliperCircle', 'circle', 'circle']
  ])('traces %s through connected Point2D measurements before allowing a transform', (geometryLabel, fitType, targetType, outputPort, inputPort) => {
    const pointCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const fitCatalog = fallbackCatalog.find(item => item.type === fitType)!;
    const targetCatalog = fallbackCatalog.find(item => item.type === targetType)!;
    const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
      typeKey: targetType, label: '最终卡尺测量', inputs: targetCatalog.inputs, parameters: {}
    } };
    const pointNodes: Node[] = [1, 2, 3].map(index => ({ id: `point-source-${index}`, position: { x: 0, y: 0 }, data: {
      typeKey: pointCatalog.type, label: `上游卡尺测点 ${index}`, inputs: pointCatalog.inputs, parameters: {}
    } }));
    const fitNode: Node = { id: 'fit-geometry', position: { x: 0, y: 0 }, data: {
      typeKey: fitType, label: `多点拟合${geometryLabel}`, inputs: fitCatalog.inputs, parameters: {}
    } };
    const transformNode: Node = { id: 'transform', position: { x: 0, y: 0 }, data: {
      typeKey: 'roi.transform', label: '定位修正 ROI', parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 }
    } };
    const imageNode: Node = { id: 'current-image', position: { x: 0, y: 0 }, data: { label: '当前图像' } };
    const edges = [
      ...pointNodes.flatMap((pointNode, index) => [
        { id: `point-image-${index}`, source: imageNode.id, sourceHandle: 'image', target: pointNode.id, targetHandle: 'image' },
        { id: `fit-point-${index}`, source: pointNode.id, sourceHandle: 'point', target: fitNode.id, targetHandle: `point${index + 1}` }
      ]),
      { id: 'measurement-image', source: imageNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' },
      { id: 'transform-image', source: imageNode.id, sourceHandle: 'image', target: transformNode.id, targetHandle: 'debugImage' },
      { id: 'fit-geometry', source: fitNode.id, sourceHandle: outputPort, target: measurementNode.id, targetHandle: inputPort },
      { id: 'transform-input', source: transformNode.id, sourceHandle: 'transform', target: measurementNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: measurementNode, catalogItem: targetCatalog,
      nodes: [measurementNode, ...pointNodes, fitNode, transformNode, imageNode], edges });

    expect(dialog.getByText('上游几何已处于变换目标坐标系')).toBeTruthy();
    expect(dialog.getByText(/断开 transform，或改接基准图像坐标/)).toBeTruthy();
    expect((dialog.getByRole('button', { name: '运行此节点' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('edits transformed search geometry over the saved ROI reference image', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const roiNode: Node = { id: 'pose-correction', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 } } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'reference-image', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'pose-transform', source: roiNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, roiNode, imageNode], edges, result: report });

    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,reference-png');
    expect(dialog.getByText(/当前显示的已保存基准图像可直接拖动或重绘/)).toBeTruthy();
    fireEvent.click(await dialog.findByRole('button', { name: '模拟拖动搜索线' }));
    await waitFor(() => expect((dialog.getByRole('spinbutton', { name: lineCatalog.parameters.find(parameter => parameter.name === 'startX')!.label }) as HTMLInputElement).value).toBe('23'));
  });

  it('shows a connected search line in its saved reference frame when transform is connected', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const roiNode: Node = { id: 'pose-correction', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { referenceImage: 'reference-png', referenceWidth: 100, referenceHeight: 100 } } };
    const lineSource: Node = { id: 'line-source', position: { x: 0, y: 0 }, data: { typeKey: 'measure.fitLine', label: '上游拟合线' } };
    const imageNode: Node = { id: 'other', position: { x: 0, y: 0 }, data: { label: '场景图像' } };
    const edges = [
      { id: 'image-input', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'search-line', source: lineSource.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' },
      { id: 'pose-transform', source: roiNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' }
    ];
    const lineResult: RunResult = { ...report, nodeReports: [{ nodeId: lineNode.id, nodeType: lineCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: {
        searchLine: { type: 'Line2D', display: '[(14, 22), (88, 46)]',
          value: { start: { x: 14, y: 22 }, end: { x: 88, y: 46 } } },
        transform: { type: 'Transform2D', display: 'translation', value: { sourceUnit: 'px', targetUnit: 'px',
          h00: 1, h01: 0, h02: 50, h10: 0, h11: 1, h12: 10, h20: 0, h21: 0, h22: 1 } }
      } }] };
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, roiNode, lineSource, imageNode], edges, result: lineResult });

    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,reference-png');
    expect(dialog.getByText(/上游基准搜索线/)).toBeTruthy();
    const geometry = await dialog.findByTestId('line-geometry');
    expect(geometry.getAttribute('data-start-x')).toBe('14');
    expect(geometry.getAttribute('data-end-y')).toBe('46');
    expect(geometry.getAttribute('data-editable')).toBe('false');
    expect(dialog.queryByRole('button', { name: '模拟拖动搜索线' })).toBeNull();

    fireEvent.click(dialog.getByRole('radio', { name: '当前测量图像' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/other/image'));
    const mappedGeometry = dialog.getByTestId('line-geometry');
    expect(mappedGeometry.getAttribute('data-start-x')).toBe('64');
    expect(mappedGeometry.getAttribute('data-start-y')).toBe('32');
    expect(mappedGeometry.getAttribute('data-end-x')).toBe('138');
    expect(mappedGeometry.getAttribute('data-end-y')).toBe('56');
    expect(mappedGeometry.getAttribute('data-editable')).toBe('false');
  });

  it('shows a connected search line on the crop input image when crop transform is connected', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Rectangle', x: 20, y: 20, width: 50, height: 50 } } } };
    const lineSource: Node = { id: 'line-source', position: { x: 0, y: 0 }, data: { typeKey: 'measure.fitLine', label: '上游拟合线' } };
    const imageNode: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '裁剪前图像' } };
    const edges = [
      { id: 'crop-image-input', source: imageNode.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'line-image-input', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'line-search-input', source: lineSource.id, sourceHandle: 'line', target: lineNode.id, targetHandle: 'searchLine' },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' }
    ];
    const lineResult: RunResult = { ...report, nodeReports: [{ nodeId: lineNode.id, nodeType: lineCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { searchLine: { type: 'Line2D', display: '[(24, 27), (86, 39)]',
        value: { start: { x: 24, y: 27 }, end: { x: 86, y: 39 } } } } }] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: lineNode.id, portName: 'image', width: 50, height: 50 }, { nodeId: cropNode.id, portName: 'image', width: 50, height: 50 },
      { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, cropNode, lineSource, imageNode], edges, result: lineResult });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source-image/image'));
    expect(dialog.getByText(/上游基准搜索线/)).toBeTruthy();
    const geometry = await dialog.findByTestId('line-geometry');
    expect(geometry.getAttribute('data-start-x')).toBe('24');
    expect(geometry.getAttribute('data-end-y')).toBe('39');
    expect(geometry.getAttribute('data-editable')).toBe('false');
  });

  it('switches a crop-transformed caliper between its editable reference and mapped current image', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const lineNode: Node = { id: 'line-measure', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs,
      parameters: { startX: 30, startY: 35, endX: 80, endY: 35 } } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Rectangle', x: 20, y: 20, width: 50, height: 50 } } } };
    const imageNode: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '裁剪前图像' } };
    const edges = [
      { id: 'crop-image-input', source: imageNode.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'line-image-input', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' }
    ];
    const lineResult: RunResult = { ...report, nodeReports: [{ nodeId: lineNode.id, nodeType: lineCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { transform: { type: 'Transform2D', display: 'crop translation', value: { sourceUnit: 'px', targetUnit: 'px',
        h00: 1, h01: 0, h02: -20, h10: 0, h11: 1, h12: -20, h20: 0, h21: 0, h22: 1 } } } }] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: lineNode.id, portName: 'image', width: 50, height: 50 },
      { nodeId: cropNode.id, portName: 'image', width: 50, height: 50 },
      { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, cropNode, imageNode], edges, result: lineResult });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source-image/image'));
    expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('30');
    expect(dialog.getByTestId('line-geometry').getAttribute('data-editable')).toBe('true');

    fireEvent.click(dialog.getByRole('radio', { name: '当前测量图像' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/crop/image'));
    expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('10');
    expect(dialog.getByTestId('line-geometry').getAttribute('data-start-y')).toBe('15');
    expect(dialog.getByTestId('line-geometry').getAttribute('data-end-x')).toBe('60');
    expect(dialog.getByTestId('line-geometry').getAttribute('data-editable')).toBe('false');
    expect(dialog.getByText(/请切回基准图像拖动或重绘卡尺几何/)).toBeTruthy();

    fireEvent.click(dialog.getByRole('radio', { name: '基准图像' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source-image/image'));
    expect(dialog.getByTestId('line-geometry').getAttribute('data-start-x')).toBe('30');
    expect(dialog.getByTestId('line-geometry').getAttribute('data-editable')).toBe('true');
  });

  it('switches a crop-transformed circle caliper to the mapped read-only current image', async () => {
    const circleCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperCircle')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const circleNode: Node = { id: 'circle-measure', position: { x: 0, y: 0 }, data: { typeKey: circleCatalog.type, label: '卡尺找圆', inputs: circleCatalog.inputs,
      parameters: { centerX: 30, centerY: 35, radius: 12, radialBand: 6, angleStartDeg: -45, angleEndDeg: 45 } } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: cropCatalog.type, label: '图像裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Rectangle', x: 20, y: 20, width: 50, height: 50 } } } };
    const imageNode: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '裁剪前图像' } };
    const edges = [
      { id: 'crop-image-input', source: imageNode.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'circle-image-input', source: cropNode.id, sourceHandle: 'image', target: circleNode.id, targetHandle: 'image' },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: circleNode.id, targetHandle: 'transform' }
    ];
    const circleResult: RunResult = { ...report, nodeReports: [{ nodeId: circleNode.id, nodeType: circleCatalog.type, success: true,
      durationMs: 1, summary: {}, inputs: { transform: { type: 'Transform2D', display: 'crop translation', value: { sourceUnit: 'px', targetUnit: 'px',
        h00: 1, h01: 0, h02: -20, h10: 0, h11: 1, h12: -20, h20: 0, h21: 0, h22: 1 } } } }] };
    vi.stubGlobal('fetch', vi.fn(async () => ({ ok: true, json: async () => ({ images: [
      { nodeId: circleNode.id, portName: 'image', width: 50, height: 50 },
      { nodeId: cropNode.id, portName: 'image', width: 50, height: 50 },
      { nodeId: imageNode.id, portName: 'image', width: 100, height: 100 }
    ] }) })));
    const { dialog } = mount({ node: circleNode, catalogItem: circleCatalog, nodes: [circleNode, cropNode, imageNode], edges, result: circleResult });

    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/source-image/image'));
    expect(dialog.getByTestId('circle-geometry').getAttribute('data-center-x')).toBe('30');
    expect(dialog.getByTestId('circle-geometry').getAttribute('data-radius')).toBe('12');
    expect(dialog.getByTestId('circle-geometry').getAttribute('data-editable')).toBe('true');

    fireEvent.click(dialog.getByRole('radio', { name: '当前测量图像' }));
    await waitFor(() => expect(dialog.getByTestId('image-url').textContent).toContain('/nodes/crop/image'));
    const mappedCircle = dialog.getByTestId('circle-geometry');
    expect(mappedCircle.getAttribute('data-center-x')).toBe('10');
    expect(mappedCircle.getAttribute('data-center-y')).toBe('15');
    expect(mappedCircle.getAttribute('data-radius')).toBe('12');
    expect(mappedCircle.getAttribute('data-angle-start')).toBe('-45');
    expect(mappedCircle.getAttribute('data-angle-end')).toBe('45');
    expect(mappedCircle.getAttribute('data-editable')).toBe('false');
  });

  it('uses the ROI reference image to edit measurement geometry through a composed ROI-and-crop transform', async () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: { typeKey: lineCatalog.type, label: '卡尺找线',
      inputs: lineCatalog.inputs, parameters: { startX: 10, startY: 20, endX: 80, endY: 20 } } };
    const roiNode: Node = { id: 'pose-correction', position: { x: 0, y: 0 }, data: { typeKey: 'roi.transform', label: '定位修正',
      parameters: { referenceImage: 'saved-png', referenceWidth: 100, referenceHeight: 100 } } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: { typeKey: cropCatalog.type, label: '图像裁剪',
      inputs: cropCatalog.inputs, parameters: { roi: { type: 'Rectangle', x: 10, y: 10, width: 70, height: 70 } } } };
    const composeNode: Node = { id: 'compose', position: { x: 0, y: 0 }, data: { typeKey: 'coordinate.transformCompose', label: '组合变换' } };
    const imageNode: Node = { id: 'source-image', position: { x: 0, y: 0 }, data: { label: '基准输入图像' } };
    const edges = [
      { id: 'crop-image-input', source: imageNode.id, sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'roi-transform', source: roiNode.id, sourceHandle: 'transform', target: composeNode.id, targetHandle: 'transformA' },
      { id: 'crop-transform', source: cropNode.id, sourceHandle: 'transform', target: composeNode.id, targetHandle: 'transformB' },
      { id: 'composed-transform', source: composeNode.id, sourceHandle: 'transform', target: lineNode.id, targetHandle: 'transform' },
      { id: 'cropped-image', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, roiNode, cropNode, composeNode, imageNode], edges });

    expect(await dialog.findByTestId('line-geometry')).toBeTruthy();
    expect(dialog.getByTestId('image-url').textContent).toBe('data:image/png;base64,saved-png');
    expect(dialog.getByTestId('line-geometry').getAttribute('data-editable')).toBe('true');
  });

  it.each(['feature.edgeSubpixel', 'measure.caliper', 'measure.caliperLine', 'measure.caliperCircle'] as const)(
    'warns %s when a cropped image has no matching croppedRoi mask', async type => {
      const measurementCatalog = fallbackCatalog.find(item => item.type === type)!;
      const measurementNode: Node = { id: 'measurement', position: { x: 0, y: 0 }, data: {
        typeKey: type, label: measurementCatalog.displayName, inputs: measurementCatalog.inputs, parameters: {}
      } };
      const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
      const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
        typeKey: cropCatalog.type, label: '预处理裁剪', inputs: cropCatalog.inputs,
        parameters: { roi: { type: 'Polygon', points: [{ x: 10, y: 10 }, { x: 50, y: 10 }, { x: 30, y: 50 }] } }
      } };
      const edges = [
        { id: 'crop-image', source: cropNode.id, sourceHandle: 'image', target: measurementNode.id, targetHandle: 'image' }
      ];
      const { dialog, unmount } = mount({ node: measurementNode, catalogItem: measurementCatalog, nodes: [measurementNode, cropNode], edges });

      expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
      expect(dialog.getByText(/选区外像素会清零/)).toBeTruthy();
      unmount();
      cleanup();

      const maskedEdges = [...edges, {
        id: 'crop-roi', source: cropNode.id, sourceHandle: 'croppedRoi', target: measurementNode.id, targetHandle: 'roi'
      }];
      const masked = mount({ node: measurementNode, catalogItem: measurementCatalog, nodes: [measurementNode, cropNode], edges: maskedEdges });
      expect(masked.dialog.queryByText('上游裁剪可能形成伪边缘')).toBeNull();
    }
  );

  it('traces crop-mask guidance through pixel-coordinate-preserving image processing', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const grayscaleCatalog = fallbackCatalog.find(item => item.type === 'image.channel')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '非矩形裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Polygon', points: [{ x: 10, y: 10 }, { x: 50, y: 10 }, { x: 30, y: 50 }] } }
    } };
    const grayscaleNode: Node = { id: 'grayscale', position: { x: 0, y: 0 }, data: {
      typeKey: grayscaleCatalog.type, label: '灰度转换', inputs: grayscaleCatalog.inputs, parameters: {}
    } };
    const nodes = [lineNode, cropNode, grayscaleNode];
    const edges = [
      { id: 'crop-gray', source: cropNode.id, sourceHandle: 'image', target: grayscaleNode.id, targetHandle: 'image' },
      { id: 'gray-line', source: grayscaleNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const view = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges });

    expect(view.dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
    expect(view.dialog.getByText(/“非矩形裁剪”的 croppedRoi 输出/)).toBeTruthy();
    expect(view.dialog.getByText(/灰度转换等不改变坐标的处理可以放在两者之间/)).toBeTruthy();
    expect(view.dialog.getByText(/模糊等邻域滤波后请避开 ROI 边界测量/)).toBeTruthy();
    view.unmount();
    cleanup();

    const masked = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges: [...edges,
      { id: 'crop-mask', source: cropNode.id, sourceHandle: 'croppedRoi', target: lineNode.id, targetHandle: 'roi' }] });
    expect(masked.dialog.queryByText('上游裁剪可能形成伪边缘')).toBeNull();
  });

  it.each(['feature.line', 'feature.circle', 'measure.blobs', 'region.fromRoi'])(
    'keeps crop-mask guidance through %s image outputs', (imageNodeType) => {
      const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
      const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
      const imageNodeCatalog = fallbackCatalog.find(item => item.type === imageNodeType)!;
      const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
        typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
      } };
      const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
        typeKey: cropCatalog.type, label: '非矩形裁剪', inputs: cropCatalog.inputs,
        parameters: { roi: { type: 'Polygon', points: [{ x: 10, y: 10 }, { x: 50, y: 10 }, { x: 30, y: 50 }] } }
      } };
      const imageNode: Node = { id: 'image-pass', position: { x: 0, y: 0 }, data: {
        typeKey: imageNodeCatalog.type, label: imageNodeCatalog.displayName, inputs: imageNodeCatalog.inputs, parameters: {}
      } };
      const nodes = [lineNode, cropNode, imageNode];
      const edges = [
        { id: 'crop-pass', source: cropNode.id, sourceHandle: 'image', target: imageNode.id, targetHandle: 'image' },
        { id: 'pass-line', source: imageNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
      ];
      const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges });

      expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
      expect(dialog.getByText(/“非矩形裁剪”的 croppedRoi 输出/)).toBeTruthy();
    }
  );

  it('traces crop-mask guidance through the ROI transform debug-image input', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const roiCatalog = fallbackCatalog.find(item => item.type === 'roi.transform')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '非矩形裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Circle', x: 40, y: 40, radius: 20 } }
    } };
    const roiNode: Node = { id: 'roi', position: { x: 0, y: 0 }, data: {
      typeKey: roiCatalog.type, label: '定位修正 ROI', inputs: roiCatalog.inputs, parameters: {}
    } };
    const edges = [
      { id: 'crop-roi', source: cropNode.id, sourceHandle: 'image', target: roiNode.id, targetHandle: 'debugImage' },
      { id: 'roi-line', source: roiNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, cropNode, roiNode], edges });

    expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
    expect(dialog.getByText(/“非矩形裁剪”的 croppedRoi 输出/)).toBeTruthy();
  });

  it('keeps crop-mask guidance when a geometric image transform changes the pixel frame', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const resizeCatalog = fallbackCatalog.find(item => item.type === 'image.resize')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '非矩形裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Circle', x: 30, y: 30, radius: 20 } }
    } };
    const resizeNode: Node = { id: 'resize', position: { x: 0, y: 0 }, data: {
      typeKey: resizeCatalog.type, label: '缩放图像', inputs: resizeCatalog.inputs, parameters: {}
    } };
    const nodes = [lineNode, cropNode, resizeNode];
    const edges = [
      { id: 'crop-resize', source: cropNode.id, sourceHandle: 'image', target: resizeNode.id, targetHandle: 'image' },
      { id: 'resize-line', source: resizeNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges });

    expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
    expect(dialog.getByText(/多次裁剪或坐标变换后，单个 croppedRoi 可能无法直接覆盖当前图像/)).toBeTruthy();
  });

  it('does not warn about synthetic ROI edges for a rectangular crop', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '矩形裁剪', inputs: cropCatalog.inputs,
      parameters: { roi: { type: 'Rectangle', x: 10, y: 10, width: 50, height: 40 } }
    } };
    const edges = [{ id: 'crop-image', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }];

    const { dialog } = mount({ node: lineNode, catalogItem: lineCatalog, nodes: [lineNode, cropNode], edges });

    expect(dialog.queryByText('上游裁剪可能形成伪边缘')).toBeNull();
  });

  it('warns when a pose-corrected rectangular ROI can rotate into a masked polygon during cropping', () => {
    const lineCatalog = fallbackCatalog.find(item => item.type === 'measure.caliperLine')!;
    const cropCatalog = fallbackCatalog.find(item => item.type === 'image.crop')!;
    const lineNode: Node = { id: 'line', position: { x: 0, y: 0 }, data: {
      typeKey: lineCatalog.type, label: '卡尺找线', inputs: lineCatalog.inputs, parameters: {}
    } };
    const cropNode: Node = { id: 'crop', position: { x: 0, y: 0 }, data: {
      typeKey: cropCatalog.type, label: '定位区域裁剪', inputs: cropCatalog.inputs,
      // This local rectangle is dormant because the connected corrected ROI takes precedence.
      parameters: { roi: { type: 'Rectangle', x: 0, y: 0, width: 80, height: 80 } }
    } };
    const roiNode: Node = { id: 'corrected-roi', position: { x: 0, y: 0 }, data: {
      typeKey: 'roi.transform', label: '定位修正 ROI',
      parameters: { roi: { type: 'Rectangle', x: 20, y: 20, width: 30, height: 20 } }
    } };
    const edges = [
      { id: 'crop-image', source: 'image-source', sourceHandle: 'image', target: cropNode.id, targetHandle: 'image' },
      { id: 'corrected-roi', source: roiNode.id, sourceHandle: 'roi', target: cropNode.id, targetHandle: 'roi' },
      { id: 'measurement-image', source: cropNode.id, sourceHandle: 'image', target: lineNode.id, targetHandle: 'image' }
    ];
    const imageNode: Node = { id: 'image-source', position: { x: 0, y: 0 }, data: { label: '输入图像' } };
    const nodes = [lineNode, cropNode, roiNode, imageNode];
    const { dialog, unmount } = mount({ node: lineNode, catalogItem: lineCatalog, nodes, edges });

    expect(dialog.getByText('上游裁剪可能形成伪边缘')).toBeTruthy();
    expect(dialog.getByText(/选区外像素会清零/)).toBeTruthy();
    unmount();
    cleanup();

    const masked = mount({ node: lineNode, catalogItem: lineCatalog, nodes,
      edges: [...edges, { id: 'crop-mask', source: cropNode.id, sourceHandle: 'croppedRoi', target: lineNode.id, targetHandle: 'roi' }] });
    expect(masked.dialog.queryByText('上游裁剪可能形成伪边缘')).toBeNull();
  });

  });
