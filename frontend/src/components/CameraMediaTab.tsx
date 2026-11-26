import { useEffect, useRef, useState } from 'react';
import { Alert, Button, Input, InputNumber, Popconfirm, Progress, Select, Space, Table, Tag, Typography, Upload } from 'antd';
import type { UploadFile } from 'antd/es/upload/interface';
import type { InputHTMLAttributes } from 'react';
import type { MediaCollectionDescriptor, MediaItemDescriptor, MediaLibraryStatus, MediaLinkedSourceDescriptor, MediaIntegrityReport } from '../types';
import { media } from '../endpoints';
import { formatBytes, labelColor, labelText } from './cameraPanelShared';

const MEDIA_IMPORT_MAX_BATCH_FILES = 200;

export type CameraMediaTabProps = {
  busy?: string;
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
  bulkUpdateMedia: (
    paths: string[],
    action: 'delete' | 'move' | 'relabel',
    options: { targetCollection?: string; targetLabel?: string }
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
  setSelectedMedia: (item?: MediaItemDescriptor) => void;
  selectedMedia?: MediaItemDescriptor;
  /** 「用作文件相机来源」：把所选媒体的 media:// 引用写进文件相机注册表单。 */
  applyMediaAsFileCameraSource: (source: string) => void;
};

/** 「媒体库」标签页：集合/标签、拖拽导入、分页浏览与所选媒体预览。 */
export default function CameraMediaTab(props: CameraMediaTabProps) {
  const {
    busy, mediaStatus, newCollection, setNewCollection, createCollection,
    uploadCollection, setUploadCollection, collections, uploadLabel, setUploadLabel, uploadFiles,
    setUploadFiles, importMedia, importProgress, linkedSources,
    linkedDirectoryName, setLinkedDirectoryName, linkedDirectoryPath, setLinkedDirectoryPath,
    linkDirectory, unlinkDirectory, bulkUpdateMedia, mediaIntegrity,
    downloadManifest, verifyManifest, scanIntegrity, repairIndex,
    mediaSearch, setMediaSearch, mediaMinWidth, setMediaMinWidth, mediaMaxWidth, setMediaMaxWidth,
    mediaModifiedAfter, setMediaModifiedAfter, mediaModifiedBefore, setMediaModifiedBefore,
    deleteMedia, mediaCollection, setMediaCollection, mediaLabel, setMediaLabel,
    mediaItems, mediaPage, setMediaPage, mediaPageSize, setMediaPageSize, mediaTotal, refreshMedia,
    setSelectedMedia, selectedMedia, applyMediaAsFileCameraSource,
  } = props;
  const directoryInputRef = useRef<HTMLInputElement>(null);
  const manifestInputRef = useRef<HTMLInputElement>(null);
  const directoryFilesRef = useRef<File[]>([]);
  const [selectionNotice, setSelectionNotice] = useState<string>();
  const [directoryFileCount, setDirectoryFileCount] = useState(0);
  const [selectedMediaPaths, setSelectedMediaPaths] = useState<string[]>([]);
  const [bulkLabel, setBulkLabel] = useState('Unlabeled');
  const [bulkCollection, setBulkCollection] = useState('');
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
  const runBulk = async (
    action: 'delete' | 'move' | 'relabel',
    options: { targetCollection?: string; targetLabel?: string }) => {
    const result = await bulkUpdateMedia(selectedMediaPaths, action, options);
    if (result && result.failures.length === 0) setSelectedMediaPaths([]);
  };
  return (
    <>
<Typography.Title level={5}>媒体库</Typography.Title>
<Space wrap style={{ marginBottom: 8 }}>
  <Tag color="blue">{mediaStatus?.collectionCount ?? 0} 个集合</Tag><Tag>{mediaStatus?.itemCount ?? 0} 张图像</Tag><Tag color="red">{mediaStatus?.ngItemCount ?? 0} 个 NG</Tag><Tag>{formatBytes(mediaStatus?.totalBytes ?? 0)} / {formatBytes(mediaStatus?.maxLibraryBytes ?? 0)}</Tag>
</Space>
<div className="media-create-row"><Input value={newCollection} onChange={(e) => setNewCollection(e.target.value)} placeholder="新建集合" onPressEnter={createCollection} /><Button loading={busy === 'create-collection'} onClick={createCollection}>创建</Button></div>
<div className="media-upload-grid">
  <Select value={uploadCollection} onChange={setUploadCollection} options={collections.map((x) => ({ value: x.name, label: x.name }))} placeholder="选择集合" />
  <Select value={uploadLabel} onChange={setUploadLabel} options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))} />
</div>
<Upload.Dragger multiple maxCount={maxBatchFiles} accept=".bmp,.png,.jpg,.jpeg,.tif,.tiff" fileList={uploadFiles} beforeUpload={() => false} onChange={({ fileList }) => chooseFiles(fileList)} onRemove={(file) => { setUploadFiles((list) => list.filter((x) => x.uid !== file.uid)); return true; }}>
  <p>将图像拖到此处，或点击选择文件</p><p className="ant-upload-hint">导入的图像将保存在配置的媒体根目录中。</p>
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
<Button style={{ marginTop: 8 }} onClick={() => directoryInputRef.current?.click()}>选择文件夹</Button>
{selectionNotice && <div role="status" style={{ marginTop: 6, fontSize: 12, color: 'var(--vs-text-secondary)' }}>{selectionNotice}</div>}
{importProgress && <Progress
  percent={Math.round(importProgress.completed / Math.max(1, importProgress.total) * 100)}
  status="active"
  format={() => `${importProgress.completed}/${importProgress.total} · 批次 ${importProgress.batch}/${importProgress.totalBatches}`}
  style={{ marginTop: 8 }}
/>}
<Button type="primary" style={{ marginTop: 8 }} disabled={!uploadCollection || pendingImportCount === 0}
  loading={busy === 'media-import'} onClick={() => void runImport()}>
  导入 {pendingImportCount || ''} 张图像
</Button>
<Typography.Title level={5} style={{ marginTop: 20 }}>引用服务端目录</Typography.Title>
<Alert
  type="info"
  showIcon
  title="只保存目录引用，不复制图片"
  description="目录必须位于服务器可读范围内。保存后会出现在 image.load 的路径候选中，读取时仍按 index/sequence 逐张解码。"
/>
<div className="media-upload-grid media-linked-source-fields" style={{ marginTop: 8 }}>
  <Input value={linkedDirectoryName} onChange={(event) => setLinkedDirectoryName(event.target.value)} placeholder="引用名称" />
  <Input value={linkedDirectoryPath} onChange={(event) => setLinkedDirectoryPath(event.target.value)} placeholder="服务端绝对目录，例如 D:\Pictures\TRAIN" />
</div>
<Button style={{ marginTop: 8 }} loading={busy === 'media-link'} disabled={!linkedDirectoryName.trim() || !linkedDirectoryPath.trim()}
  onClick={() => void linkDirectory()}>保存只读目录引用</Button>
{linkedSources.map((source) => (
  <div key={source.name} className="media-linked-source-row">
    <div>
      <strong>{source.name}</strong>
      <span>{source.path}</span>
      <small>
        {source.itemCount} 张图像 · {formatBytes(source.totalBytes)}
        {source.authorizedBy ? ` · 由 ${source.authorizedBy} 授权` : ''}
      </small>
    </div>
    <Popconfirm title="移除目录引用？原始文件不会删除。" okText="移除" cancelText="取消"
      onConfirm={() => unlinkDirectory(source.name)}>
      <Button size="small" danger loading={busy === `media-unlink:${source.name}`}>移除引用</Button>
    </Popconfirm>
  </div>
))}
<Space wrap style={{ margin: '16px 0 8px' }}>
  <Button loading={busy === 'media-manifest'} onClick={() => void downloadManifest()}>生成快照清单</Button>
  <input ref={manifestInputRef} type="file" accept=".json" style={{ display: 'none' }}
    onChange={(event) => {
      const file = event.currentTarget.files?.[0];
      if (file) void verifyManifest(file);
      event.currentTarget.value = '';
    }} />
  <Button loading={busy === 'media-manifest-verify'} onClick={() => manifestInputRef.current?.click()}>校验快照清单</Button>
  <Button loading={busy === 'media-integrity'} onClick={() => void scanIntegrity()}>扫描完整性</Button>
  {mediaIntegrity && mediaIntegrity.issues.length > 0 &&
    <Button danger loading={busy === 'media-maintenance'} onClick={() => void repairIndex()}>修复索引并清理缩略图</Button>}
</Space>
{mediaIntegrity && <Alert
  type={mediaIntegrity.issues.length ? 'warning' : 'success'}
  showIcon
  title={`媒体完整性：${mediaIntegrity.fileCount} 个文件，${mediaIntegrity.duplicateGroups} 组重复`}
  description={mediaIntegrity.issues.length
    ? mediaIntegrity.issues.slice(0, 4).map(issue => `${issue.kind}: ${issue.path}`).join('；')
    : '索引、文件和缩略图均一致。'}
/>}
<div className="media-filter-row">
  <Input aria-label="搜索媒体文件名" allowClear placeholder="搜索文件名或路径" value={mediaSearch}
    onChange={(event) => setMediaSearch(event.target.value)} onPressEnter={() => { setMediaPage(1); void refreshMedia(false, 1, mediaPageSize); }} />
  <Select allowClear placeholder="全部集合" value={mediaCollection} onChange={setMediaCollection} options={collections.map((x) => ({ value: x.name, label: x.name }))} />
  <Select allowClear placeholder="全部标签" value={mediaLabel} onChange={setMediaLabel} options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))} />
  <InputNumber aria-label="最小宽度" min={1} max={100000} placeholder="最小宽度" value={mediaMinWidth}
    onChange={(value) => setMediaMinWidth(value == null ? undefined : Number(value))} />
  <InputNumber aria-label="最大宽度" min={1} max={100000} placeholder="最大宽度" value={mediaMaxWidth}
    onChange={(value) => setMediaMaxWidth(value == null ? undefined : Number(value))} />
  <Input aria-label="修改日期起点" type="date" value={mediaModifiedAfter} onChange={(event) => setMediaModifiedAfter(event.target.value)} />
  <Input aria-label="修改日期终点" type="date" value={mediaModifiedBefore} onChange={(event) => setMediaModifiedBefore(event.target.value)} />
  <Button onClick={() => { setMediaPage(1); void refreshMedia(false, 1, mediaPageSize); }}>扫描</Button>
</div>
<Space wrap style={{ marginBottom: 8 }}>
  <Tag>{selectedMediaPaths.length} 项已选</Tag>
  <Select aria-label="批量标签" style={{ width: 110 }} value={bulkLabel} onChange={setBulkLabel}
    options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))} />
  <Button size="small" disabled={selectedMediaPaths.length === 0} loading={busy === 'media-bulk'}
    onClick={() => void runBulk('relabel', { targetLabel: bulkLabel })}>改标签</Button>
  <Input aria-label="目标集合" style={{ width: 160 }} placeholder="目标集合" value={bulkCollection}
    onChange={(event) => setBulkCollection(event.target.value)} />
  <Button size="small" disabled={selectedMediaPaths.length === 0 || !bulkCollection.trim()} loading={busy === 'media-bulk'}
    onClick={() => void runBulk('move', { targetCollection: bulkCollection.trim() })}>移动</Button>
  <Popconfirm title={`删除选中的 ${selectedMediaPaths.length} 个媒体项？`} okText="删除" cancelText="取消"
    onConfirm={() => void runBulk('delete', {})}>
    <Button size="small" danger disabled={selectedMediaPaths.length === 0} loading={busy === 'media-bulk'}>批量删除</Button>
  </Popconfirm>
</Space>
<Table<MediaItemDescriptor> size="small" virtual scroll={{ y: 380, x: 'max-content' }} rowKey="relativePath" dataSource={mediaItems}
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
}} onRow={(row) => ({ onClick: () => setSelectedMedia(row) })} rowClassName={(row) => row.relativePath === selectedMedia?.relativePath ? 'camera-selected-row' : ''} columns={[
  { title: '预览', width: 70, render: (_: unknown, row) => <img className="media-thumbnail" src={media.thumbnail(row.relativePath)} loading="lazy" alt={row.name} /> },
  { title: '标签', dataIndex: 'label', width: 86, render: (value: string) => <Tag color={labelColor[value]}>{labelText[value] ?? value}</Tag> },
  { title: '图像', dataIndex: 'name', ellipsis: true }, { title: '集合', dataIndex: 'collection', width: 110 },
  { title: '尺寸', width: 100, render: (_: unknown, row) => row.width && row.height ? `${row.width}×${row.height}` : '—' },
  { title: '大小', width: 80, render: (_: unknown, row) => formatBytes(row.bytes) },
  { title: '来源', width: 130, render: (_: unknown, row) => <span title={row.originPath ?? ''}>{row.sourceKind ?? '旧媒体'}</span> },
  { title: '留证', width: 120, render: (_: unknown, row) => row.sha256
    ? <Tag color={row.duplicateOf ? 'gold' : 'green'} title={row.sha256}>{row.duplicateOf ? '重复内容' : row.sha256.slice(0, 10)}</Tag>
    : <Tag>待扫描</Tag> },
  { title: '', width: 70, render: (_: unknown, row) => <Popconfirm title="确定删除此媒体项吗？" okText="删除" cancelText="取消" onConfirm={() => deleteMedia(row)}><Button size="small" danger loading={busy === `delete:${row.relativePath}`}>删除</Button></Popconfirm> }
]} />

{/* 所选媒体预览：与媒体库同属一个任务上下文 */}
<div className="media-preview-panel">
  <div className="media-preview-title"><strong>所选媒体</strong><span>{selectedMedia?.source ?? '—'}</span></div>
  {selectedMedia ? <img className="media-preview-image" src={media.thumbnail(selectedMedia.relativePath)} alt={selectedMedia.name} /> : <div className="media-preview-empty">选择最近样本或 NG 样本进行预览</div>}
  {selectedMedia && <Space wrap><Tag color={labelColor[selectedMedia.label]}>{labelText[selectedMedia.label] ?? selectedMedia.label}</Tag><Button size="small" onClick={() => applyMediaAsFileCameraSource(selectedMedia.source)}>用作文件相机来源</Button></Space>}
</div>
    </>
  );
}
