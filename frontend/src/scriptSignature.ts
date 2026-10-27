export type ScriptDoc = { signature?: string; description: string };

export type SignatureHint = {
  name: string;
  parameters: string[];
  active: number;
};

/** 编辑器悬停文档与签名提示共用的静态 API 索引。 */
export const SCRIPT_DOCS: Record<string, ScriptDoc> = {
  Input: { signature: 'ScriptInputs Input', description: '读取 a..h 数据端口。未接线的端口读取会抛 E_INPUT。' },
  'Input.Number': { signature: 'double Input.Number(string name)', description: '读取数值端口；整型端口也可按 double 读取。' },
  'Input.Integer': { signature: 'long Input.Integer(string name)', description: '读取整型端口，要求上游输出 Integer。' },
  'Input.Boolean': { signature: 'bool Input.Boolean(string name)', description: '读取布尔端口。' },
  'Input.Text': { signature: 'string Input.Text(string name)', description: '读取文本端口。' },
  'Input.Numbers': { signature: 'IReadOnlyList<double> Input.Numbers(string name)', description: '读取数值列表端口。' },
  'Input.Points': { signature: 'IReadOnlyList<ScriptPoint> Input.Points(string name)', description: '读取点列表端口，点坐标是 double 像素值。' },
  'Input.Has': { signature: 'bool Input.Has(string name)', description: '端口是否已接线；适合可选输入。' },
  'Input.Value': { signature: 'object? Input.Value(string name)', description: '读取原始值，需要自行判断类型。' },
  Output: { signature: 'ScriptOutput Output', description: '写入节点结果；至少写入一项或 return 标量，否则报 E_OUTPUT。' },
  'Output.Result': { signature: 'object? Output.Result', description: '主结果，支持 double/long/bool/string；也可直接 return。' },
  'Output.Number': { signature: 'double? Output.Number', description: '数值结果，必须是有限值。' },
  'Output.Text': { signature: 'string? Output.Text', description: '文本结果，受 maxOutputBytes 限制。' },
  'Output.Boolean': { signature: 'bool? Output.Boolean', description: '布尔结果。' },
  'Output.Json': { signature: 'string? Output.Json', description: '必须是合法 JSON 字符串；节点会校验并限制长度。' },
  'Output.Image': { signature: 'Mat? Output.Image', description: '图像结果；赋值时立即克隆，可用 using/Dispose 释放工作 Mat。' },
  Image: { signature: 'Mat? Image', description: '可选的图像输入克隆，由脚本执行持有；没有 image 输入时为 null。' },
  Log: { signature: 'void Log(string message)', description: '写入脚本日志，最多 100 条、每条 2000 字符。' },
  Fail: { signature: 'void Fail(string code, string message)', description: '以指定错误码终止脚本，节点报告会显示该错误码。' },
  Mat: { signature: 'Mat(int rows, int cols, MatType type)', description: 'OpenCV 图像。脚本内创建的 Mat 用完应 Dispose；输出图像由 Output.Image 接管。' },
  MatType: { description: '像素类型常量，如 CV_8UC1、CV_8UC3、CV_32FC1。' },
  Point: { signature: 'Point(int x, int y)', description: '整数像素点。' },
  Point2f: { signature: 'Point2f(float x, float y)', description: '浮点像素点，用于亚像素几何。' },
  Rect: { signature: 'Rect(int x, int y, int width, int height)', description: '矩形区域，原点在左上角。' },
  Size: { signature: 'Size(int width, int height)', description: '宽高尺寸。' },
  Scalar: { signature: 'Scalar(double v0, double v1 = 0, double v2 = 0, double v3 = 0)', description: '四通道标量/颜色；也提供 Scalar.All、Scalar.Red 等快捷值。' },
  ColorConversionCodes: { description: 'Cv2.CvtColor 的颜色转换码，如 BGR2GRAY、GRAY2BGR、BGR2HSV。' },
  ThresholdTypes: { description: '阈值类型：Binary、BinaryInv、Otsu、Triangle 等，可与 Otsu 组合。' },
  MorphTypes: { description: '形态学操作：Erode、Dilate、Open、Close、Gradient 等。' },
  RetrievalModes: { description: 'FindContours 的检索模式：External 只取外轮廓，List/Tree 取层级。' },
  ContourApproximationModes: { description: 'FindContours 的逼近模式；ApproxSimple 只保留拐点。' },
  TemplateMatchModes: { description: 'MatchTemplate 的匹配模式；CCoeffNormed 常用于归一化相关匹配。' },
  'Cv2.CvtColor': { signature: 'Cv2.CvtColor(Mat src, Mat dst, ColorConversionCodes code)', description: '颜色空间转换；目标 Mat 必须与源尺寸匹配并按需创建。' },
  'Cv2.Threshold': { signature: 'double Cv2.Threshold(Mat src, Mat dst, double thresh, double maxval, ThresholdTypes type)', description: '固定/自动阈值；Otsu 时 thresh 可传 0，返回实际阈值。' },
  'Cv2.GaussianBlur': { signature: 'Cv2.GaussianBlur(Mat src, Mat dst, Size ksize, double sigmaX, double sigmaY = 0)', description: '高斯滤波；ksize 必须是正奇数。' },
  'Cv2.MedianBlur': { signature: 'Cv2.MedianBlur(Mat src, Mat dst, int ksize)', description: '中值滤波；ksize 必须是大于 1 的奇数。' },
  'Cv2.FindContours': { signature: 'Cv2.FindContours(Mat image, out Point[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes mode, ContourApproximationModes method)', description: '提取轮廓；输入通常是 8 位单通道二值图。' },
  'Cv2.ContourArea': { signature: 'double Cv2.ContourArea(IEnumerable<Point> contour)', description: '计算轮廓面积。' },
  'Cv2.BoundingRect': { signature: 'Rect Cv2.BoundingRect(IEnumerable<Point> points)', description: '计算点集的正外接矩形。' },
  'Cv2.MinMaxLoc': { signature: 'Cv2.MinMaxLoc(Mat src, out double minVal, out double maxVal, out Point minLoc, out Point maxLoc)', description: '定位极值；模板匹配后用它取最佳位置。' },
  'Cv2.MatchTemplate': { signature: 'Cv2.MatchTemplate(Mat image, Mat templ, Mat result, TemplateMatchModes method)', description: '模板匹配；result 尺寸为 (W-w+1, H-h+1)。' },
  'Cv2.Resize': { signature: 'Cv2.Resize(Mat src, Mat dst, Size dsize, double fx = 0, double fy = 0, InterpolationFlags interpolation = Linear)', description: '缩放图像；dsize 为零时按 fx/fy 计算。' },
  'Cv2.WarpAffine': { signature: 'Cv2.WarpAffine(Mat src, Mat dst, Mat m, Size dsize, InterpolationFlags flags = Linear)', description: '仿射变换；可用 GetRotationMatrix2D 生成矩阵。' },
  'Cv2.SolvePnP': { signature: 'Cv2.SolvePnP(objectPoints, imagePoints, cameraMatrix, distCoeffs, rvec, tvec)', description: '求解位姿；需要先准备相机内参与畸变参数。' },
  'Cv2.Kmeans': { signature: 'double Cv2.Kmeans(samples, clusterCount, labels, criteria, attempts, flags, centers)', description: 'K-means 聚类；返回紧凑度，labels/centers 由输出参数返回。' },
  'Cv2.ConnectedComponentsWithStats': { signature: 'int Cv2.ConnectedComponentsWithStats(Mat image, Mat labels, Mat stats, Mat centroids, PixelConnectivity connectivity = 8)', description: '连通域标记与统计；stats 每行包含面积和外接框。' },
  'Cv2.ApproxPolyDP': { signature: 'Point[] Cv2.ApproxPolyDP(IEnumerable<Point> curve, double epsilon, bool closed)', description: '多边形逼近；epsilon 越大保留的顶点越少。' }
};

function nameBefore(text: string, open: number): string | undefined {
  let index = open - 1;
  while (index >= 0 && /\s/.test(text[index])) index--;
  const end = index + 1;
  while (index >= 0 && /[A-Za-z0-9_.]/.test(text[index])) index--;
  const name = text.slice(index + 1, end);
  return name || undefined;
}

function splitParameters(text: string): string[] {
  const parameters: string[] = [];
  let current = '';
  let depth = 0;
  for (const char of text) {
    if (char === '<' || char === '(' || char === '[') depth++;
    else if (char === '>' || char === ')' || char === ']') depth = Math.max(0, depth - 1);
    if (char === ',' && depth === 0) {
      parameters.push(current.trim());
      current = '';
    } else {
      current += char;
    }
  }
  if (current.trim()) parameters.push(current.trim());
  return parameters;
}

export function parseSignature(signature: string): { name: string; parameters: string[] } | undefined {
  const open = signature.indexOf('(');
  const close = signature.lastIndexOf(')');
  if (open < 0 || close < open) return undefined;
  const name = signature.slice(0, open).trim().split(/\s+/).pop();
  if (!name) return undefined;
  return { name, parameters: splitParameters(signature.slice(open + 1, close)) };
}

type ScanState = 'normal' | 'string' | 'char' | 'verbatim' | 'lineComment' | 'blockComment';

/**
 * Finds the innermost open call before the cursor. Strings and comments are skipped so a
 * comma inside "a, b" or a parenthesis in a comment does not change the active parameter.
 */
function scanTo(text: string, position: number): { frames: Array<{ open: number; commas: number }>; state: ScanState } {
  const frames: Array<{ open: number; commas: number }> = [];
  let state: ScanState = 'normal';
  for (let index = 0; index < position; index++) {
    const char = text[index];
    const next = text[index + 1];
    if (state === 'lineComment') {
      if (char === '\n') state = 'normal';
      continue;
    }
    if (state === 'blockComment') {
      if (char === '*' && next === '/') { state = 'normal'; index++; }
      continue;
    }
    if (state === 'string') {
      if (char === '\\') { index++; continue; }
      if (char === '"') state = 'normal';
      continue;
    }
    if (state === 'char') {
      if (char === '\\') { index++; continue; }
      if (char === '\'') state = 'normal';
      continue;
    }
    if (state === 'verbatim') {
      if (char === '"' && next === '"') { index++; continue; }
      if (char === '"') state = 'normal';
      continue;
    }
    if (char === '/' && next === '/') { state = 'lineComment'; index++; continue; }
    if (char === '/' && next === '*') { state = 'blockComment'; index++; continue; }
    if (char === '@' && next === '"') { state = 'verbatim'; index++; continue; }
    if (char === '"') { state = 'string'; continue; }
    if (char === '\'') { state = 'char'; continue; }
    if (char === '(') { frames.push({ open: index, commas: 0 }); continue; }
    if (char === ')') { frames.pop(); continue; }
    if (char === ',' && frames.length > 0) frames[frames.length - 1].commas++;
  }
  return { frames, state };
}

export function signatureAt(text: string, position: number, docs: Record<string, ScriptDoc> = SCRIPT_DOCS): SignatureHint | undefined {
  const scan = scanTo(text, Math.max(0, Math.min(position, text.length)));
  if (scan.state === 'lineComment' || scan.state === 'blockComment') return undefined;
  const frame = scan.frames[scan.frames.length - 1];
  if (!frame) return undefined;
  const name = nameBefore(text, frame.open);
  if (!name) return undefined;
  const doc = docs[name];
  if (!doc?.signature) return undefined;
  const parsed = parseSignature(doc.signature);
  if (!parsed) return undefined;
  return {
    name: parsed.name,
    parameters: parsed.parameters,
    active: parsed.parameters.length === 0 ? -1 : Math.min(frame.commas, parsed.parameters.length - 1)
  };
}

export function scriptDocAt(text: string, position: number): { doc: ScriptDoc; from: number; to: number } | undefined {
  const left = text.slice(0, position).match(/[A-Za-z_][A-Za-z0-9_.]*$/)?.[0] ?? '';
  const right = text.slice(position).match(/^[A-Za-z0-9_.]*/)?.[0] ?? '';
  const word = `${left}${right}`.replace(/^\.+|\.+$/g, '');
  if (!word) return undefined;
  const candidates = [word, word.split('.').pop() ?? word, word.split('.')[0]];
  for (const candidate of candidates) {
    const doc = SCRIPT_DOCS[candidate];
    if (doc) return { doc, from: position - left.length, to: position + right.length };
  }
  return undefined;
}
