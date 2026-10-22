import { describe, expect, it, vi } from 'vitest';
import { apiErrorMessage, responseErrorMessage } from '../apiErrors';

describe('API error presentation', () => {
  it('keeps detail, code and correlation id from ProblemDetails', () => {
    expect(apiErrorMessage({ detail: 'PLC disconnected', title: 'Request failed', code: 'DEVICE_OFFLINE', correlationId: 'req-42' }, 'fallback'))
      .toBe('PLC disconnected（DEVICE_OFFLINE · 关联号 req-42）');
  });

  it('uses a readable fallback for non-JSON error responses', async () => {
    const response = { json: vi.fn().mockRejectedValue(new Error('invalid json')) } as unknown as Response;
    await expect(responseErrorMessage(response, '操作失败')).resolves.toBe('操作失败');
  });
});
