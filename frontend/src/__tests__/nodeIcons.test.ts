import { describe, expect, it } from 'vitest';
import { iconForNodeType } from '../components/NodeCategoryIcon';
import { fallbackCatalogRaw } from '../catalog';
import { nodeCategory } from '../toolboxModel';

describe('node icon mapping', () => {
  it('distinguishes the image tools that used to share one generic category icon', () => {
    expect(iconForNodeType('image.acquire')).toBe('camera');
    expect(iconForNodeType('image.synthetic')).toBe('sparkles');
    expect(iconForNodeType('image.threshold')).toBe('threshold');
    expect(iconForNodeType('image.crop')).toBe('crop');
    expect(iconForNodeType('image.colorConvert')).toBe('palette');
    expect(iconForNodeType('region.fromRange')).toBe('palette');
  });
  it('uses purpose-specific icons across inspection and workflow steps', () => {
    expect(iconForNodeType('measure.grayHistogram')).toBe('histogram');
    expect(iconForNodeType('measure.caliperLine')).toBe('caliper');
    expect(iconForNodeType('region.skeleton')).toBe('skeleton');
    expect(iconForNodeType('flow.forEach')).toBe('loop');
    expect(iconForNodeType('flow.parallel')).toBe('parallel');
    expect(iconForNodeType('recognition.barcode')).toBe('barcode');
  });
  it('maps every advanced JLVision and defect node to a purpose-specific icon', () => {
    expect(iconForNodeType('jlvision.contourOp')).toBe('contour');
    expect(iconForNodeType('jlvision.dxf')).toBe('file');
    expect(iconForNodeType('jlvision.featureExtract')).toBe('edge');
    expect(iconForNodeType('defect.referenceCompare')).toBe('compare');
    expect(iconForNodeType('jlvision.geometry')).toBe('corner');
    expect(iconForNodeType('jlvision.homMat')).toBe('transform');
    expect(iconForNodeType('jlvision.matrixOp')).toBe('grid');
    expect(iconForNodeType('jlvision.measure1d')).toBe('caliper');
    expect(iconForNodeType('jlvision.contourFeature')).toBe('contour');
    expect(iconForNodeType('jlvision.regionFeature')).toBe('histogram');
    expect(iconForNodeType('jlvision.imageOp')).toBe('sliders');
    expect(iconForNodeType('jlvision.threshold')).toBe('threshold');
    expect(iconForNodeType('jlvision.restoration')).toBe('sharpen');
    expect(iconForNodeType('jlvision.surface')).toBe('surface');
    expect(iconForNodeType('jlvision.regionOp')).toBe('region');
    expect(iconForNodeType('jlvision.regionAnalysis')).toBe('detections');
    expect(iconForNodeType('jlvision.segmentation')).toBe('mask');
    expect(iconForNodeType('jlvision.worldPlane')).toBe('perspective');
  });
  it('never falls back to the plugin icon for a builtin catalog node', () => {
    const pluginFallbacks = fallbackCatalogRaw
      .filter((item) => !item.type.startsWith('plugin.') && iconForNodeType(item.type) === 'puzzle')
      .map((item) => item.type);
    expect(pluginFallbacks).toEqual([]);
  });
  it('gives advanced JLVision and defect nodes a real category presentation', () => {
    for (const type of ['jlvision.worldPlane', 'jlvision.geometry', 'jlvision.measure1d', 'jlvision.imageOp',
      'jlvision.contourOp', 'jlvision.regionOp', 'defect.referenceCompare']) {
      expect(nodeCategory(type)).not.toBe('plugin');
    }
    expect(nodeCategory('jlvision.worldPlane')).toBe('calibration');
    expect(nodeCategory('jlvision.geometry')).toBe('geometry');
    expect(nodeCategory('jlvision.measure1d')).toBe('measurement');
    expect(nodeCategory('jlvision.imageOp')).toBe('preprocess');
    expect(nodeCategory('jlvision.contourOp')).toBe('feature');
    expect(nodeCategory('jlvision.regionOp')).toBe('feature');
    expect(nodeCategory('defect.referenceCompare')).toBe('measurement');
  });
  it('matches node types case-insensitively and retains category fallbacks', () => {
    expect(iconForNodeType('IMAGE.SYNTHETIC')).toBe('sparkles');
    expect(iconForNodeType('camera.vendorCapture')).toBe('camera');
    expect(iconForNodeType('vendor.specialWidget')).toBe('puzzle');
  });
});
