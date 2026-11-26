import { useEffect, useRef, useState } from 'react';
import {
  Alert, Button, Collapse, Input, InputNumber, Modal, Popconfirm, Progress,
  Segmented, Select, Space, Table, Tag, Typography, Upload
} from 'antd';
import type { UploadFile } from 'antd/es/upload/interface';
import type { InputHTMLAttributes } from 'react';
import type {
  MediaCollectionDescriptor, MediaItemDescriptor, MediaLibraryStatus,
  MediaLinkedSourceDescriptor, MediaIntegrityReport
} from '../types';
import { media } from '../endpoints';
import { formatBytes, labelColor, labelText } from './cameraPanelShared';

const MEDIA_IMPORT_MAX_BATCH_FILES = 200;

export type CameraMediaTabProps = {
  busy?: string;
  mediaLoading?: boolean;
  mediaStatus?: MediaLibraryStatus;
  newCollection: string;
  setNewCollection: (value: string) => void;
  createCollection: () => void | Promise<void>;
  uploadCollection: string;
  setUploadCollection: (value: string) => void;
  collections: MediaCollectionDescriptor[];
  uploadLabel: string;
  setUploadLabel: (value: string) => void;
  uploadFiles: UploadFile[];
  setUploadFiles: (updater: UploadFile[] | ((list: UploadFile[]) => UploadFile[])) => void;
  importMedia: (files?: File[]) => boolean | undefined | Promise<boolean | undefined>;
  importProgress?: { completed: number; total: number; batch: number; totalBatches: number };
  linkedSources: MediaLinkedSourceDescriptor[];
  linkedDirectoryName: string;
  setLinkedDirectoryName: (value: string) => void;
  linkedDirectoryPath: string;
  setLinkedDirectoryPath: (value: string) => void;
  linkDirectory: () => void | Promise<void>;
  unlinkDirectory: (name: string) => void | Promise<void>;
  browsingLinkedName?: string;
  setBrowsingLinkedName?: (name?: string) => void;
  linkedItems?: MediaItemDescriptor[];
  linkedItemsTotal?: number;
  linkedItemsPage?: number;
  linkedItemsPageSize?: number;
  linkedItemsSearch?: string;
  setLinkedItemsSearch?: (value: string) => void;
  linkedItemsLoading?: boolean;
  selectedLinkedItem?: MediaItemDescriptor;
  setSelectedLinkedItem?: (item?: MediaItemDescriptor) => void;
  browseLinkedSource?: (name: string, page?: number, pageSize?: number, search?: string) => void | Promise<void>;
  bulkUpdateMedia: (
    paths: string[],
    action: 'delete' | 'move' | 'relabel' | 'rename',
    options: { targetCollection?: string; targetLabel?: string; targetName?: string }
  ) => Promise<{ succeeded: number; failures: unknown[] } | undefined>;
  mediaIntegrity?: MediaIntegrityReport;
  downloadManifest: () => void | Promise<void>;
  verifyManifest: (file: File) => void | Promise<void>;
  scanIntegrity: () => void | Promise<void>;
  repairIndex: () => void | Promise<void>;
  mediaSearch: string;
  setMediaSearch: (value: string) => void;
  mediaMinWidth?: number;
  setMediaMinWidth: (value?: number) => void;
  mediaMaxWidth?: number;
  setMediaMaxWidth: (value?: number) => void;
  mediaModifiedAfter: string;
  setMediaModifiedAfter: (value: string) => void;
  mediaModifiedBefore: string;
  setMediaModifiedBefore: (value: string) => void;
  deleteMedia: (item: MediaItemDescriptor) => void | Promise<void>;
  mediaCollection?: string;
  setMediaCollection: (value?: string) => void;
  mediaLabel?: string;
  setMediaLabel: (value?: string) => void;
  mediaItems: MediaItemDescriptor[];
  mediaPage: number;
  setMediaPage: (value: number) => void;
  mediaPageSize: number;
  setMediaPageSize: (value: number) => void;
  mediaTotal: number;
  refreshMedia: (quiet: boolean, page: number, pageSize: number) => void | Promise<void>;
  resetMediaFilters?: () => void;
  setSelectedMedia: (item?: MediaItemDescriptor) => void;
  selectedMedia?: MediaItemDescriptor;
  onCopyMessage?: (text: string) => void;
  /** 「用作文件相机来源」：把所选媒体的 media:// 引用写进文件相机注册表单。 */
  applyMediaAsFileCameraSource: (source: string) => void;
};

/** 「媒体库」标签页：集合快捷栏、筛选检索、批量管理、高清预览与服务端目录在线浏览。 */
export default function CameraMediaTab(props: CameraMediaTabProps) {
  const {
    busy, mediaLoading = false, mediaStatus, newCollection, setNewCollection, createCollection,
    uploadCollection, setUploadCollection, collections, uploadLabel, setUploadLabel, uploadFiles,
    setUploadFiles, importMedia, importProgress, linkedSources,
    linkedDirectoryName, setLinkedDirectoryName, linkedDirectoryPath, setLinkedDirectoryPath,
    linkDirectory, unlinkDirectory,
    browsingLinkedName, setBrowsingLinkedName, linkedItems = [], linkedItemsTotal = 0,
    linkedItemsPage = 1, linkedItemsPageSize = 20, linkedItemsSearch = '', setLinkedItemsSearch,
    linkedItemsLoading = false, selectedLinkedItem, setSelectedLinkedItem, browseLinkedSource,
    bulkUpdateMedia, mediaIntegrity,
    downloadManifest, verifyManifest, scanIntegrity, repairIndex,
    mediaSearch, setMediaSearch, mediaMinWidth, setMediaMinWidth, mediaMaxWidth, setMediaMaxWidth,
    mediaModifiedAfter, setMediaModifiedAfter, mediaModifiedBefore, setMediaModifiedBefore,
    deleteMedia, mediaCollection, setMediaCollection, mediaLabel, setMediaLabel,
    mediaItems, mediaPage, setMediaPage, mediaPageSize, setMediaPageSize, mediaTotal, refreshMedia,
    resetMediaFilters, setSelectedMedia, selectedMedia, onCopyMessage, applyMediaAsFileCameraSource,
  } = props;
  const directoryInputRef = useRef<HTMLInputElement>(null);
  const manifestInputRef = useRef<HTMLInputElement>(null);
  const directoryFilesRef = useRef<File[]>([]);
  const [selectionNotice, setSelectionNotice] = useState<string>();
  const [directoryFileCount, setDirectoryFileCount] = useState(0);
  const [selectedMediaPaths, setSelectedMediaPaths] = useState<string[]>([]);
  const [bulkLabel, setBulkLabel] = useState('Unlabeled');
  const [bulkCollection, setBulkCollection] = useState<string>();
  const [previewFallback, setPreviewFallback] = useState(false);
  const [lightboxOpen, setLightboxOpen] = useState(false);
  const [lightboxMode, setLightboxMode] = useState<'fit' | 'actual'>('fit');
  const [renaming, setRenaming] = useState(false);
  const [renameText, setRenameText] = useState('');
  const [linkedPreviewFallback, setLinkedPreviewFallback] = useState(false);
  const maxBatchFiles = MEDIA_IMPORT_MAX_BATCH_FILES;
  const maxImportBytes = mediaStatus?.maxImportBytes ?? 100 * 1024 * 1024;
  const directoryAttributes = {
    directory: '',
    webkitdirectory: ''
  } as InputHTMLAttributes<HTMLInputElement>;

  const chooseDirectory = (files: FileList | null) => {
    const candidates = Array.from(files ?? []);
    const images = candidates.filter((file) => /\.(bmp|png|jpe?g|tiff?)$/i.test(file.name));
    const unique = new Set<string>();
    const accepted: File[] = [];
    let duplicates = 0;
    let oversized = 0;
    for (const file of images) {
      const key = `${file.webkitRelativePath ?? ''}:${file.name}:${file.lastModified}:${file.size}`;
      if (unique.has(key)) { duplicates++; continue; }
      if (file.size <= 0 || file.size > maxImportBytes) { oversized++; continue; }
      unique.add(key);
      accepted.push(file);
    }
    directoryFilesRef.current = accepted;
    setDirectoryFileCount(accepted.length);
    setUploadFiles([]);
    const ignored = candidates.length - images.length;
    const notices = [
      accepted.length ? `已选择 ${accepted.length} 张图片，将分 ${Math.ceil(accepted.length / maxBatchFiles)} 批上传` : '没有可加入的新图片',
      ignored ? `忽略 ${ignored} 个非图片文件` : '',
      duplicates ? `忽略 ${duplicates} 张重复图片` : '',
      oversized ? `${oversized} 张超过单图大小限制` : ''
    ].filter(Boolean);
    setSelectionNotice(notices.join('；') + '。');
    if (directoryInputRef.current) directoryInputRef.current.value = '';
  };

  const chooseFiles = (fileList: UploadFile[]) => {
    directoryFilesRef.current = [];
    setDirectoryFileCount(0);
    setSelectionNotice(undefined);
    setUploadFiles(fileList.slice(-maxBatchFiles));
  };

  const runImport = async () => {
    const directoryFiles = directoryFilesRef.current;
    const ok = await importMedia(directoryFiles.length ? directoryFiles : undefined);
    if (ok && directoryFiles.length) {
      directoryFilesRef.current = [];
      setDirectoryFileCount(0);
      setSelectionNotice(undefined);
    }
  };

  const pendingImportCount = directoryFileCount || uploadFiles.length;

  useEffect(() => {
    const visible = new Set(mediaItems.map((item) => item.relativePath));
    setSelectedMediaPaths((current) => current.filter((path) => visible.has(path)));
  }, [mediaItems]);

  useEffect(() => {
    setPreviewFallback(false);
    setRenaming(false);
    setRenameText(selectedMedia?.name ?? '');
  }, [selectedMedia?.relativePath, selectedMedia?.name]);

  useEffect(() => {
    setLinkedPreviewFallback(false);
  }, [browsingLinkedName, selectedLinkedItem?.relativePath]);

  const runBulk = async (
    action: 'delete' | 'move' | 'relabel',
    options: { targetCollection?: string; targetLabel?: string }) => {
    const result = await bulkUpdateMedia(selectedMediaPaths, action, options);
    if (result && result.failures.length === 0) setSelectedMediaPaths([]);
  };

  const copyText = async (value: string, successMessage: string) => {
    try {
      await navigator.clipboard?.writeText(value);
      onCopyMessage?.(successMessage);
    } catch {
      onCopyMessage?.(`已就绪：${value}`);
    }
  };

  const hasFilters = Boolean(
    mediaSearch.trim() || mediaCollection || mediaLabel
    || mediaMinWidth !== undefined || mediaMaxWidth !== undefined
    || mediaModifiedAfter || mediaModifiedBefore
  );

  const selectedPreviewSrc = selectedMedia
    ? (previewFallback ? media.thumbnail(selectedMedia.relativePath) : media.preview(selectedMedia.relativePath))
    : undefined;

  const linkedPreviewSrc = browsingLinkedName && selectedLinkedItem
    ? (linkedPreviewFallback
      ? media.linkedSourceThumbnail(browsingLinkedName, selectedLinkedItem.relativePath)
      : media.linkedSourcePreview(browsingLinkedName, selectedLinkedItem.relativePath))
    : undefined;

  const triggerQuery = () => {
    setMediaPage(1);
    void refreshMedia(false, 1, mediaPageSize);
  };

  const selectCollectionChip = (name?: string) => {
    setMediaCollection(name);
    if (name) setUploadCollection(name);
  };

  return (
    <>
      <div className="media-header-bar">
        <Typography.Title level={5} style={{ margin: 0 }}>媒体库</Typography.Title>
        <Space wrap>
          <Tag color="blue">{mediaStatus?.collectionCount ?? collections.length} 个集合</Tag>
          <Tag>{mediaStatus?.itemCount ?? 0} 张图像</Tag>
          <Tag color="red">{mediaStatus?.ngItemCount ?? 0} 个 NG</Tag>
          <Tag>{formatBytes(mediaStatus?.totalBytes ?? 0)} / {formatBytes(mediaStatus?.maxLibraryBytes ?? 0)}</Tag>
        </Space>
      </div>

      {/* 集合快捷导航栏：一键筛选集合，并同步默认上传目标集合 */}
      <div className="media-collection-bar" role="navigation" aria-label="集合快捷筛选">
        <Button
          size="small"
          type={!mediaCollection ? 'primary' : 'default'}
          onClick={() => selectCollectionChip(undefined)}
        >
          全部 ({mediaStatus?.itemCount ?? mediaTotal})
        </Button>
        {collections.map((col) => (
          <Button
            key={col.name}
            size="small"
            type={mediaCollection === col.name ? 'primary' : 'default'}
            onClick={() => selectCollectionChip(col.name)}
          >
            <span>{col.name}</span>
            <span className="media-collection-count">{col.itemCount}</span>
            {col.ngItemCount > 0 && <Tag color="red" style={{ marginInlineStart: 4, marginInlineEnd: 0 }}>NG {col.ngItemCount}</Tag>}
          </Button>
        ))}
      </div>

      {/* 筛选栏 */}
      <div className="media-filter-row">
        <Input
          aria-label="搜索媒体文件名"
          allowClear
          placeholder="搜索文件名或路径"
          value={mediaSearch}
          onChange={(event) => setMediaSearch(event.target.value)}
          onPressEnter={triggerQuery}
        />
        <Select
          aria-label="筛选集合"
          allowClear
          placeholder="全部集合"
          value={mediaCollection}
          onChange={setMediaCollection}
          options={collections.map((x) => ({ value: x.name, label: `${x.name} (${x.itemCount})` }))}
        />
        <Select
          aria-label="筛选标签"
          allowClear
          placeholder="全部标签"
          value={mediaLabel}
          onChange={setMediaLabel}
          options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))}
        />
        <InputNumber
          aria-label="最小宽度"
          min={1}
          max={100000}
          placeholder="最小宽"
          value={mediaMinWidth}
          onChange={(value) => setMediaMinWidth(value == null ? undefined : Number(value))}
        />
        <InputNumber
          aria-label="最大宽度"
          min={1}
          max={100000}
          placeholder="最大宽"
          value={mediaMaxWidth}
          onChange={(value) => setMediaMaxWidth(value == null ? undefined : Number(value))}
        />
        <Input aria-label="修改日期起点" type="date" value={mediaModifiedAfter} onChange={(event) => setMediaModifiedAfter(event.target.value)} />
        <Input aria-label="修改日期终点" type="date" value={mediaModifiedBefore} onChange={(event) => setMediaModifiedBefore(event.target.value)} />
        <Space size={4}>
          <Button type="primary" ghost onClick={triggerQuery}>扫描</Button>
          {resetMediaFilters && (
            <Button disabled={!hasFilters} onClick={resetMediaFilters}>重置</Button>
          )}
        </Space>
      </div>

      {/* 批量操作栏 */}
      <Space wrap style={{ marginBottom: 8 }}>
        <Tag>{selectedMediaPaths.length} 项已选</Tag>
        <Select
          aria-label="批量标签"
          style={{ width: 110 }}
          value={bulkLabel}
          onChange={setBulkLabel}
          options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))}
        />
        <Button
          size="small"
          disabled={selectedMediaPaths.length === 0}
          loading={busy === 'media-bulk'}
          onClick={() => void runBulk('relabel', { targetLabel: bulkLabel })}
        >
          改标签
        </Button>
        <Select
          aria-label="目标集合"
          showSearch
          allowClear
          style={{ width: 165 }}
          placeholder="选择目标集合"
          value={bulkCollection}
          onChange={setBulkCollection}
          options={collections.map((x) => ({ value: x.name, label: x.name }))}
        />
        <Button
          size="small"
          disabled={selectedMediaPaths.length === 0 || !bulkCollection?.trim()}
          loading={busy === 'media-bulk'}
          onClick={() => void runBulk('move', { targetCollection: bulkCollection?.trim() })}
        >
          移动到集合
        </Button>
        <Popconfirm
          title={`删除选中的 ${selectedMediaPaths.length} 个媒体项？`}
          okText="删除"
          cancelText="取消"
          onConfirm={() => void runBulk('delete', {})}
        >
          <Button size="small" danger disabled={selectedMediaPaths.length === 0} loading={busy === 'media-bulk'}>批量删除</Button>
        </Popconfirm>
      </Space>

      {/* 媒体列表 */}
      <Table<MediaItemDescriptor>
        size="small"
        loading={mediaLoading}
        virtual
        scroll={{ y: 340, x: 'max-content' }}
        rowKey="relativePath"
        dataSource={mediaItems}
        rowSelection={{ selectedRowKeys: selectedMediaPaths, onChange: (keys) => setSelectedMediaPaths(keys.map(String)) }}
        pagination={{
          current: mediaPage,
          pageSize: mediaPageSize,
          total: mediaTotal,
          size: 'small',
          showSizeChanger: true,
          pageSizeOptions: [20, 50, 100],
          showTotal: (total) => `共 ${total} 项`,
          onChange: (page, pageSize) => {
            setMediaPage(page);
            setMediaPageSize(pageSize);
            void refreshMedia(false, page, pageSize);
          }
        }}
        onRow={(row) => ({ onClick: () => setSelectedMedia(row) })}
        rowClassName={(row) => row.relativePath === selectedMedia?.relativePath ? 'camera-selected-row' : ''}
        columns={[
          { title: '预览', width: 70, render: (_: unknown, row) => <img className="media-thumbnail" src={media.thumbnail(row.relativePath)} loading="lazy" alt={row.name} /> },
          { title: '标签', dataIndex: 'label', width: 86, render: (value: string) => <Tag color={labelColor[value]}>{labelText[value] ?? value}</Tag> },
          { title: '图像', dataIndex: 'name', ellipsis: true },
          { title: '集合', dataIndex: 'collection', width: 110 },
          { title: '尺寸', width: 100, render: (_: unknown, row) => row.width && row.height ? `${row.width}×${row.height}` : '—' },
          { title: '大小', width: 85, render: (_: unknown, row) => formatBytes(row.bytes) },
          { title: '来源', width: 110, render: (_: unknown, row) => <span title={row.originPath ?? ''}>{row.sourceKind ?? '媒体库'}</span> },
          {
            title: '留证', width: 118, render: (_: unknown, row) => row.sha256
              ? <Tag color={row.duplicateOf ? 'gold' : 'green'} title={row.sha256}>{row.duplicateOf ? '重复内容' : row.sha256.slice(0, 10)}</Tag>
              : <Tag>待扫描</Tag>
          },
          {
            title: '', width: 72, render: (_: unknown, row) => (
              <Popconfirm
                title="确定删除此媒体项吗？"
                okText="删除"
                cancelText="取消"
                onConfirm={(event) => { event?.stopPropagation(); void deleteMedia(row); }}
              >
                <Button size="small" danger loading={busy === `delete:${row.relativePath}`} onClick={(event) => event.stopPropagation()}>删除</Button>
              </Popconfirm>
            )
          }
        ]}
      />

      {/* 高清原图预览 + 元信息 + 单图快捷操作 */}
      <div className="media-preview-panel">
        <div className="media-preview-title">
          <strong>{selectedMedia ? selectedMedia.name : '所选媒体'}</strong>
          <span>{selectedMedia?.source ?? '—'}</span>
        </div>
        {selectedMedia && selectedPreviewSrc ? (
          <>
            <img
              className="media-preview-image"
              src={selectedPreviewSrc}
              alt={selectedMedia.name}
              onError={() => { if (!previewFallback) setPreviewFallback(true); }}
              onClick={() => setLightboxOpen(true)}
              title="点击查看大图"
            />
            {previewFallback && (
              <Alert
                type="info"
                showIcon
                style={{ marginTop: 8 }}
                title="当前格式浏览器无法直接解码原图，已自动切换为归一化缩略图预览（可点击“下载原图”获取原始文件）。"
              />
            )}
            <div className="media-meta-strip">
              <span>尺寸：{selectedMedia.width && selectedMedia.height ? `${selectedMedia.width} × ${selectedMedia.height}` : '未索引'}</span>
              <span>大小：{formatBytes(selectedMedia.bytes)}</span>
              {selectedMedia.channels ? <span>通道/位深：{selectedMedia.channels}ch · {selectedMedia.bitDepth ?? '?'}bit</span> : null}
              <span>修改时间：{new Date(selectedMedia.modifiedAt).toLocaleString()}</span>
              {selectedMedia.sha256 ? <span title={selectedMedia.sha256}>SHA-256：{selectedMedia.sha256.slice(0, 12)}…</span> : null}
            </div>
            <Space wrap style={{ marginTop: 8 }}>
              <Tag color={labelColor[selectedMedia.label]}>{labelText[selectedMedia.label] ?? selectedMedia.label}</Tag>
              <Select
                aria-label="修改当前图像标签"
                size="small"
                style={{ width: 105 }}
                value={selectedMedia.label}
                disabled={busy === 'media-bulk'}
                onChange={(targetLabel) => void bulkUpdateMedia([selectedMedia.relativePath], 'relabel', { targetLabel })}
                options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))}
              />
              <Button size="small" onClick={() => setLightboxOpen(true)}>查看大图</Button>
              <Button size="small" href={media.preview(selectedMedia.relativePath)} download={selectedMedia.name}>下载原图</Button>
              <Button size="small" onClick={() => void copyText(selectedMedia.source, '已复制 media:// 引用')}>复制引用</Button>
              <Button size="small" onClick={() => { setRenameText(selectedMedia.name); setRenaming((v) => !v); }}>重命名</Button>
              <Button size="small" type="primary" ghost onClick={() => applyMediaAsFileCameraSource(selectedMedia.source)}>
                用作文件相机来源
              </Button>
            </Space>
            {renaming && (
              <Space style={{ marginTop: 8 }} wrap>
                <Input
                  aria-label="新文件名"
                  size="small"
                  style={{ width: 220 }}
                  value={renameText}
                  onChange={(event) => setRenameText(event.target.value)}
                  placeholder="输入包含扩展名的新文件名"
                />
                <Button
                  size="small"
                  type="primary"
                  loading={busy === 'media-bulk'}
                  disabled={!renameText.trim() || renameText.trim() === selectedMedia.name}
                  onClick={async () => {
                    const result = await bulkUpdateMedia([selectedMedia.relativePath], 'rename', { targetName: renameText.trim() });
                    if (result && result.succeeded > 0) setRenaming(false);
                  }}
                >
                  确认重命名
                </Button>
                <Button size="small" onClick={() => setRenaming(false)}>取消</Button>
              </Space>
            )}
          </>
        ) : (
          <div className="media-preview-empty">点击上方列表中的图像，即可查看高清原图与元信息</div>
        )}
      </div>

      {/* 折叠区块：导入上传 / 引用服务端目录（含在线浏览） / 清单与完整性维护 */}
      <Collapse
        className="media-sections-collapse"
        defaultActiveKey={['import', 'linked', 'integrity']}
        items={[
          {
            key: 'import',
            label: `导入图像与新建集合${pendingImportCount ? `（待导入 ${pendingImportCount} 项）` : ''}`,
            children: (
              <>
                <div className="media-create-row">
                  <Input value={newCollection} onChange={(e) => setNewCollection(e.target.value)} placeholder="新建集合名称" onPressEnter={createCollection} />
                  <Button loading={busy === 'create-collection'} onClick={createCollection}>创建集合</Button>
                </div>
                <div className="media-upload-grid">
                  <Select value={uploadCollection} onChange={setUploadCollection} options={collections.map((x) => ({ value: x.name, label: x.name }))} placeholder="选择目标集合" />
                  <Select value={uploadLabel} onChange={setUploadLabel} options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))} />
                </div>
                <Upload.Dragger
                  multiple
                  maxCount={maxBatchFiles}
                  accept=".bmp,.png,.jpg,.jpeg,.tif,.tiff"
                  fileList={uploadFiles}
                  beforeUpload={() => false}
                  onChange={({ fileList }) => chooseFiles(fileList)}
                  onRemove={(file) => { setUploadFiles((list) => list.filter((x) => x.uid !== file.uid)); return true; }}
                >
                  <p>将图像拖到此处，或点击选择文件</p>
                  <p className="ant-upload-hint">导入的图像将保存在服务端配置的媒体根目录中。</p>
                </Upload.Dragger>
                <input
                  {...directoryAttributes}
                  ref={directoryInputRef}
                  type="file"
                  multiple
                  accept=".bmp,.png,.jpg,.jpeg,.tif,.tiff"
                  style={{ display: 'none' }}
                  onChange={(event) => chooseDirectory(event.currentTarget.files)}
                />
                <Space style={{ marginTop: 8 }} wrap>
                  <Button onClick={() => directoryInputRef.current?.click()}>选择文件夹</Button>
                  <Button
                    type="primary"
                    disabled={!uploadCollection || pendingImportCount === 0}
                    loading={busy === 'media-import'}
                    onClick={() => void runImport()}
                  >
                    导入 {pendingImportCount || ''} 张图像
                  </Button>
                </Space>
                {selectionNotice && <div role="status" style={{ marginTop: 6, fontSize: 12, color: 'var(--vs-text-4)' }}>{selectionNotice}</div>}
                {importProgress && (
                  <Progress
                    percent={Math.round(importProgress.completed / Math.max(1, importProgress.total) * 100)}
                    status="active"
                    format={() => `${importProgress.completed}/${importProgress.total} · 批次 ${importProgress.batch}/${importProgress.totalBatches}`}
                    style={{ marginTop: 8 }}
                  />
                )}
              </>
            )
          },
          {
            key: 'linked',
            label: `引用服务端目录（免复制在线浏览 · ${linkedSources.length} 个）`,
            children: (
              <>
                <Alert
                  type="info"
                  showIcon
                  title="只保存目录引用，不复制图片"
                  description="目录必须位于服务器授权可读范围内。保存后可直接在此处分页浏览缩略图与原图，也可在 image.load 节点路径候选中直接选用。"
                />
                <div className="media-upload-grid media-linked-source-fields" style={{ marginTop: 8 }}>
                  <Input value={linkedDirectoryName} onChange={(event) => setLinkedDirectoryName(event.target.value)} placeholder="引用名称（如 train-ng）" />
                  <Input value={linkedDirectoryPath} onChange={(event) => setLinkedDirectoryPath(event.target.value)} placeholder="服务端绝对目录，例如 D:\Pictures\TRAIN" />
                </div>
                <Button
                  style={{ marginTop: 8 }}
                  loading={busy === 'media-link'}
                  disabled={!linkedDirectoryName.trim() || !linkedDirectoryPath.trim()}
                  onClick={() => void linkDirectory()}
                >
                  保存只读目录引用
                </Button>
                {linkedSources.map((source) => (
                  <div key={source.name} className={`media-linked-source-row${browsingLinkedName === source.name ? ' is-active' : ''}`}>
                    <div>
                      <strong>{source.name}</strong>
                      <span>{source.path}</span>
                      <small>
                        {source.itemCount} 张图像 · {formatBytes(source.totalBytes)}
                        {source.authorizedBy ? ` · 由 ${source.authorizedBy} 授权` : ''}
                      </small>
                    </div>
                    <Space size={6}>
                      {browseLinkedSource && (
                        <Button
                          size="small"
                          type={browsingLinkedName === source.name ? 'primary' : 'default'}
                          onClick={() => void browseLinkedSource(source.name, 1, linkedItemsPageSize, '')}
                        >
                          浏览图片
                        </Button>
                      )}
                      <Popconfirm
                        title="移除目录引用？原始文件不会删除。"
                        okText="移除"
                        cancelText="取消"
                        onConfirm={() => unlinkDirectory(source.name)}
                      >
                        <Button size="small" danger loading={busy === `media-unlink:${source.name}`}>移除引用</Button>
                      </Popconfirm>
                    </Space>
                  </div>
                ))}

                {browsingLinkedName && browseLinkedSource && (
                  <div className="media-linked-browser">
                    <div className="media-linked-browser-head">
                      <strong>服务端目录浏览：{browsingLinkedName}</strong>
                      <Space size={6}>
                        <Input
                          aria-label="搜索引用目录文件名"
                          size="small"
                          allowClear
                          style={{ width: 200 }}
                          placeholder="按文件名过滤"
                          value={linkedItemsSearch}
                          onChange={(event) => setLinkedItemsSearch?.(event.target.value)}
                          onPressEnter={() => void browseLinkedSource(browsingLinkedName, 1, linkedItemsPageSize, linkedItemsSearch)}
                        />
                        <Button size="small" loading={linkedItemsLoading} onClick={() => void browseLinkedSource(browsingLinkedName, 1, linkedItemsPageSize, linkedItemsSearch)}>
                          搜索
                        </Button>
                        <Button size="small" onClick={() => setBrowsingLinkedName?.(undefined)}>关闭</Button>
                      </Space>
                    </div>
                    <Table<MediaItemDescriptor>
                      size="small"
                      loading={linkedItemsLoading}
                      rowKey="relativePath"
                      dataSource={linkedItems}
                      scroll={{ y: 220, x: 'max-content' }}
                      pagination={{
                        current: linkedItemsPage,
                        pageSize: linkedItemsPageSize,
                        total: linkedItemsTotal,
                        size: 'small',
                        showTotal: (total) => `共 ${total} 张`,
                        onChange: (page, pageSize) => void browseLinkedSource(browsingLinkedName, page, pageSize, linkedItemsSearch)
                      }}
                      onRow={(row) => ({ onClick: () => setSelectedLinkedItem?.(row) })}
                      rowClassName={(row) => row.relativePath === selectedLinkedItem?.relativePath ? 'camera-selected-row' : ''}
                      columns={[
                        {
                          title: '预览', width: 68,
                          render: (_: unknown, row) => (
                            <img
                              className="media-thumbnail"
                              src={media.linkedSourceThumbnail(browsingLinkedName, row.relativePath)}
                              loading="lazy"
                              alt={row.name}
                            />
                          )
                        },
                        { title: '相对路径', dataIndex: 'relativePath', ellipsis: true },
                        { title: '大小', width: 85, render: (_: unknown, row) => formatBytes(row.bytes) },
                        {
                          title: '', width: 110,
                          render: (_: unknown, row) => (
                            <Button
                              size="small"
                              onClick={(event) => {
                                event.stopPropagation();
                                void copyText(row.source, '已复制服务端文件路径（可用于 image.load）');
                              }}
                            >
                              复制路径
                            </Button>
                          )
                        }
                      ]}
                    />
                    {selectedLinkedItem && linkedPreviewSrc && (
                      <div className="media-preview-panel">
                        <div className="media-preview-title">
                          <strong>{selectedLinkedItem.name}</strong>
                          <span>{selectedLinkedItem.source}</span>
                        </div>
                        <img
                          className="media-preview-image"
                          src={linkedPreviewSrc}
                          alt={selectedLinkedItem.name}
                          onError={() => { if (!linkedPreviewFallback) setLinkedPreviewFallback(true); }}
                        />
                        <Space wrap style={{ marginTop: 8 }}>
                          <Button size="small" onClick={() => void copyText(selectedLinkedItem.source, '已复制服务端文件路径（可用于 image.load）')}>
                            复制路径（用于 image.load）
                          </Button>
                          <Button size="small" href={media.linkedSourcePreview(browsingLinkedName, selectedLinkedItem.relativePath)} download={selectedLinkedItem.name}>
                            下载原图
                          </Button>
                        </Space>
                      </div>
                    )}
                  </div>
                )}
              </>
            )
          },
          {
            key: 'integrity',
            label: '清单留证与完整性维护',
            children: (
              <>
                <Space wrap style={{ marginBottom: 8 }}>
                  <Button loading={busy === 'media-manifest'} onClick={() => void downloadManifest()}>生成快照清单</Button>
                  <input
                    ref={manifestInputRef}
                    type="file"
                    accept=".json"
                    style={{ display: 'none' }}
                    onChange={(event) => {
                      const file = event.currentTarget.files?.[0];
                      if (file) void verifyManifest(file);
                      event.currentTarget.value = '';
                    }}
                  />
                  <Button loading={busy === 'media-manifest-verify'} onClick={() => manifestInputRef.current?.click()}>校验快照清单</Button>
                  <Button loading={busy === 'media-integrity'} onClick={() => void scanIntegrity()}>扫描完整性</Button>
                  {mediaIntegrity && mediaIntegrity.issues.length > 0 && (
                    <Button danger loading={busy === 'media-maintenance'} onClick={() => void repairIndex()}>修复索引并清理缩略图</Button>
                  )}
                </Space>
                {mediaIntegrity && (
                  <Alert
                    type={mediaIntegrity.issues.length ? 'warning' : 'success'}
                    showIcon
                    title={`媒体完整性：${mediaIntegrity.fileCount} 个文件，${mediaIntegrity.duplicateGroups} 组重复`}
                    description={mediaIntegrity.issues.length
                      ? mediaIntegrity.issues.slice(0, 4).map((issue) => `${issue.kind}: ${issue.path}`).join('；')
                      : '索引、文件和缩略图均一致。'}
                  />
                )}
              </>
            )
          }
        ]}
      />

      {/* 原图放大查看 Modal */}
      <Modal
        open={lightboxOpen && Boolean(selectedMedia && selectedPreviewSrc)}
        onCancel={() => setLightboxOpen(false)}
        footer={null}
        width="min(1080px, 92vw)"
        title={selectedMedia ? `${selectedMedia.name} · ${selectedMedia.source}` : '图像预览'}
      >
        {selectedMedia && selectedPreviewSrc && (
          <>
            <Space style={{ marginBottom: 10 }} wrap>
              <Segmented
                size="small"
                value={lightboxMode}
                onChange={(value) => setLightboxMode(value as 'fit' | 'actual')}
                options={[
                  { label: '适应窗口', value: 'fit' },
                  { label: '1:1 原尺寸', value: 'actual' }
                ]}
              />
              <Button size="small" href={media.preview(selectedMedia.relativePath)} download={selectedMedia.name}>下载原图</Button>
            </Space>
            <div className={`media-lightbox-stage ${lightboxMode === 'actual' ? 'is-actual' : ''}`}>
              <img
                src={selectedPreviewSrc}
                alt={selectedMedia.name}
                onError={() => { if (!previewFallback) setPreviewFallback(true); }}
              />
            </div>
          </>
        )}
      </Modal>
    </>
  );
}

