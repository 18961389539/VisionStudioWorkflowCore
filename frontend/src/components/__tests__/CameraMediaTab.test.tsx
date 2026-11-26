import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import CameraMediaTab, { type CameraMediaTabProps } from '../CameraMediaTab';
import CameraPanel from '../CameraPanel';
import { media } from '../../endpoints';
import type { MediaItemDescriptor } from '../../types';

const sampleItem: MediaItemDescriptor = {
  relativePath: 'offline/NG/part-01.png',
  source: 'media://offline/NG/part-01.png',
  collection: 'offline',
  label: 'NG',
  name: 'part-01.png',
  extension: '.png',
  bytes: 2048,
  modifiedAt: '2026-11-26T10:00:00Z',
  width: 640,
  height: 480,
  channels: 1,
  bitDepth: 8,
  sha256: 'abcdef1234567890abcdef1234567890'
};

function makeProps(overrides: Partial<CameraMediaTabProps> = {}): CameraMediaTabProps {
  return {
    mediaLoading: false,
    mediaStatus: {
      root: 'media://',
      collectionCount: 2,
      itemCount: 5,
      ngItemCount: 2,
      totalBytes: 10240,
      maxImportBytes: 100 * 1024 * 1024,
      maxLibraryBytes: 1024 * 1024 * 1024,
      allowedExtensions: ['.png', '.jpg', '.bmp', '.tif']
    },
    newCollection: '',
    setNewCollection: vi.fn(),
    createCollection: vi.fn(),
    uploadCollection: 'offline',
    setUploadCollection: vi.fn(),
    collections: [
      { name: 'offline', source: 'media://offline', itemCount: 3, ngItemCount: 2, totalBytes: 6144, lastModifiedAt: null },
      { name: 'golden', source: 'media://golden', itemCount: 2, ngItemCount: 0, totalBytes: 4096, lastModifiedAt: null }
    ],
    uploadLabel: 'Unlabeled',
    setUploadLabel: vi.fn(),
    uploadFiles: [],
    setUploadFiles: vi.fn(),
    importMedia: vi.fn(),
    linkedSources: [
      { name: 'train-dir', path: 'D:\\Images\\Train', itemCount: 12, totalBytes: 51200, lastModifiedAt: '2026-11-26T09:00:00Z', authorizedBy: 'admin' }
    ],
    linkedDirectoryName: '',
    setLinkedDirectoryName: vi.fn(),
    linkedDirectoryPath: '',
    setLinkedDirectoryPath: vi.fn(),
    linkDirectory: vi.fn(),
    unlinkDirectory: vi.fn(),
    bulkUpdateMedia: vi.fn().mockResolvedValue({ succeeded: 1, items: [], failures: [] }),
    downloadManifest: vi.fn(),
    verifyManifest: vi.fn(),
    scanIntegrity: vi.fn(),
    repairIndex: vi.fn(),
    mediaSearch: '',
    setMediaSearch: vi.fn(),
    setMediaMinWidth: vi.fn(),
    setMediaMaxWidth: vi.fn(),
    mediaModifiedAfter: '',
    setMediaModifiedAfter: vi.fn(),
    mediaModifiedBefore: '',
    setMediaModifiedBefore: vi.fn(),
    deleteMedia: vi.fn(),
    setMediaCollection: vi.fn(),
    setMediaLabel: vi.fn(),
    mediaItems: [sampleItem],
    mediaPage: 1,
    setMediaPage: vi.fn(),
    mediaPageSize: 20,
    setMediaPageSize: vi.fn(),
    mediaTotal: 1,
    refreshMedia: vi.fn(),
    resetMediaFilters: vi.fn(),
    setSelectedMedia: vi.fn(),
    selectedMedia: sampleItem,
    onCopyMessage: vi.fn(),
    applyMediaAsFileCameraSource: vi.fn(),
    ...overrides
  };
}

describe('CameraMediaTab', () => {
  beforeEach(() => {
    Object.defineProperty(window, 'matchMedia', {
      configurable: true,
      value: vi.fn(() => ({
        matches: false,
        media: '',
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        dispatchEvent: vi.fn()
      }))
    });
    vi.stubGlobal('ResizeObserver', class {
      observe() { /* jsdom 无布局 */ }
      unobserve() { /* jsdom 无布局 */ }
      disconnect() { /* jsdom 无布局 */ }
    });
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it('uses full-res preview for selected item and falls back to thumbnail on decode error', () => {
    render(<CameraMediaTab {...makeProps()} />);
    const images = screen.getAllByAltText('part-01.png') as HTMLImageElement[];
    const previewImg = images.find((img) => img.classList.contains('media-preview-image'));
    expect(previewImg).toBeTruthy();
    expect(previewImg!.getAttribute('src')).toBe(media.preview(sampleItem.relativePath));

    fireEvent.error(previewImg!);
    expect(previewImg!.getAttribute('src')).toBe(media.thumbnail(sampleItem.relativePath));
    expect(screen.getByText(/已自动切换为归一化缩略图预览/)).toBeTruthy();
  });

  it('supports collection chips, filter reset, single-item rename, and file-camera handoff', async () => {
    const setMediaCollection = vi.fn();
    const setUploadCollection = vi.fn();
    const resetMediaFilters = vi.fn();
    const bulkUpdateMedia = vi.fn().mockResolvedValue({ succeeded: 1, items: [], failures: [] });
    const applyMediaAsFileCameraSource = vi.fn();

    render(
      <CameraMediaTab
        {...makeProps({
          mediaSearch: 'part',
          setMediaCollection,
          setUploadCollection,
          resetMediaFilters,
          bulkUpdateMedia,
          applyMediaAsFileCameraSource
        })}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: /golden/i }));
    expect(setMediaCollection).toHaveBeenCalledWith('golden');
    expect(setUploadCollection).toHaveBeenCalledWith('golden');

    fireEvent.click(screen.getByRole('button', { name: /^重\s*置$/ }));
    expect(resetMediaFilters).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: '重命名' }));
    const renameInput = screen.getByLabelText('新文件名');
    fireEvent.change(renameInput, { target: { value: 'part-02.png' } });
    await act(async () => {
      fireEvent.click(screen.getByRole('button', { name: '确认重命名' }));
    });
    expect(bulkUpdateMedia).toHaveBeenCalledWith(
      ['offline/NG/part-01.png'],
      'rename',
      { targetName: 'part-02.png' }
    );

    fireEvent.click(screen.getByRole('button', { name: '用作文件相机来源' }));
    expect(applyMediaAsFileCameraSource).toHaveBeenCalledWith('media://offline/NG/part-01.png');
  });

  it('browses linked server directory items and renders preview/copy action', () => {
    const browseLinkedSource = vi.fn();
    const linkedItem: MediaItemDescriptor = {
      relativePath: 'sub/img-01.tif',
      source: 'D:\\Images\\Train\\sub\\img-01.tif',
      collection: 'train-dir',
      label: 'Unlabeled',
      name: 'img-01.tif',
      extension: '.tif',
      bytes: 4096,
      modifiedAt: '2026-11-26T09:00:00Z',
      sourceKind: 'linked'
    };
    render(
      <CameraMediaTab
        {...makeProps({
          browseLinkedSource,
          browsingLinkedName: 'train-dir',
          linkedItems: [linkedItem],
          linkedItemsTotal: 1,
          selectedLinkedItem: linkedItem
        })}
      />
    );

    fireEvent.click(screen.getByRole('button', { name: '浏览图片' }));
    expect(browseLinkedSource).toHaveBeenCalledWith('train-dir', 1, 20, '');

    const linkedPreview = screen
      .getAllByAltText('img-01.tif')
      .find((img) => img.classList.contains('media-preview-image')) as HTMLImageElement | undefined;
    expect(linkedPreview).toBeTruthy();
    expect(linkedPreview!.getAttribute('src')).toBe(media.linkedSourcePreview('train-dir', 'sub/img-01.tif'));
    fireEvent.error(linkedPreview!);
    expect(linkedPreview!.getAttribute('src')).toBe(media.linkedSourceThumbnail('train-dir', 'sub/img-01.tif'));
  });

  it('degrades gracefully when linked-sources endpoint returns 403', async () => {
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL) => {
      const url = String(input);
      if (url.includes('/api/media/status')) {
        return Promise.resolve({
          ok: true,
          json: async () => ({ root: 'media://', collectionCount: 1, itemCount: 1, ngItemCount: 0, totalBytes: 100, maxImportBytes: 1000, maxLibraryBytes: 10000, allowedExtensions: ['.png'] })
        } as Response);
      }
      if (url.includes('/api/media/collections')) {
        return Promise.resolve({
          ok: true,
          json: async () => [{ name: 'offline', source: 'media://offline', itemCount: 1, ngItemCount: 0, totalBytes: 100, lastModifiedAt: null }]
        } as Response);
      }
      if (url.includes('/api/media/items')) {
        return Promise.resolve({
          ok: true,
          headers: new Headers({ 'X-Total-Count': '1' }),
          json: async () => [sampleItem]
        } as Response);
      }
      if (url.includes('/api/media/linked-sources')) {
        return Promise.resolve({ ok: false, status: 403, json: async () => ({}) } as Response);
      }
      return Promise.resolve({ ok: true, json: async () => [] } as Response);
    }));

    render(<CameraPanel open initialTab="media" onClose={vi.fn()} />);
    await act(async () => { await Promise.resolve(); await Promise.resolve(); });
    expect(screen.getByText('part-01.png')).toBeTruthy();
    vi.unstubAllGlobals();
  });
});
