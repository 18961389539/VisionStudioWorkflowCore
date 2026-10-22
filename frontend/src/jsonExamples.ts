/** 仅用于空/占位 JSON 字段的入门示例；插入后由用户替换为现场数据。 */
export const jsonExamples: Readonly<Record<string, string>> = {
  'measure.metrology.objects': JSON.stringify([
    { type: 'circle', name: 'hole', row: 240, column: 320, radius: 80, measureLength1: 20, measureLength2: 5 },
    { type: 'line', name: 'topEdge', rowBegin: 120, columnBegin: 80, rowEnd: 120, columnEnd: 560 }
  ], null, 2),
  'calibration.planar.pairsJson': JSON.stringify([
    { imageX: 200, imageY: 150, worldX: 0, worldY: 0 },
    { imageX: 600, imageY: 150, worldX: 50, worldY: 0 },
    { imageX: 1000, imageY: 150, worldX: 100, worldY: 0 },
    { imageX: 200, imageY: 450, worldX: 0, worldY: 50 },
    { imageX: 600, imageY: 450, worldX: 50, worldY: 50 },
    { imageX: 1000, imageY: 450, worldX: 100, worldY: 50 },
    { imageX: 200, imageY: 750, worldX: 0, worldY: 100 },
    { imageX: 600, imageY: 750, worldX: 50, worldY: 100 },
    { imageX: 1000, imageY: 750, worldX: 100, worldY: 100 }
  ], null, 2),
  'calibration.handEye2D.samplesJson': JSON.stringify({
    samples: [
      { robot: { x: 0, y: 0, thetaDeg: 0 }, vision: { x: 0, y: 0, thetaDeg: 0 } },
      { robot: { x: 100, y: 0, thetaDeg: 10 }, vision: { x: 10, y: 5, thetaDeg: 8 } },
      { robot: { x: 0, y: 100, thetaDeg: -10 }, vision: { x: -5, y: 12, thetaDeg: -9 } },
      { robot: { x: 100, y: 100, thetaDeg: 20 }, vision: { x: 8, y: 18, thetaDeg: 17 } }
    ]
  }, null, 2),
  'calibration.cameraIntrinsics.calibrationDataJson': JSON.stringify({
    imageWidth: 1280,
    imageHeight: 960,
    views: [
      { objectPoints: [[0, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0]], imagePoints: [[400, 300], [500, 300], [500, 400], [400, 400]] },
      { objectPoints: [[0, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0]], imagePoints: [[380, 320], [520, 310], [530, 430], [390, 440]] },
      { objectPoints: [[0, 0, 0], [10, 0, 0], [10, 10, 0], [0, 10, 0]], imagePoints: [[420, 280], [540, 290], [550, 410], [410, 430]] }
    ]
  }, null, 2)
};

const extraJsonParameters = new Set([
  'measure.metrology.objects',
  'jlvision.worldPlane.cameraParameters',
  'jlvision.worldPlane.worldPoseJson'
]);

export function isJsonParameter(type: string, parameter: string): boolean {
  return /json$/i.test(parameter) || extraJsonParameters.has(`${type}.${parameter}`);
}

export function jsonExampleFor(type: string, parameter: string): string | undefined {
  return jsonExamples[`${type}.${parameter}`];
}
