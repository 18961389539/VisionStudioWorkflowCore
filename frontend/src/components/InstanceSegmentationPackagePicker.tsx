import { useEffect, useState } from 'react';
import { Alert, Button, Modal, Space, Spin } from 'antd';
import { apiJson } from '../apiClient';
import { modelPackages } from '../endpoints';
import type { components } from '../generated/api-types';
import { useI18n } from '../i18n/provider';

type PackageChoice = Pick<components['schemas']['ModelPackageSummary'], 'id' | 'name' | 'kind' | 'path'>;
type Props = { value: string; onChange: (path: string) => void; disabled?: boolean };

function instancePackages(value: unknown) {
  if (!Array.isArray(value)) throw new Error('The model package service did not return a list.');
  return value.filter((item): item is PackageChoice => item?.kind === 'InstanceSegmentation'
    && typeof item.id === 'string' && typeof item.name === 'string' && typeof item.path === 'string' && Boolean(item.path.trim()));
}

export default function InstanceSegmentationPackagePicker({ value, onChange, disabled }: Props) {
  const { lang } = useI18n();
  const zh = lang === 'zh';
  const [open, setOpen] = useState(false);
  const [retry, setRetry] = useState(0);
  const [packages, setPackages] = useState<PackageChoice[]>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    if (!open) return;
    const request = new AbortController();
    void apiJson<unknown>(modelPackages.list, { signal: request.signal, timeoutMs: 15_000,
      errorLabel: zh ? '读取训练包失败' : 'Could not load model packages' })
      .then(response => { if (!request.signal.aborted) setPackages(instancePackages(response)); })
      .catch((reason: unknown) => {
        if (!request.signal.aborted) setError(reason instanceof Error ? reason.message : String(reason));
      });
    return () => request.abort();
  }, [open, retry, zh]);

  const resetList = () => { setPackages(undefined); setError(undefined); };
  return <>
    <Button size="small" disabled={disabled} onClick={() => { resetList(); setOpen(true); }}>
      {zh ? '选择已导入的实例分割包' : 'Choose an imported instance segmentation package'}
    </Button>
    <Modal open={open} onCancel={() => setOpen(false)} footer={null}
      title={zh ? '实例分割模型包' : 'Instance segmentation model packages'}>
      {error ? <Alert type="error" showIcon title={error}
        action={<Button size="small" onClick={() => { resetList(); setRetry(count => count + 1); }}>{zh ? '重试' : 'Retry'}</Button>} />
        : !packages ? <Spin aria-label={zh ? '正在读取训练包' : 'Loading model packages'} />
        : packages.length === 0 ? <p>{zh ? '请先在「模型包」中导入实例分割包，或直接填写 ONNX 模型路径。'
          : 'Import an instance segmentation package from Model Packages, or enter an ONNX model path.'}</p>
        : <Space orientation="vertical" size="small" style={{ width: '100%' }}>
          {packages.map(model => <Button key={model.id} block disabled={disabled} aria-pressed={model.path === value}
            onClick={() => { onChange(model.path); setOpen(false); }}>
            {model.name} <span className="node-info-muted">{model.id}</span>
          </Button>)}
        </Space>}
    </Modal>
  </>;
}
