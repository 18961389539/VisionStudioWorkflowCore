import { Button, Input, Popconfirm, Select, Space, Table, Tag, Typography, Upload } from 'antd';
import type { UploadFile } from 'antd/es/upload/interface';
import type { MediaCollectionDescriptor, MediaItemDescriptor, MediaLibraryStatus } from '../types';
import { media } from '../endpoints';
import { formatBytes, labelColor, labelText } from './cameraPanelShared';

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
  importMedia: () => void | Promise<void>;
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
    setUploadFiles, importMedia, deleteMedia, mediaCollection, setMediaCollection, mediaLabel, setMediaLabel,
    mediaItems, mediaPage, setMediaPage, mediaPageSize, setMediaPageSize, mediaTotal, refreshMedia,
    setSelectedMedia, selectedMedia, applyMediaAsFileCameraSource,
  } = props;
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
<Upload.Dragger multiple accept=".bmp,.png,.jpg,.jpeg,.tif,.tiff" fileList={uploadFiles} beforeUpload={() => false} onChange={({ fileList }) => setUploadFiles(fileList)} onRemove={(file) => { setUploadFiles((list) => list.filter((x) => x.uid !== file.uid)); return true; }}>
  <p>将图像拖到此处，或点击选择文件</p><p className="ant-upload-hint">导入的图像将保存在配置的媒体根目录中。</p>
</Upload.Dragger>
<Button type="primary" style={{ marginTop: 8 }} disabled={!uploadCollection || uploadFiles.length === 0} loading={busy === 'media-import'} onClick={importMedia}>导入 {uploadFiles.length || ''} 张图像</Button>
<div className="media-filter-row">
  <Select allowClear placeholder="全部集合" value={mediaCollection} onChange={setMediaCollection} options={collections.map((x) => ({ value: x.name, label: x.name }))} />
  <Select allowClear placeholder="全部标签" value={mediaLabel} onChange={setMediaLabel} options={['Unlabeled', 'OK', 'NG', 'Review'].map((x) => ({ value: x, label: labelText[x] }))} />
  <Button onClick={() => { setMediaPage(1); void refreshMedia(false, 1, mediaPageSize); }}>扫描</Button>
</div>
<Table<MediaItemDescriptor> size="small" rowKey="relativePath" dataSource={mediaItems} pagination={{
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
  { title: '标签', dataIndex: 'label', width: 86, render: (value: string) => <Tag color={labelColor[value]}>{labelText[value] ?? value}</Tag> },
  { title: '图像', dataIndex: 'name', ellipsis: true }, { title: '集合', dataIndex: 'collection', width: 110 }, { title: '大小', width: 80, render: (_: unknown, row) => formatBytes(row.bytes) },
  { title: '', width: 70, render: (_: unknown, row) => <Popconfirm title="确定删除此媒体项吗？" okText="删除" cancelText="取消" onConfirm={() => deleteMedia(row)}><Button size="small" danger loading={busy === `delete:${row.relativePath}`}>删除</Button></Popconfirm> }
]} />

{/* 所选媒体预览：与媒体库同属一个任务上下文 */}
<div className="media-preview-panel">
  <div className="media-preview-title"><strong>所选媒体</strong><span>{selectedMedia?.source ?? '—'}</span></div>
  {selectedMedia ? <img className="media-preview-image" src={media.preview(selectedMedia.relativePath)} alt={selectedMedia.name} /> : <div className="media-preview-empty">选择最近样本或 NG 样本进行预览</div>}
  {selectedMedia && <Space wrap><Tag color={labelColor[selectedMedia.label]}>{labelText[selectedMedia.label] ?? selectedMedia.label}</Tag><Button size="small" onClick={() => applyMediaAsFileCameraSource(selectedMedia.source)}>用作文件相机来源</Button></Space>}
</div>
    </>
  );
}
