/**
 * Q10：统一的 API 错误消息适配器。
 *
 * 后端失败响应统一为 ProblemDetails（detail / title / code / correlationId）。
 * 各面板此前各自解析（部分只取 data.error），导致具体原因（设备故障、租约冲突、
 * 禁止动作）退化成通用文案，排障时既看不到原因也拿不到关联号。
 *
 * 约束：本函数只负责"把已知信息完整呈现给用户"。危险写入在解析失败时**不得**变成
 * 自动重试——调用方必须让操作停滞并要求人工确认。
 */

export interface ProblemDetailsLike {
  detail?: unknown;
  error?: unknown;
  title?: unknown;
  code?: unknown;
  correlationId?: unknown;
}

export function apiErrorMessage(payload: unknown, fallback: string): string {
  if (payload == null || typeof payload !== 'object') return fallback;
  const p = payload as ProblemDetailsLike;
  const pick = (value: unknown): string | undefined =>
    typeof value === 'string' && value.trim() ? value.trim() : undefined;

  const message = pick(p.detail) ?? pick(p.error) ?? pick(p.title) ?? fallback;
  const annotations: string[] = [];
  const code = pick(p.code);
  const correlationId = pick(p.correlationId);
  if (code) annotations.push(code);
  if (correlationId) annotations.push(`关联号 ${correlationId}`);
  return annotations.length > 0 ? `${message}（${annotations.join(' · ')}）` : message;
}

/** 尽力读取错误响应体；非 JSON（网关错误页等）时退化为 null。 */
export async function readErrorPayload(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return null;
  }
}

export async function responseErrorMessage(response: Response, fallback: string): Promise<string> {
  return apiErrorMessage(await readErrorPayload(response), fallback);
}
