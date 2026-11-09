import { ApiError, apiFetch, apiJson } from './apiClient';
import { modelAssets as modelAssetEndpoints } from './endpoints';

/**
 * 模型资产客户端（P0 M3）。
 *
 * 只封装工作区需要的最小面：创建资产、追加版本、按版本取回字节。
 * 字节以 base64 往返（后端 P0 契约），16 MB 上限与服务端一致；
 * blob 读取做进程内缓存，避免每次打开配置页都重新拉十几 MB。
 */
export interface ShapeModelVersionInfo {
  version: number;
  sha256: string;
  sizeBytes: number;
  trainingConfig: string;
  sourceImageSha256: string;
  note: string;
  createdAt: string;
  createdBy: string;
}

export interface ShapeModelAssetDescriptor {
  id: string;
  name: string;
  description: string;
  createdAt: string;
  updatedAt: string;
  latestVersion: number;
  versions: ShapeModelVersionInfo[];
}

export interface CreateShapeModelAssetBody {
  id?: string;
  name: string;
  description?: string;
  modelBase64: string;
  trainingConfig: string;
  sourceImageSha256?: string;
  note?: string;
  createdBy?: string;
}

export interface AppendShapeModelVersionBody {
  modelBase64: string;
  trainingConfig: string;
  sourceImageSha256?: string;
  note?: string;
  createdBy?: string;
}

const jsonHeaders = { 'Content-Type': 'application/json' };

export const createShapeModelAsset = (body: CreateShapeModelAssetBody) =>
  apiJson<ShapeModelAssetDescriptor>(modelAssetEndpoints.create, {
    method: 'POST', headers: jsonHeaders, body: JSON.stringify(body), errorLabel: '保存模型资产失败'
  });

export const appendShapeModelVersion = (assetId: string, body: AppendShapeModelVersionBody) =>
  apiJson<ShapeModelVersionInfo>(modelAssetEndpoints.versions(assetId), {
    method: 'POST', headers: jsonHeaders, body: JSON.stringify(body), errorLabel: '追加模型资产版本失败'
  });

/** Uint8Array → base64，分块避免大模型撑爆 String.fromCharCode 的参数栈。 */
export function bytesToBase64(bytes: Uint8Array): string {
  const chunk = 0x8000;
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += chunk)
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunk));
  return btoa(binary);
}

const blobCache = new Map<string, string>();

/** 清空缓存（测试或资产被替换时使用）。 */
export function clearShapeModelAssetCache(): void {
  blobCache.clear();
}

/** 取回指定版本的模型字节并转成 base64；`version<=0` 表示 latest（由服务端解析）。 */
export async function fetchShapeModelBase64(assetId: string, version: number): Promise<string> {
  const key = `${assetId}@${version > 0 ? version : 'latest'}`;
  const cached = blobCache.get(key);
  if (cached) return cached;

  const response = await apiFetch(modelAssetEndpoints.blob(assetId, version > 0 ? version : 0));
  if (!response.ok) {
    throw new ApiError(`读取模型资产 '${assetId}' v${version > 0 ? version : 'latest'} 失败（HTTP ${response.status}）`, response.status);
  }
  const base64 = bytesToBase64(new Uint8Array(await response.arrayBuffer()));
  blobCache.set(key, base64);
  return base64;
}
