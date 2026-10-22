import { useEffect, useState } from 'react';
import type { Edge, Node } from '@xyflow/react';
import { Button, Segmented, Select, Tag } from 'antd';
import type { RunResult } from '../types';
import { runImagePortLabel, useRunImages } from '../useRunImages';
import ImageViewer from './ImageViewer';

type Props = {
  nodes: Node[];
  edges: Edge[];
  selectedId: string;
  nodeLabels: Record<string, string>;
  result?: RunResult;
  stale: boolean;
  running: boolean;
  onClose: () => void;
};

export default function WorkflowImagePreview({ nodes, edges, selectedId, nodeLabels, result, stale, running, onClose }: Props) {
  const [pinnedId, setPinnedId] = useState<string>();
  // 删除固定的节点后立即跟随当前选择，避免固定入口指向不存在的节点。
  const pinned = Boolean(pinnedId && nodes.some((node) => node.id === pinnedId));
  const targetId = (pinned ? pinnedId : selectedId) ?? '';
  useEffect(() => {
    if (pinnedId && !pinned) setPinnedId(undefined);
  }, [pinnedId, pinned]);
  const { imageSource, setImageSource, imageViewerSource, viewerOverlays, selectedInputAvailable,
    selectedOutputAvailable, imageCatalogLoading, imageCatalogError, retryImageCatalog,
    imageOutputPorts, imageOutputPortName, setImageOutputPortName } = useRunImages({
    result, selectedId: targetId, nodes, edges, nodeLabels
  });

  return <aside className="workflow-image-preview" aria-label="图像预览">
    <div className="preview-heading">
      <strong>图像预览</strong>
      <Button size="small" type={pinned ? 'primary' : 'default'} disabled={!targetId}
        aria-pressed={pinned} title={pinned ? '取消固定，跟随画布选择' : '固定当前节点，切换画布选择时保持查看此节点'}
        onClick={() => setPinnedId(pinned ? undefined : targetId)}>{pinned ? '取消固定' : '固定节点'}</Button>
      <Button size="small" aria-label="关闭图像预览" onClick={onClose}>收起</Button>
    </div>
    <div className="preview-context">
      <span title={targetId}>{nodeLabels[targetId] ?? '选择节点查看图像'}</span>
      <small>{pinned ? '已固定节点' : '跟随画布选择'}</small>
    </div>
    <Segmented className="preview-source" block size="small" value={imageSource}
      onChange={(value) => setImageSource(value as typeof imageSource)} options={[
        { label: '最终预览', value: 'preview' },
        { label: '节点输入', value: 'input', disabled: !selectedInputAvailable },
        { label: '节点输出', value: 'output', disabled: !selectedOutputAvailable }
      ]} />
    {imageSource === 'output' && imageOutputPorts.length > 1 && <Select className="preview-source-port" size="small"
      aria-label="输出图像端口" value={imageOutputPortName} options={imageOutputPorts.map((entry) => ({ value: entry.portName, label: runImagePortLabel(nodes, targetId, entry.portName) }))}
      onChange={setImageOutputPortName} />}
    <div className="preview-provenance" aria-live="polite">
      <span>{imageViewerSource.label}</span>
      {stale && <Tag color="gold">当前流程尚未验证 · 上次结果</Tag>}
      <small title={result?.runId}>运行批次：{result?.runId ?? '尚未运行'}</small>
      {running && <small>{result ? '正在运行，当前显示上次结果…' : '正在运行，等待首张图像…'}</small>}
      {imageCatalogLoading && <small>正在加载节点图像…</small>}
      {imageCatalogError && <div className="preview-load-error" role="alert">
        <span>节点图像加载失败：{imageCatalogError}{imageViewerSource.url ? '，当前显示最终预览' : ''}</span>
        <Button size="small" onClick={retryImageCatalog}>重试加载</Button>
      </div>}
      {result && !imageCatalogLoading && !imageCatalogError && !selectedInputAvailable && !selectedOutputAvailable &&
        <small>此节点没有可用的输入、输出图像，可查看最终预览。</small>}
    </div>
    <div className="preview-viewer">
      <ImageViewer previewUrl={imageViewerSource.url} imageWidth={imageViewerSource.width}
        imageHeight={imageViewerSource.height} overlays={viewerOverlays} nodeLabels={nodeLabels}
        roiEnabled={false} showRoiTools={false} onRoiChange={() => {}}
        loading={imageCatalogLoading && !imageViewerSource.url}
        emptyHint={running ? '正在运行，等待图像…' : result ? '本次运行未产生可显示的图像，请检查运行结果。' : '运行流程后可查看最终预览及捕获的节点输入、输出图像'} />
    </div>
  </aside>;
}
