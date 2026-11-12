/**
 * Helpers shared by the planar (`CalibrationHandEyeWizard`) and SE(3) (`CalibrationHandEye3DWizard`)
 * wizards. Both files carried their own copy of the download helper and the CSV row splitter, and the
 * planar wizard additionally duplicated the angular-coverage / translation-span prechecks.
 */

/** Circular coverage of a set of angles, i.e. 360 minus the largest gap. */
export const angularCoverage = (anglesDeg: number[]) => {
  if (anglesDeg.length < 2) return 0;
  const sorted = anglesDeg.map((a) => ((a % 360) + 360) % 360).sort((a, b) => a - b);
  let largestGap = sorted[0] + 360 - sorted[sorted.length - 1];
  for (let i = 1; i < sorted.length; i += 1) largestGap = Math.max(largestGap, sorted[i] - sorted[i - 1]);
  return 360 - largestGap;
};

/** Diagonal of the axis-aligned bounding box of the given points. */
export const translationSpan = (points: Array<{ x: number; y: number }>) => {
  if (points.length === 0) return 0;
  const xs = points.map((p) => p.x);
  const ys = points.map((p) => p.y);
  return Math.hypot(Math.max(...xs) - Math.min(...xs), Math.max(...ys) - Math.min(...ys));
};

export const download = (filename: string, text: string, type: string) => {
  const url = URL.createObjectURL(new Blob([text], { type }));
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.click();
  URL.revokeObjectURL(url);
};

/**
 * Split comma-separated numeric rows into fixed-width chunks.
 * `headerPattern` lets the first line be a textual header (robotX,... / robot0,...) instead of data.
 * `describe` is appended to the error message so 6-column (planar) and 32-column (SE(3)) failures stay
 * self-explanatory.
 */
export const splitNumericRows = (text: string, columns: number, headerPattern: RegExp, describe = ''): number[][] => {
  const lines = text.split(/\r?\n/).map((line) => line.trim()).filter((line) => line.length > 0);
  const rows: number[][] = [];
  for (const line of lines) {
    const cells = line.split(',').map((cell) => cell.trim());
    if (cells.length !== columns || cells.some((cell) => cell === '' || Number.isNaN(Number(cell)))) {
      if (rows.length === 0 && headerPattern.test(line)) continue;
      throw new Error(`CSV 行无效（需 ${columns} 列数值${describe}）：${line.slice(0, 40)}`);
    }
    rows.push(cells.map(Number));
  }
  return rows;
};
