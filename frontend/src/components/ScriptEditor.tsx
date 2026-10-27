import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Select, Space, Tag, Tooltip } from 'antd';
import CodeMirror from '@uiw/react-codemirror';
import { autocompletion, startCompletion, type CompletionContext, type CompletionResult } from '@codemirror/autocomplete';
import { StreamLanguage } from '@codemirror/language';
import { csharp } from '@codemirror/legacy-modes/mode/clike';
import { linter, type Diagnostic } from '@codemirror/lint';
import { oneDark } from '@codemirror/theme-one-dark';
import { EditorView, hoverTooltip, keymap } from '@codemirror/view';
import { apiJson, isAbortError } from '../apiClient';
import { scripts } from '../endpoints';
import { scriptDocAt, signatureAt, type SignatureHint } from '../scriptSignature';
import { useThemeMode } from '../theme';

export type ScriptDiagnostic = {
  line: number;
  column: number;
  severity: string;
  code: string;
  message: string;
};

type ScriptCompletion = {
  label: string;
  kind: string;
  detail: string;
  insertText: string;
};

type ValidationResponse = {
  valid: boolean;
  diagnostics: ScriptDiagnostic[];
};

type FormatResponse = ValidationResponse & { formatted: string };

export type ScriptPreviewValue = {
  type: string;
  display: string;
  value?: unknown;
};

/** 节点测试结果中与 script.csharp 相关的精简视图，由配置弹窗从 RunResult 提取。 */
export type ScriptPreviewReport = {
  success: boolean;
  error?: string | null;
  durationMs: number;
  summary: Record<string, unknown>;
  inputs?: Record<string, ScriptPreviewValue> | null;
  outputs?: Record<string, ScriptPreviewValue> | null;
  imageUrl?: string;
  receivedAt?: string;
  stale?: boolean;
  draft?: boolean;
};

type Props = {
  value: string;
  disabled?: boolean;
  connectedInputPorts?: readonly string[];
  /** 使用暂存参数运行当前节点；由节点配置弹窗提供。 */
  onRunPreview?: () => void;
  previewBusy?: boolean;
  preview?: ScriptPreviewReport;
  /** 配置弹窗判定当前不能测试时的原因。 */
  previewBlockedReason?: string;
  onChange: (value: string) => void;
};

/** 预览面板只展示脚本实际写出的标量结果；diagnostics 是字符串化端口，放进表格只会干扰阅读。 */
const PREVIEW_OUTPUT_PORTS = new Set(['result', 'number', 'text', 'boolean', 'json']);

function previewNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function previewText(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function previewLogs(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((line): line is string => typeof line === 'string') : [];
}

function previewValueText(item: ScriptPreviewValue): string {
  if (item.display) return item.display;
  if (item.value === null || item.value === undefined) return '—';
  return typeof item.value === 'object' ? JSON.stringify(item.value) : String(item.value);
}

/** 运行时错误里的原始行号：generic 异常用 "at line N"，ScriptUserException 用 "(line N)"。 */
function runtimeErrorLine(error?: string | null): number | undefined {
  const match = error?.match(/\bline (\d+)\b/i);
  return match ? Number(match[1]) : undefined;
}

const LOCAL_COMPLETIONS: ScriptCompletion[] = [
  { label: 'Input', kind: 'Variable', detail: '类型化输入', insertText: 'Input' },
  { label: 'Output', kind: 'Variable', detail: '类型化输出', insertText: 'Output' },
  { label: 'Image', kind: 'Variable', detail: '输入 Mat 或 null', insertText: 'Image' },
  { label: 'Log', kind: 'Method', detail: 'void Log(string message)', insertText: 'Log(' },
  { label: 'Fail', kind: 'Method', detail: 'void Fail(string code, string message)', insertText: 'Fail(' },
  { label: 'Mat', kind: 'Type', detail: 'OpenCvSharp.Mat', insertText: 'Mat' },
  { label: 'Cv2', kind: 'Type', detail: 'OpenCvSharp.Cv2', insertText: 'Cv2' },
  { label: 'for', kind: 'Keyword', detail: '循环', insertText: 'for (var i = 0; i < ; i++) {\n    \n}' },
  { label: 'if', kind: 'Keyword', detail: '条件', insertText: 'if () {\n    \n}' }
];

const SNIPPETS = [
  { label: '数值输出', value: 'number', code: 'Output.Number = Input.Number("a") * 2;' },
  { label: '灰度转换', value: 'gray', code: 'var gray = new Mat();\nCv2.CvtColor(Image, gray, ColorConversionCodes.BGR2GRAY);\nOutput.Image = gray;\ngray.Dispose();' },
  { label: '阈值', value: 'threshold', code: 'var binary = new Mat();\nCv2.Threshold(Image, binary, 128, 255, ThresholdTypes.Binary);\nOutput.Image = binary;\nbinary.Dispose();' },
  {
    label: '异常处理', value: 'try',
    code: 'try {\n    \n} catch (OpenCvSharpException ex) {\n    Fail("E_CV", ex.Message);\n}'
  },
  { label: 'JSON 输出', value: 'json', code: 'Output.Json = "{\\"ok\\":true}";' },
  {
    label: '轮廓统计', value: 'contours',
    code: 'var gray = new Mat();\nCv2.CvtColor(Image, gray, ColorConversionCodes.BGR2GRAY);\nvar binary = new Mat();\nCv2.Threshold(gray, binary, 0, 255, ThresholdTypes.Otsu);\nCv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);\nvar areas = contours.Select(item => Cv2.ContourArea(item)).OrderByDescending(area => area).ToArray();\nOutput.Json = "{\\"count\\":" + contours.Length + ",\\"largest\\":" + (areas.Length > 0 ? areas[0] : 0) + "}";\ngray.Dispose();\nbinary.Dispose();'
  }
];

const API_GROUPS: Array<{ title: string; items: Array<[string, string]> }> = [
  {
    title: '脚本全局',
    items: [
      ['Input.Number("a")', '读取数值输入'],
      ['Input.Text("a")', '读取文本输入'],
      ['Input.Numbers("a")', '读取数值列表'],
      ['Input.Points("a")', '读取点列表'],
      ['Output.Result = ', '写入主结果'],
      ['Output.Number = ', '写入数值结果'],
      ['Output.Text = ', '写入文本结果'],
      ['Output.Boolean = ', '写入布尔结果'],
      ['Output.Json = ', '写入 JSON 结果'],
      ['Output.Image = ', '写入图像结果'],
      ['Log("...")', '写入脚本日志'],
      ['Fail("E_CODE", "...")', '终止脚本']
    ]
  },
  {
    title: '常用类型',
    items: [
      ['Mat', 'OpenCV 图像'],
      ['MatType', '像素类型 CV_8UC1 等'],
      ['Scalar', '颜色/标量'],
      ['Point', '整数点'],
      ['Point2f', '浮点像素点'],
      ['Rect', '矩形'],
      ['Size', '尺寸'],
      ['Cv2', 'OpenCV 静态方法']
    ]
  },
  {
    title: '常量',
    items: [
      ['ColorConversionCodes.BGR2GRAY', '彩色转灰度'],
      ['ThresholdTypes.Otsu', '自动阈值'],
      ['MorphTypes.Open', '开运算'],
      ['MatType.CV_8UC1', '8 位单通道'],
      ['RetrievalModes.External', '只取外轮廓'],
      ['ContourApproximationModes.ApproxSimple', '简化轮廓点'],
      ['TemplateMatchModes.CCoeffNormed', '归一化模板匹配'],
      ['NormTypes.INF', '无穷范数'],
      ['InterpolationFlags.Linear', '双线性插值'],
      ['BorderTypes.Constant', '常量边界'],
      ['Scalar.Red', '红色标量']
    ]
  },
  {
    title: '允许的 Cv2',
    items: [
      ['Cv2.CvtColor(', '颜色空间转换'],
      ['Cv2.Mean(', '均值'],
      ['Cv2.Threshold(', '固定/自动阈值'],
      ['Cv2.GaussianBlur(', '高斯滤波'],
      ['Cv2.FindContours(', '轮廓提取'],
      ['Cv2.ContourArea(', '轮廓面积'],
      ['Cv2.ApproxPolyDP(', '多边形逼近'],
      ['Cv2.ConnectedComponentsWithStats(', '连通域统计'],
      ['Cv2.Kmeans(', 'K-means 聚类'],
      ['Cv2.MinMaxLoc(', '极值定位'],
      ['Cv2.MatchTemplate(', '模板匹配'],
      ['Cv2.SolvePnP(', '位姿求解'],
      ['Cv2.ImDecode(', '字节解码'],
      ['Cv2.ImEncode(', '编码为字节']
    ]
  }
];

const scriptHoverExtension = hoverTooltip((view, position) => {
  const line = view.state.doc.lineAt(position);
  const hit = scriptDocAt(line.text, position - line.from);
  if (!hit) return null;
  return {
    pos: line.from + hit.from,
    end: line.from + hit.to,
    above: true,
    create: () => {
      const dom = document.createElement('div');
      dom.className = 'script-editor-hover-doc';
      if (hit.doc.signature) {
        const signature = document.createElement('code');
        signature.textContent = hit.doc.signature;
        dom.append(signature);
      }
      const description = document.createElement('p');
      description.textContent = hit.doc.description;
      dom.append(description);
      return { dom };
    }
  };
});

export default function ScriptEditor({ value, disabled, connectedInputPorts, onRunPreview, previewBusy, preview, previewBlockedReason, onChange }: Props) {
  const { mode } = useThemeMode();
  const viewRef = useRef<EditorView | null>(null);
  const cacheRef = useRef(new Map<string, ScriptCompletion[]>());
  const [diagnostics, setDiagnostics] = useState<ScriptDiagnostic[]>([]);
  const [valid, setValid] = useState(true);
  // 校验结果对应的源码：输入后、防抖请求返回前不能再显示上一次的“通过”。
  const [validatedSource, setValidatedSource] = useState<string>();
  const [validating, setValidating] = useState(false);
  const [formatting, setFormatting] = useState(false);
  const [formatError, setFormatError] = useState<string>();
  const [serverError, setServerError] = useState<string>();
  const [fullscreen, setFullscreen] = useState(false);
  const [showApi, setShowApi] = useState(false);
  const [snippet, setSnippet] = useState<string>();
  const [previewOpen, setPreviewOpen] = useState(true);
  const [signature, setSignature] = useState<SignatureHint>();

  const ports = useMemo(
    () => (connectedInputPorts ?? []).filter(port => /^[a-h]$/i.test(port)).map(port => port.toLowerCase()),
    [connectedInputPorts]
  );
  const previewSummary = preview?.summary ?? {};
  const compileMs = previewNumber(previewSummary.compileMs);
  const executeMs = previewNumber(previewSummary.executeMs);
  const ticks = previewNumber(previewSummary.ticks);
  const sourceHash = previewText(previewSummary.sourceHash);
  const rawOutputImage = previewText(previewSummary.outputImage);
  const outputImageSize = rawOutputImage === 'none' ? undefined : rawOutputImage;
  const logs = previewLogs(previewSummary.logs);
  const inputEntries = Object.entries(preview?.inputs ?? {});
  const outputEntries = Object.entries(preview?.outputs ?? {}).filter(([name]) => PREVIEW_OUTPUT_PORTS.has(name.toLowerCase()));
  const errorCount = diagnostics.filter(item => item.severity.toLowerCase() === 'error').length;
  const pendingValidation = validatedSource !== value;
  const errorLine = runtimeErrorLine(preview?.error);
  // 全屏时给预览面板留出固定高度；否则固定 100vh-210px 的编辑器会把面板裁出视口。
  const editorHeight = fullscreen ? (preview ? 'calc(100vh - 370px)' : 'calc(100vh - 210px)') : '260px';

  // 每次新结果（或请求失败）自动展开预览，避免用户只看到变化的标签却看不到内容。
  useEffect(() => {
    if (preview?.receivedAt || preview?.error) setPreviewOpen(true);
  }, [preview?.receivedAt, preview?.error]);

  const validate = useCallback(async (source: string, signal?: AbortSignal) => {
    setValidating(true);
    try {
      const result = await apiJson<ValidationResponse>(scripts.validate, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ source }),
        signal,
        timeoutMs: 5000,
        errorLabel: '脚本校验失败'
      });
      setDiagnostics(result.diagnostics ?? []);
      setValid(result.valid);
      setValidatedSource(source);
      setServerError(undefined);
    } catch (error) {
      if (isAbortError(error)) return;
      setServerError('脚本校验服务不可用；仍可保存并运行，但错误会在执行时返回。');
    } finally {
      setValidating(false);
    }
  }, []);

  const formatSource = useCallback(async () => {
    if (disabled || formatting) return;
    const view = viewRef.current;
    const source = view ? view.state.doc.toString() : value;
    if (!source.trim()) return;
    setFormatting(true);
    setFormatError(undefined);
    try {
      const result = await apiJson<FormatResponse>(scripts.format, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ source }),
        timeoutMs: 5000,
        errorLabel: '脚本格式化失败'
      });
      if (!result.valid) {
        setDiagnostics(result.diagnostics ?? []);
        setValid(false);
        setValidatedSource(source);
        setFormatError('脚本存在语法错误，已保留原内容。');
        return;
      }
      if (!view || result.formatted === source) return;
      const anchor = Math.min(view.state.selection.main.head, result.formatted.length);
      view.dispatch({
        changes: { from: 0, to: view.state.doc.length, insert: result.formatted },
        selection: { anchor }
      });
      setDiagnostics([]);
      setValidatedSource(undefined);
      view.focus();
    } catch (error) {
      if (isAbortError(error)) return;
      setFormatError('格式化服务不可用，请稍后重试。');
    } finally {
      setFormatting(false);
    }
  }, [disabled, formatting, value]);
  const formatRef = useRef(formatSource);
  formatRef.current = formatSource;

  useEffect(() => {
    // 源码变化后旧的格式化失败提示已过期。
    setFormatError(undefined);
    if (disabled) return;
    const controller = new AbortController();
    const timer = window.setTimeout(() => void validate(value, controller.signal), 500);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [disabled, validate, value]);

  useEffect(() => {
    if (!fullscreen) return;
    const close = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      event.stopPropagation();
      setFullscreen(false);
    };
    window.addEventListener('keydown', close, true);
    return () => window.removeEventListener('keydown', close, true);
  }, [fullscreen]);

  const insertText = useCallback((text: string, cursorOffset = text.length) => {
    const view = viewRef.current;
    if (!view || disabled) return;
    const { from, to } = view.state.selection.main;
    view.dispatch({
      changes: { from, to, insert: text },
      selection: { anchor: from + Math.max(0, Math.min(cursorOffset, text.length)) }
    });
    view.focus();
  }, [disabled]);

  const completionSource = useCallback(async (context: CompletionContext): Promise<CompletionResult | null> => {
    const portContext = context.matchBefore(/Input\.(?:Number|Integer|Boolean|Text|Numbers|Points)\("[^"]*$/);
    if (portContext) {
      const quote = portContext.text.lastIndexOf('"');
      const partial = portContext.text.slice(quote + 1);
      const from = context.pos - partial.length;
      const source = context.state.doc.toString();
      const key = `port:${partial}|${ports.join(',')}`;
      let items = cacheRef.current.get(key);
      if (!items) {
        try {
          items = await apiJson<ScriptCompletion[]>(scripts.complete, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ source, offset: context.pos, ports }),
            timeoutMs: 2500
          });
          cacheRef.current.set(key, items);
        } catch {
          items = (ports.length ? ports : ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'])
            .filter(port => port.startsWith(partial))
            .map(port => ({ label: port, kind: 'Port', detail: '脚本输入', insertText: port }));
        }
      }
      return {
        from,
        options: items.map(item => ({
          label: item.label,
          detail: item.detail,
          type: item.kind.toLowerCase(),
          apply: (view, _completion, applyFrom, applyTo) => {
            view.dispatch({
              changes: { from: applyFrom, to: applyTo, insert: item.insertText },
              selection: { anchor: applyFrom + item.insertText.length }
            });
          }
        }))
      };
    }

    const match = context.matchBefore(/[\w.]*/);
    if (!match || (match.from === match.to && !context.explicit)) return null;
    const source = context.state.doc.toString();
    const afterDot = match.text.includes('.') ? match.from + match.text.lastIndexOf('.') + 1 : match.from;
    // 不在前端缓存这里的成员补全：var gray = new Mat() 等声明会改变 gray. 的结果，
    // 而缓存键只有 match.text，会在后续输入中复用过期候选。
    let items: ScriptCompletion[];
    try {
      items = await apiJson<ScriptCompletion[]>(scripts.complete, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ source, offset: context.pos, ports }),
        timeoutMs: 2500
      });
    } catch {
      const token = match.text.split('.').pop() ?? match.text;
      items = LOCAL_COMPLETIONS.filter(item => item.label.toLowerCase().startsWith(token.toLowerCase()));
    }
    return {
      from: afterDot,
      options: items.map(item => ({
        label: item.label,
        detail: item.detail,
        type: item.kind.toLowerCase(),
        apply: (view, _completion, applyFrom, applyTo) => {
          const insert = item.insertText;
          const cursor = insert.endsWith('""') ? insert.length - 1 : insert.length;
          view.dispatch({
            changes: { from: applyFrom, to: applyTo, insert },
            selection: { anchor: applyFrom + cursor }
          });
        }
      }))
    };
  }, [ports]);

  const lintExtension = useMemo(() => linter((view): Diagnostic[] => {
    const doc = view.state.doc;
    return diagnostics.map(item => {
      const lineNumber = Math.max(1, Math.min(item.line, doc.lines));
      const line = doc.line(lineNumber);
      const from = Math.min(line.to, line.from + Math.max(0, item.column - 1));
      const to = Math.min(line.to, Math.max(from + 1, from));
      return {
        from,
        to,
        severity: item.severity.toLowerCase() === 'warning' ? 'warning' : 'error',
        message: `${item.code}: ${item.message}`
      };
    });
  }), [diagnostics]);

  const signatureListener = useMemo(() => EditorView.updateListener.of((update) => {
    if (!update.selectionSet && !update.docChanged) return;
    const head = update.state.selection.main.head;
    setSignature(signatureAt(update.state.doc.toString(), head));
  }), []);

  const extensions = useMemo(() => [
    // legacy-modes 与 language 各自打包了 streamparser 类型，运行时对象兼容；仅类型声明不同。
    StreamLanguage.define(csharp as never),
    EditorView.lineWrapping,
    autocompletion({ override: [completionSource], activateOnTyping: true }),
    lintExtension,
    scriptHoverExtension,
    signatureListener,
    // Ctrl+Enter 交给节点配置弹窗的全局快捷键，避免编辑器内再绑一次导致同一次按键
    // 既校验又触发节点测试（keymap 只 preventDefault，不阻断冒泡）。
    keymap.of([
      { key: 'Mod-Space', run: startCompletion },
      { key: 'Shift-Alt-f', run: () => { void formatRef.current(); return true; } }
    ])
  ], [completionSource, lintExtension, signatureListener]);

  const jumpTo = (item: ScriptDiagnostic) => {
    const view = viewRef.current;
    if (!view) return;
    const lineNumber = Math.max(1, Math.min(item.line, view.state.doc.lines));
    const line = view.state.doc.line(lineNumber);
    const anchor = Math.min(line.to, line.from + Math.max(0, item.column - 1));
    view.dispatch({ selection: { anchor }, scrollIntoView: true });
    view.focus();
  };

  const jumpToLine = (lineNumber: number) => {
    const view = viewRef.current;
    if (!view) return;
    const line = view.state.doc.line(Math.max(1, Math.min(lineNumber, view.state.doc.lines)));
    view.dispatch({ selection: { anchor: line.from }, scrollIntoView: true });
    view.focus();
  };

  return (
    <div className={'script-editor' + (fullscreen ? ' is-fullscreen' : '')} aria-label="C# 脚本编辑器">
      <div className="script-editor-toolbar">
        <Space size={6} wrap>
          <Tag color={serverError ? 'orange' : pendingValidation ? 'default' : valid ? 'green' : 'red'}>
            {serverError ? '校验不可用' : pendingValidation ? '待校验' : validating ? '校验中' : valid ? '通过' : `${errorCount} 个错误`}
          </Tag>
          {onRunPreview && <Tooltip title={previewBlockedReason ?? '用暂存参数运行当前节点，结果显示在下方（Ctrl+Enter）'}>
            <Button size="small" type="primary" loading={previewBusy}
              disabled={disabled || previewBusy || Boolean(previewBlockedReason)}
              onClick={onRunPreview}>运行预览</Button>
          </Tooltip>}
          <Button size="small" disabled={disabled || validating} onClick={() => void validate(value)}>校验</Button>
          <Tooltip title="整理缩进与换行（Shift+Alt+F）">
            <Button size="small" loading={formatting} disabled={disabled || formatting}
              onClick={() => void formatSource()}>格式化</Button>
          </Tooltip>
          <Select size="small" className="script-editor-snippet-select" placeholder="插入片段" value={snippet}
            disabled={disabled} options={SNIPPETS.map(item => ({ label: item.label, value: item.value }))}
            onChange={(next) => {
              const item = SNIPPETS.find(candidate => candidate.value === next);
              if (item) insertText(item.code, item.code.length);
              setSnippet(undefined);
            }} />
          <Tooltip title="显示可用脚本 API">
            <Button size="small" onClick={() => setShowApi(current => !current)}>{showApi ? '隐藏 API' : 'API'}</Button>
          </Tooltip>
          <Tooltip title={fullscreen ? '退出全屏（Esc）' : '全屏编辑'}>
            <Button size="small" onClick={() => setFullscreen(current => !current)}>{fullscreen ? '退出全屏' : '全屏'}</Button>
          </Tooltip>
        </Space>
      </div>
      <div className={'script-editor-main' + (showApi ? ' with-api' : '')}>
        <div className="script-editor-code">
          <CodeMirror
            value={value}
            height={editorHeight}
            theme={mode === 'dark' ? oneDark : 'light'}
            editable={!disabled}
            extensions={extensions}
            basicSetup={{
              lineNumbers: true,
              foldGutter: true,
              highlightActiveLine: true,
              highlightActiveLineGutter: true,
              bracketMatching: true,
              closeBrackets: true,
              autocompletion: false
            }}
            onCreateEditor={(view) => {
              viewRef.current = view;
              setSignature(signatureAt(view.state.doc.toString(), view.state.selection.main.head));
            }}
            onChange={(next) => onChange(next)}
          />
          {signature && <div className="script-editor-signature" aria-label="当前函数签名">
            <code>{signature.name}(</code>
            {signature.parameters.map((parameter, index) => <span key={`${parameter}-${index}`}
              className={index === signature.active ? 'is-active' : undefined}>
              {parameter}{index < signature.parameters.length - 1 ? ', ' : ''}
            </span>)}
            <code>)</code>
          </div>}
          {serverError && <Alert type="warning" showIcon title={serverError} className="script-editor-alert" />}
          {formatError && <Alert type="warning" showIcon title={formatError} className="script-editor-alert" />}
          {diagnostics.length > 0 && <div className="script-editor-diagnostics">
            {diagnostics.map((item, index) => (
              <button key={`${item.code}-${item.line}-${item.column}-${index}`} type="button"
                className={'script-editor-diagnostic ' + item.severity.toLowerCase()}
                onClick={() => jumpTo(item)}>
                <span>{item.line}:{item.column}</span>
                <span>{item.code}</span>
                <span>{item.message}</span>
              </button>
            ))}
          </div>}
        </div>
        {showApi && <aside className="script-editor-api">
          {API_GROUPS.map(group => <details key={group.title} open>
            <summary>{group.title}</summary>
            {group.items.map(([text, detail]) => <button key={text} type="button" disabled={disabled}
              onClick={() => insertText(text)} title={detail}>
              <code>{text}</code><span>{detail}</span>
            </button>)}
          </details>)}
        </aside>}
      </div>
      {preview && <section className="script-editor-preview" aria-label="脚本运行预览">
        <button type="button" className="script-editor-preview-heading" aria-expanded={previewOpen}
          onClick={() => setPreviewOpen(current => !current)}>
          <span aria-hidden="true">{previewOpen ? '▾' : '▸'}</span>
          <strong>运行预览</strong>
          {previewBusy
            ? <Tag color="processing">运行中</Tag>
            : preview.stale
              ? <Tag color="gold">当前脚本已变更</Tag>
              : preview.success ? <Tag color="success">成功</Tag> : <Tag color="error">失败</Tag>}
          {preview.draft === false && <Tag>流程结果</Tag>}
          <span>{preview.durationMs.toFixed(2)} 毫秒</span>
          {preview.receivedAt && <span>{new Date(preview.receivedAt).toLocaleTimeString()}</span>}
        </button>
        {previewOpen && <div className="script-editor-preview-body">
          {preview.error && <Alert type="error" showIcon title="脚本执行失败" description={<div>
            <pre>{preview.error}</pre>
            {errorLine !== undefined && !preview.stale && <Button type="link" size="small" className="script-editor-preview-jump"
              onClick={() => jumpToLine(errorLine)}>跳转到第 {errorLine} 行</Button>}
          </div>} />}
          {(compileMs !== undefined || executeMs !== undefined || ticks !== undefined || sourceHash || outputImageSize) && <div className="script-editor-preview-metrics">
            {compileMs !== undefined && <span><b>编译</b> {compileMs.toFixed(2)} ms</span>}
            {executeMs !== undefined && <span><b>执行</b> {executeMs.toFixed(2)} ms</span>}
            {ticks !== undefined && <span><b>ticks</b> {ticks.toLocaleString()}</span>}
            {outputImageSize && <span><b>图像</b> {outputImageSize}</span>}
            {sourceHash && <span title={sourceHash}><b>源码</b> {sourceHash.length > 12 ? `${sourceHash.slice(0, 12)}…` : sourceHash}</span>}
          </div>}
          {(inputEntries.length > 0 || outputEntries.length > 0) && <div className="script-editor-preview-tables">
            {inputEntries.length > 0 && <div>
              <div className="script-editor-preview-title">本次输入</div>
              <table>
                <thead><tr><th>端口</th><th>类型</th><th>值</th></tr></thead>
                <tbody>{inputEntries.map(([name, item]) => <tr key={name}>
                  <td>{name}</td><td>{item.type}</td><td title={previewValueText(item)}>{previewValueText(item)}</td>
                </tr>)}</tbody>
              </table>
            </div>}
            {outputEntries.length > 0 && <div>
              <div className="script-editor-preview-title">脚本输出</div>
              <table>
                <thead><tr><th>端口</th><th>类型</th><th>值</th></tr></thead>
                <tbody>{outputEntries.map(([name, item]) => <tr key={name}>
                  <td>{name}</td><td>{item.type}</td><td title={previewValueText(item)}>{previewValueText(item)}</td>
                </tr>)}</tbody>
              </table>
            </div>}
          </div>}
          {logs.length > 0 && <div className="script-editor-preview-logs">
            <div className="script-editor-preview-title">脚本日志</div>
            <ol>{logs.map((line, index) => <li key={index}>{line}</li>)}</ol>
          </div>}
          {preview.imageUrl && <figure className="script-editor-preview-image">
            <img src={preview.imageUrl} alt="脚本输出图像预览" loading="lazy" />
            <figcaption>输出图像{outputImageSize ? ` · ${outputImageSize}` : ''}</figcaption>
          </figure>}
        </div>}
      </section>}
    </div>
  );
}
