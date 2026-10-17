import type { CaliperDiagnosticManifest, CaliperSpokeProfile } from '../../caliperCircleDiagnostics';
import type { NodeRunReport } from '../../types';

export const diagnosticReport = (sequence = 7): NodeRunReport => ({ nodeId: 'circle', nodeType: 'measure.caliperCircle',
  success: true, durationMs: 5, executionSequence: sequence, summary: { found: true, caliperDiagnosticsAvailable: true } });

export function diagnosticManifest(runId = 'run-a', sequence = 7, count = 4): CaliperDiagnosticManifest {
  return { runId, nodeId: 'circle', executionSequence: sequence, imageWidth: 100, imageHeight: 100,
    center: { x: 50, y: 50 }, nominalRadius: 20, radialBand: 4, minContrast: 8, sampleWidth: 1,
    polarity: 'any', searchDirection: 'strongest', found: true, reason: 'ok',
    spokes: Array.from({ length: count }, (_, index) => {
      const angleDeg = index * 360 / count, radians = angleDeg * Math.PI / 180;
      const point = (radius: number) => ({ x: 50 + Math.cos(radians) * radius, y: 50 + Math.sin(radians) * radius });
      return { index, angleDeg, start: point(16), end: point(24), profileStart: point(16), profileEnd: point(24),
        clipped: false, status: index === 1 ? 'fit-outlier' : 'used', edgePoint: point(20), edgeRadius: 20,
        edgeStrength: 64, sampleCount: 9, validSampleCount: 8, candidateCount: 1 };
    }) };
}

export const diagnosticProfile = (index = 0, runId = 'run-a', sequence = 7): CaliperSpokeProfile => ({
  runId, nodeId: 'circle', executionSequence: sequence, index, startRadius: 16, spacing: 1,
  profile: [30, 30, null, 40, 150, 220, 220, 220, 220], edgeRadius: 20, edgeStrength: 64,
  candidates: [{ position: 4, amplitude: 64, method: 'gaussian' }]
});

export const jsonResponse = (body: unknown, status = 200): Response => ({ ok: status >= 200 && status < 300, status,
  json: async () => body } as Response);

export function deferredResponse() {
  let resolve!: (response: Response) => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<Response>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
