import { describe, expect, it } from 'vitest';
import { iconForNodeType } from '../components/NodeCategoryIcon';

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
  it('matches node types case-insensitively and retains category fallbacks', () => {
    expect(iconForNodeType('IMAGE.SYNTHETIC')).toBe('sparkles');
    expect(iconForNodeType('camera.vendorCapture')).toBe('camera');
    expect(iconForNodeType('vendor.specialWidget')).toBe('puzzle');
  });
});
