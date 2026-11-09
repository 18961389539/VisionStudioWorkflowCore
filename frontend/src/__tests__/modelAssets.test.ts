import { afterEach, describe, expect, it, vi } from 'vitest';
import { bytesToBase64, clearShapeModelAssetCache, createShapeModelAsset, fetchShapeModelBase64 } from '../modelAssets';

describe('shape model asset client', () => {
  afterEach(() => {
    clearShapeModelAssetCache();
    vi.restoreAllMocks();
  });

  it('converts bytes to base64 across chunk boundaries', () => {
    const bytes = new Uint8Array(0x8000 * 2 + 7);
    for (let index = 0; index < bytes.length; index++) bytes[index] = index % 251;

    const encoded = bytesToBase64(bytes);
    const decoded = Uint8Array.from(atob(encoded), (char) => char.charCodeAt(0));
    expect(decoded).toEqual(bytes);
  });

  it('posts create requests as JSON and returns the descriptor', async () => {
    const descriptor = { id: 'shape-a', name: 'A', latestVersion: 1, versions: [] };
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify(descriptor), { status: 200, headers: { 'Content-Type': 'application/json' } }));

    const created = await createShapeModelAsset({ name: 'A', modelBase64: 'AAAA', trainingConfig: 'cfg' });

    expect(created.id).toBe('shape-a');
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(String(url)).toBe('/api/model-assets');
    expect(init?.method).toBe('POST');
    expect(JSON.parse(String(init?.body))).toEqual({ name: 'A', modelBase64: 'AAAA', trainingConfig: 'cfg' });
  });

  it('caches blob reads per asset version', async () => {
    const payload = new Uint8Array([1, 2, 3, 250]);
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response(payload, { status: 200 }));

    const first = await fetchShapeModelBase64('shape-a', 2);
    const second = await fetchShapeModelBase64('shape-a', 2);

    expect(first).toBe(second);
    expect(first).toBe(bytesToBase64(payload));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0]![0])).toBe('/api/model-assets/shape-a/versions/2/blob');
  });

  it('reports missing assets instead of returning empty data', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('missing', { status: 404 }));
    await expect(fetchShapeModelBase64('shape-x', 1)).rejects.toThrow(/HTTP 404/);
  });
});
