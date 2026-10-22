import type { ParameterDescriptor, PortDescriptor } from './types';

export function isPortRequired(port: PortDescriptor, parameters: Record<string, unknown> = {}, descriptors: ParameterDescriptor[] = []): boolean {
  if (port.required) return true;
  const condition = port.requiredWhen;
  if (!condition) return false;
  const configured = parameters[condition.parameter];
  const value = configured === undefined ? descriptors.find(p => p.name === condition.parameter)?.defaultValue : configured;
  return condition.values.some(expected => expected.toLowerCase() === String(value ?? '').toLowerCase());
}
