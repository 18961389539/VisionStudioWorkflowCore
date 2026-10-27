import { describe, expect, it } from 'vitest';
import { parseSignature, scriptDocAt, signatureAt } from '../scriptSignature';

describe('script signature help', () => {
  it('tracks the active parameter of the innermost call', () => {
    const text = 'Cv2.CvtColor(Image, gray, ColorConversionCodes.BGR2GRAY);';
    const hint = signatureAt(text, text.indexOf('gray'));
    expect(hint?.name).toBe('Cv2.CvtColor');
    expect(hint?.parameters).toEqual(['Mat src', 'Mat dst', 'ColorConversionCodes code']);
    expect(hint?.active).toBe(1);
  });

  it('ignores commas in strings and nested calls', () => {
    const text = 'Cv2.Threshold(Cv2.CvtColor(Image, gray, ColorConversionCodes.BGR2GRAY), binary, 0';
    const hint = signatureAt(text, text.length);
    expect(hint?.name).toBe('Cv2.Threshold');
    expect(hint?.active).toBe(2);

    const log = 'Log("a, b"';
    expect(signatureAt(log, log.length)?.active).toBe(0);
  });

  it('hides signature help in comments and unknown calls', () => {
    expect(signatureAt('// Cv2.CvtColor(', 16)).toBeUndefined();
    expect(signatureAt('if (true)', 9)).toBeUndefined();
    expect(signatureAt('Foo(', 4)).toBeUndefined();
  });

  it('parses generic parameter commas correctly', () => {
    expect(parseSignature('void Foo(Dictionary<string, int> map, int count)')).toEqual({
      name: 'Foo',
      parameters: ['Dictionary<string, int> map', 'int count']
    });
  });

  it('finds hover docs for dotted symbols', () => {
    const text = 'Cv2.FindContours(binary, out contours, out hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);';
    const hit = scriptDocAt(text, text.indexOf('RetrievalModes.External') + 3);
    expect(hit?.doc.description).toContain('FindContours');
  });
});
