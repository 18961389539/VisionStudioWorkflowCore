/**
 * 多工位配置草稿（2026-11-23）。
 *
 * 现场登记工位要同时改三处：`site-config/appsettings.site.json` 的 `ProductionFleet.Stations`、
 * 宿主运行账户里的两个令牌环境变量、然后重启。手写 JSON 最容易错的是**令牌缺失**——
 * `VisionStudioServiceRegistration` 对 `ProductionFleetOptions` 用了 `Validate(...)`，
 * 校验失败会在宿主启动时抛 OptionsValidationException，表现为"服务起不来"。
 *
 * 这里把后端 `ProductionStationOptions.IsValid` 的规则逐条搬过来，在浏览器里先拦住，
 * 再生成可直接粘贴的配置片段。校验口径必须**不比后端更宽**：宁可多提示一句，
 * 也不要让一份界面说"没问题"的配置把宿主拖死。
 */

export type StationConfigDraft = {
  id: string;
  name: string;
  baseUrl: string;
  token: string;
  deliveryToken: string;
  expectedJobId: string;
  expectedVersion: string;
  expectedWorkflowHash: string;
};

export const emptyStationDraft: StationConfigDraft = {
  id: '', name: '', baseUrl: '', token: '', deliveryToken: '',
  expectedJobId: '', expectedVersion: '', expectedWorkflowHash: '',
};

export type StationDraftField = keyof StationConfigDraft;
export type StationDraftIssue = { field: StationDraftField; message: string };

const STATION_ID = /^[A-Za-z0-9_-]{1,64}$/;
const JOB_ID = /^[A-Za-z0-9_-]+$/;
const WORKFLOW_HASH = /^[0-9a-fA-F]{64}$/;
const TOKEN_CHARS = /^[A-Za-z0-9_-]+$/;

/** 与后端 `ProductionStationOptions.IsToken` 同口径。 */
function isToken(value: string): boolean {
  return value.startsWith('vst_') && value.length <= 256 && TOKEN_CHARS.test(value);
}

function isLoopbackHost(host: string): boolean {
  return host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host === '::1';
}

/** 后端要求：绝对地址；https，或 http 但必须是回环；不得带账号、查询串、片段。 */
function baseUrlIssue(value: string): string | undefined {
  let url: URL;
  try { url = new URL(value); } catch { return '请填写完整的绝对地址，例如 https://inspection-a.example/'; }
  if (url.protocol !== 'https:' && !(url.protocol === 'http:' && isLoopbackHost(url.hostname))) {
    return '跨主机必须用 https；http 只允许 localhost / 127.0.0.1 / ::1（测试用）';
  }
  if (url.username || url.password) return '地址里不能带账号密码';
  if (url.search || url.hash) return '地址里不能带查询参数或 # 片段';
  return undefined;
}

export function validateStationDraft(
  draft: StationConfigDraft,
  context: { existingIds?: string[]; stationCount?: number } = {},
): StationDraftIssue[] {
  const issues: StationDraftIssue[] = [];
  const id = draft.id.trim();
  const name = draft.name.trim();
  const baseUrl = draft.baseUrl.trim();
  const token = draft.token.trim();
  const deliveryToken = draft.deliveryToken.trim();
  const expectedJobId = draft.expectedJobId.trim();
  const expectedVersion = draft.expectedVersion.trim();
  const expectedWorkflowHash = draft.expectedWorkflowHash.trim();

  if (!STATION_ID.test(id)) issues.push({ field: 'id', message: '工位 ID 只能用字母、数字、- 或 _，长度 1–64' });
  else if ((context.existingIds ?? []).some((value) => value.toLowerCase() === id.toLowerCase())) {
    issues.push({ field: 'id', message: '该工位 ID 已存在（大小写不敏感）' });
  }
  if ((context.stationCount ?? 0) >= 32) issues.push({ field: 'id', message: '工位数量上限为 32 个' });

  if (!name || name.length > 80) issues.push({ field: 'name', message: '工位名称必填，且不超过 80 个字符' });

  const urlIssue = baseUrlIssue(baseUrl);
  if (!baseUrl || urlIssue) issues.push({ field: 'baseUrl', message: urlIssue ?? '请填写工位地址' });

  if (!isToken(token)) issues.push({ field: 'token', message: '读取令牌必须以 vst_ 开头，只含字母、数字、- 或 _' });
  if (deliveryToken && !isToken(deliveryToken)) {
    issues.push({ field: 'deliveryToken', message: '下发令牌必须以 vst_ 开头，只含字母、数字、- 或 _' });
  } else if (deliveryToken && deliveryToken === token) {
    issues.push({ field: 'deliveryToken', message: '下发令牌必须与读取令牌不同' });
  }

  if (expectedJobId) {
    if (!JOB_ID.test(expectedJobId)) issues.push({ field: 'expectedJobId', message: '配方 ID 只能含字母、数字、- 或 _' });
    const version = Number(expectedVersion);
    if (!expectedVersion || !Number.isInteger(version) || version <= 0) {
      issues.push({ field: 'expectedVersion', message: '填了配方 ID 就必须填大于 0 的期望版本' });
    }
    if (expectedWorkflowHash && !WORKFLOW_HASH.test(expectedWorkflowHash)) {
      issues.push({ field: 'expectedWorkflowHash', message: '流程指纹是 64 位十六进制' });
    }
  } else if (expectedVersion || expectedWorkflowHash) {
    issues.push({ field: 'expectedJobId', message: '填写期望版本或流程指纹前，必须先填配方 ID' });
  }

  return issues;
}

type StationJson = {
  Id: string;
  Name: string;
  BaseUrl: string;
  ExpectedJobId?: string;
  ExpectedVersion?: number;
  ExpectedWorkflowHash?: string;
};

export function stationObject(draft: StationConfigDraft): StationJson {
  const station: StationJson = {
    Id: draft.id.trim(),
    Name: draft.name.trim(),
    BaseUrl: draft.baseUrl.trim(),
  };
  const expectedJobId = draft.expectedJobId.trim();
  if (expectedJobId) {
    station.ExpectedJobId = expectedJobId;
    station.ExpectedVersion = Number(draft.expectedVersion.trim());
    const hash = draft.expectedWorkflowHash.trim();
    if (hash) station.ExpectedWorkflowHash = hash;
  }
  return station;
}

/**
 * 令牌刻意**不写进 JSON**：site-config 会随系统备份导出，令牌应留在宿主环境变量里。
 * `startIndex === 0` 时连 `ProductionFleet` 骨架一起生成，否则只给一个可粘进 `Stations` 数组的对象。
 */
export function stationConfigJson(draft: StationConfigDraft, startIndex = 0): string {
  const station = stationObject(draft);
  if (startIndex > 0) return JSON.stringify(station, null, 2);
  return JSON.stringify({ ProductionFleet: { Stations: [station] } }, null, 2);
}

/** 环境变量按**列表下标**绑定，不从工位 ID 推断。 */
export function stationEnvText(draft: StationConfigDraft, index: number): string {
  const lines = [`ProductionFleet__Stations__${index}__Token=${draft.token.trim()}`];
  const deliveryToken = draft.deliveryToken.trim();
  if (deliveryToken) lines.push(`ProductionFleet__Stations__${index}__DeliveryToken=${deliveryToken}`);
  return lines.join('\n');
}
