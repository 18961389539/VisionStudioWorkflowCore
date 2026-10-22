import type { ProductionRuntimeConfig, ProductionRuntimeStatus } from './types';

// 仅用于尚未读取配置时的展示；不能作为可保存的服务端基线。
export const defaultProductionConfig: ProductionRuntimeConfig = {
  jobId: null, autoStart: false, cycleDelayMs: 25, maxCycleMs: 15000,
  maxConsecutiveFailures: 3, autoRecover: true, recoveryDelayMs: 1000, stopTimeoutMs: 5000,
  ptpDriftGuardEnabled: true, ptpGuardWindowRuns: 20, ptpGuardMinimumEvidenceRuns: 5,
  ptpGuardMinimumReadyRate: 0.99, ptpGuardCheckEveryCycles: 10, ptpGuardFaultOnMasterClockChange: true,
  synchronizationHealthGuardEnabled: true, synchronizationGuardWindowRuns: 20,
  synchronizationGuardMinimumEvidenceRuns: 5, synchronizationGuardMaximumFailureRate: 0.05,
  synchronizationGuardMaximumFrameTimeoutRate: 0.05, synchronizationGuardMaxConsecutiveFailures: 2,
  synchronizationGuardMaxConsecutiveSkewViolations: 3, synchronizationGuardMaximumNativeFrameLossRate: 0.005,
  synchronizationGuardMaximumBufferUnderruns: 0, synchronizationGuardMaximumResynchronizations: 0,
  synchronizationGuardMaximumSequenceGapRate: 0.01, synchronizationGuardCheckEveryCycles: 5,
  synchronizationGuardUnhealthyChecksToFault: 2, synchronizationGuardHealthyChecksToRecover: 3,
  synchronizationGuardRecoveryTimeoutMs: 30000,
};

export function isProductionConfig(value: unknown): value is ProductionRuntimeConfig {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const config = value as Record<string, unknown>;
  return (config.jobId == null || typeof config.jobId === 'string')
    && Object.entries(defaultProductionConfig).every(([key, fallback]) => key === 'jobId'
      || (typeof config[key] === typeof fallback && (typeof fallback !== 'number' || Number.isFinite(config[key]))));
}

export function productionConfigsEqual(a: ProductionRuntimeConfig, b: ProductionRuntimeConfig): boolean {
  // 配置字段为标量；同时保留并比较服务端新增的字段，不能因对象键顺序不同误报修改。
  const left = a as unknown as Record<string, unknown>;
  const right = b as unknown as Record<string, unknown>;
  return [...new Set([...Object.keys(left), ...Object.keys(right)])].every((key) => Object.is(left[key], right[key]));
}

export function isProductionStatus(value: unknown): value is ProductionRuntimeStatus {
  if (!value || typeof value !== 'object') return false;
  const status = value as Record<string, unknown>;
  return ['Stopped', 'Starting', 'Running', 'Recovering', 'Stopping', 'Faulted'].includes(String(status.state))
    && typeof status.productionLocked === 'boolean'
    && ['cycleCount', 'okCount', 'ngCount', 'errorCount', 'consecutiveFailures', 'watchdogTrips', 'lastDurationMs']
      .every((key) => typeof status[key] === 'number' && Number.isFinite(status[key]))
    && ['ptpGuard', 'synchronizationGuard'].every((key) => status[key] == null
      || (typeof status[key] === 'object' && Array.isArray((status[key] as { groups?: unknown }).groups)));
}
