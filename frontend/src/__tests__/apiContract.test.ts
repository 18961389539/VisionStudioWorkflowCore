/**
 * API 契约快照测试。
 *
 * 快照：frontend/src/generated/openapi.json（由 scripts/export-openapi.ps1 从后端导出）。
 * 目的：把手写镜像（src/types.ts）与后端 DTO 之间的漂移变成可执行断言——
 *   1) 前端依赖的骨架字段必须在 spec 里存在；
 *   2) 核心域 DTO 必须持续留在 spec 里：端点漏掉 Produces<T>() 会让整条类型生成链断掉。
 * 刷新快照：pwsh scripts/export-openapi.ps1（需先 dotnet build）
 */
import { describe, expect, it } from 'vitest';
import raw from '../generated/openapi.json?raw';

type Schema = { required?: string[]; properties?: Record<string, unknown>; type?: string };
type Spec = { openapi: string; paths: Record<string, unknown>; components: { schemas: Record<string, Schema> } };
const spec = JSON.parse(raw) as Spec;
const schemas = spec.components.schemas;

/**
 * 通过端点 `Produces<T>()` 进入 spec 的核心域 DTO。
 *
 * 2026-10-23（P1-4 第一批）：原先是"尚未进入 spec 的名单，只减不增"的棘轮，现已全部补齐，
 * 断言翻转为正向守卫——这些类型必须存在于 spec 中。后端若把端点改回匿名对象、或 DTO 改名，
 * 这里会立即失败，而这正是要继续生成 types.ts 的前提。
 *
 * 命名注意：前端手写的 RunResult 对应后端 WorkflowRunResult（spec 用后端类型名）。
 */
const REQUIRED_SPEC_DTOS = [
  'DeviceDescriptor',
  'NodeCatalogItem',
  'RobotDescriptor',
  'AlarmRecord',
  'JobDescriptor',
  'RunTraceRecord',
  'TracePage',
  'WorkflowRunResult',
  // 编辑器的校验/编译链路：响应必须是具名 DTO，否则 OpenAPI 生成不出类型（2026-11-10）。
  'WorkflowCompileResponse',
  'WorkflowValidationSuccess',
  'WorkflowValidationFailure',
  // 资产域：面板与训练工作区直接消费，响应不再允许退回匿名对象（2026-11-10）。
  'ShapeModelAssetDescriptor',
  'ShapeModelVersionInfo',
  'ShapeModelAssetGcPlan',
  'ExtractShapeModelAssetResponse',
  'TemplateAssetDescriptor',
  // 数据集 / 批量验证域（2026-11-10 响应具名化）。
  'ValidationDatasetSummary',
  'ValidationDatasetDetail',
  'ValidationRunRecord',
  // 追溯 / 回放 / 存储域（2026-11-10 响应具名化）。
  'TraceStats',
  'RunTraceComparison',
  'RunObservabilitySnapshot',
  'OfflineReplayContext',
  'OfflineReplayResult',
  'StorageBackupDescriptor',
  // 诊断 / 目录元信息（2026-11-10 响应具名化）。
  'DiagnosticsSummary',
  'AssetHealthSnapshot',
  'CatalogMetaResponse',
  'HostModeStatus',
  // 插件注册表（2026-11-10 响应具名化）。
  'PluginLoadInfo',
  'PluginSdkInfo',
  'PluginRescanResult',
  // 认证 / 用户 / 审计（2026-11-10 响应具名化）。
  'SecurityStatus',
  'SessionUserResponse',
  'SecurityUserDto',
  'AuditEventDto',
  // 标定 / 内参资产域（2026-11-11 响应具名化）。
  'CalibrationProviderDescriptor',
  'CalibrationWorkspacePoint',
  'CalibrationWorkspaceResult',
  'CalibrationAssetDescriptor',
  'CalibrationVersionSnapshot',
  'CameraIntrinsicAssetDescriptor',
  'CameraIntrinsicVersionSnapshot',
  // 相机 / 媒体域（2026-11-11 响应具名化）。
  'CameraDescriptor',
  'CameraTransportTelemetry',
  'CameraDiscoveredDevice',
  'MediaLibraryStatus',
  'MediaCollectionDescriptor',
  'MediaItemDescriptor',
  // PLC 设备域（2026-11-11 响应具名化）。
  'DeviceAddressTemplate',
  'DeviceProfile',
  'DeviceTagSample',
  // 作业域（2026-11-11 响应具名化）。
  'JobVersionSnapshot',
  'RuntimeDependencyManifest',
  'RuntimeDependencyValidation',
  // 机器人域（2026-11-11 响应具名化）。
  'RobotCommandTraceRecord',
  // 生产运行域（2026-11-11 响应具名化）。
  'ProductionRuntimeStatus',
  'ProductionPtpGuardSnapshot',
  'ProductionSynchronizationGuardSnapshot',
  // 相机同步 / 相机特性域（2026-11-11 响应具名化）。
  'CameraSynchronizationGroup',
  'CameraSynchronizationRunRecord',
  'CameraSynchronizationStatistics',
  'CameraSynchronizationCommissioningTest',
  'CameraCommissioningSnapshot',
  'CameraFeatureDescriptor',
  'CameraFeatureProfileRecord',
  'CameraFeatureUpdateResponse',
  'CameraFeatureProfileApplyResponse',
  // 插件包 / 模型包 / 工作流模块域（2026-11-11 响应具名化）。
  'PluginPackageVersionInfo',
  'TrustedPluginPublisher',
  'PluginPackagePreflightResult',
  'PluginPackageInstallResult',
  'PluginPackageSelectionResult',
  'ModelPackageSummary',
  'ModelPackageDetail',
  'ExternalTrainManifest',
  'ExternalTrainSmokeResult',
  'WorkflowModuleDescriptor',
  'WorkflowModuleVersion',
  'ExtractWorkflowModuleResult',
  // 调查 / 硬件来源 / 脚本编辑器 / 配方域（2026-11-11 响应具名化）。
  'InvestigationCaseSummary',
  'InvestigationCaseDetail',
  'InvestigationVerificationEvidence',
  'InvestigationTrendDashboard',
  'HardwareProvenanceSnapshot',
  'ScriptValidationResult',
  'ScriptCompletionItem',
  'ScriptFormatResult',
  'JobVersionDiff',
  'ValidationCandidateInfo',
  // 插件基准 / 插件工作进程 / 工作流存储 / 配方参数域（2026-11-11 响应具名化）。
  'PluginBenchmarkRun',
  'PluginWorkerStatus',
  'PluginWorkerPerformanceProfile',
  'WorkflowDocumentSummary',
  'ParameterValueSet',
  'ResolvedRecipeParameterization',
  // 调试会话 / 匹配自动调参 / 参数调优（2026-11-11 响应具名化）。
  'DebugSessionRunResponse',
  'DebugSessionSnapshotResponse',
  'MatchingAutoTuneHttpResponse',
  'ParameterTuningPreviewResult'
] as const;

describe('API contract snapshot', () => {
  it('快照可解析且覆盖后端路由模型', () => {
    expect(spec.openapi).toMatch(/^3\./);
    // 153 为 2026-10-23 实测值（补齐核心域 DTO 前是 101）。下限只用于发现 schema 大规模丢失。
    expect(Object.keys(schemas).length).toBeGreaterThanOrEqual(150);
  });

  it('工作流文档骨架与后端 schema 一致（保存/加载最高频契约）', () => {
    const schema = schemas.WorkflowDefinition;
    expect(schema, '缺少 WorkflowDefinition：后端类型改名后必须同步此测试与前端文档处理').toBeTruthy();
    // schemaVersion 是非空 int（默认当前版本），序列化总会输出，required 由 transformer 补入。
    expect([...(schema.required ?? [])].sort()).toEqual(['edges', 'id', 'name', 'nodes', 'schemaVersion']);
    for (const field of ['id', 'name', 'nodes', 'edges', 'schemaVersion'])
      expect(Object.keys(schema.properties ?? {}), `WorkflowDefinition 缺少前端依赖的字段 ${field}`).toContain(field);
  });

  it('核心域 DTO 必须留在 spec 中（Produces<T> 守卫）', () => {
    const missing = REQUIRED_SPEC_DTOS.filter((name) => !(name in schemas));
    expect(missing, '有 DTO 掉出 spec：检查对应端点是否漏了 Produces<T>()，或后端改名后未同步本清单').toEqual([]);
  });

  it('带默认值/计算属性的非空字段按 required 生成', () => {
    // ASP.NET OpenAPI 默认把 C# 默认值参数与计算属性标成 optional；transformer 必须补回 required。
    expect(schemas.CameraSettings.required).toEqual(expect.arrayContaining(['triggerMode', 'outputPixelFormat', 'externalTriggerSource']));
    expect(schemas.RunCounterPersistenceStatus.required).toContain('degraded');
    // 条件序列化（NodeMaturity 缺省 Stable 不写 JSON）必须保持 optional。
    expect(schemas.NodeCatalogItem.required ?? []).not.toContain('maturity');
  });

  it('JsonElement 载荷生成自由对象而不是 never', () => {
    // .NET 默认把 JsonElement 生成成 "object 无属性"，openapi-typescript 会读成 Record<string, never>；
    // schema transformer 必须补 additionalProperties（对象值）或空 schema（直接 JsonElement）。
    const nodeDefinition = schemas.NodeDefinition;
    const parameters = (nodeDefinition.properties?.parameters ?? {}) as { additionalProperties?: unknown };
    expect(parameters.additionalProperties).toEqual({});

    const parameterDescriptor = schemas.ParameterDescriptor;
    const defaultValue = (parameterDescriptor.properties?.defaultValue ?? {}) as Record<string, unknown>;
    expect(Object.keys(defaultValue)).toEqual([]);
  });

  it('标定/相机列表端点生成数组响应（前端列表渲染依赖）', () => {
    const listSchema = (path: string) =>
      (spec.paths[path] as { get: { responses: Record<string, { content: Record<string, { schema: unknown }> }> } }).get
        .responses['200'].content['application/json'].schema as { type?: string; items?: { $ref?: string } };
    expect(listSchema('/api/calibration/providers')).toMatchObject({ type: 'array', items: { $ref: '#/components/schemas/CalibrationProviderDescriptor' } });
    expect(listSchema('/api/calibrations')).toMatchObject({ type: 'array', items: { $ref: '#/components/schemas/CalibrationAssetDescriptor' } });
    expect(listSchema('/api/camera-intrinsics')).toMatchObject({ type: 'array', items: { $ref: '#/components/schemas/CameraIntrinsicAssetDescriptor' } });
    expect(listSchema('/api/cameras')).toMatchObject({ type: 'array', items: { $ref: '#/components/schemas/CameraDescriptor' } });
    expect(listSchema('/api/media/collections')).toMatchObject({ type: 'array', items: { $ref: '#/components/schemas/MediaCollectionDescriptor' } });
    expect(listSchema('/api/media/items')).toMatchObject({ type: 'array', items: { $ref: '#/components/schemas/MediaItemDescriptor' } });
  });

  it('已有域端点在快照中保留（刷新后不回落）', () => {
    // 2026-11-10 刷新时补入 model-assets/template-assets（此前快照停在 2026-10-29）。
    // 这里只锁"已经存在于快照的域"，避免刷新后的端点再次被旧快照覆盖。
    const requiredPaths = ['/api/validate', '/api/compile', '/api/model-assets', '/api/template-assets', '/api/diagnostics/node-durations', '/api/validation/datasets', '/api/validation/runs/{runId}', '/api/traces/{runId}', '/api/traces/{runId}/replay/context', '/api/storage/backups', '/api/catalog/meta', '/api/diagnostics/summary', '/api/plugins', '/api/plugins/sdk', '/api/auth/status', '/api/security/users', '/api/audit', '/api/calibration/providers', '/api/calibration/capture-grid', '/api/calibration/solve', '/api/calibrations', '/api/camera-intrinsics', '/api/cameras', '/api/cameras/discover', '/api/media/status', '/api/media/collections', '/api/media/items', '/api/devices', '/api/devices/{id}/tags/{tagId}', '/api/device-profiles', '/api/device-address-templates', '/api/jobs', '/api/jobs/{id}', '/api/jobs/{id}/versions/{version}', '/api/jobs/{id}/dependencies', '/api/robots', '/api/robot-traces', '/api/robot-traces/{traceId}', '/api/production/status', '/api/production/config', '/api/production/ptp-guard', '/api/production/synchronization-guard', '/api/camera-sync/groups', '/api/camera-sync/groups/{id}/status', '/api/camera-sync/runs', '/api/camera-sync/groups/{id}/statistics', '/api/camera-sync/groups/{id}/ptp-diagnostics', '/api/camera-sync/groups/{id}/gige-network-diagnostics', '/api/camera-sync/commissioning-tests/{testId}', '/api/cameras/{id}/commissioning', '/api/cameras/{id}/features', '/api/camera-feature-profiles', '/api/plugin-packages', '/api/plugin-publishers', '/api/model-packages', '/api/model-packages/{id}', '/api/modules', '/api/modules/{id}', '/api/modules/{id}/versions/{version}', '/api/modules/extract', '/api/investigations', '/api/investigations/trends', '/api/investigations/{id}', '/api/investigations/{id}/verification-evidence', '/api/hardware-provenance', '/api/hardware-provenance/{kind}/{id}', '/api/scripts/validate', '/api/scripts/complete', '/api/scripts/format', '/api/recipes', '/api/jobs/{id}/versions/{fromVersion}/diff/{toVersion}', '/api/plugin-benchmarks', '/api/plugin-benchmarks/{runId}', '/api/plugin-benchmarks/{runId}/ci-spec', '/api/plugin-workers', '/api/plugin-workers/performance', '/api/workflows', '/api/workflows/{id}', '/api/jobs/{id}/parameters', '/api/jobs/{id}/parameterization/resolve', '/api/debug/sessions', '/api/debug/sessions/{sessionId}', '/api/matching/auto-tune', '/api/tuning/preview'];
    expect(requiredPaths.filter((path) => !(path in spec.paths))).toEqual([]);
  });
});
