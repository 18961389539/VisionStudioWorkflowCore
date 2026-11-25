import { useState } from 'react';
import { Form, type message } from 'antd';
import type {
  CameraCommissioningProfile, CameraCommissioningSnapshot, CameraDescriptor,
  CameraFeatureDescriptor, CameraFeatureProfileRecord
} from '../types';
import { apiFetch } from '../apiClient';
import { apiUrl, cameras as cameraEndpoints, cameraFeatureProfiles } from '../endpoints';

type MessageApi = ReturnType<typeof message.useMessage>[0];

async function readCameraError(response: Response, fallback: string) {
  try {
    const data = await response.json();
    return data.detail ?? data.error ?? fallback;
  } catch {
    return fallback;
  }
}

export function useCameraCommissioning(options: {
  selected?: CameraDescriptor;
  setBusy: (busy?: string) => void;
  refreshCameras: (quiet?: boolean) => Promise<void>;
  messageApi: MessageApi;
}) {
  const { selected, setBusy, refreshCameras, messageApi } = options;
  const [commissioningForm] = Form.useForm<CameraCommissioningProfile>();
  const [commissioning, setCommissioning] = useState<CameraCommissioningSnapshot>();
  const [features, setFeatures] = useState<CameraFeatureDescriptor[]>([]);
  const [featureEdits, setFeatureEdits] = useState<Record<string, string>>({});
  const [featureProfiles, setFeatureProfiles] = useState<CameraFeatureProfileRecord[]>([]);
  const [selectedFeatureProfile, setSelectedFeatureProfile] = useState<string>();
  const [profileId, setProfileId] = useState('camera-profile-1');
  const [profileName, setProfileName] = useState('Commissioned Camera Profile');

  const clearCommissioning = () => {
    setCommissioning(undefined);
    setFeatures([]);
  };

  const refreshCommissioning = async (quiet = false) => {
    if (!selected || !['basler-pylon', 'hikrobot-mvs'].includes(selected.driver) || selected.state === 'Closed') {
      clearCommissioning();
      return;
    }
    try {
      const [snapshotResponse, featuresResponse] = await Promise.all([
        apiFetch(cameraEndpoints.commissioning(selected.id)),
        apiFetch(cameraEndpoints.features(selected.id))
      ]);
      if (!snapshotResponse.ok) throw new Error(await readCameraError(snapshotResponse, 'Commissioning profile unavailable'));
      if (!featuresResponse.ok) throw new Error(await readCameraError(featuresResponse, 'Feature browser unavailable'));
      const snapshot: CameraCommissioningSnapshot = await snapshotResponse.json();
      const featureList: CameraFeatureDescriptor[] = await featuresResponse.json();
      setCommissioning(snapshot);
      setFeatures(featureList);
      commissioningForm.setFieldsValue(snapshot.profile);
      setFeatureEdits(Object.fromEntries(featureList.map((x) => [x.key, x.value ?? ''])));
    } catch (error) {
      if (!quiet) messageApi.error(error instanceof Error ? error.message : 'Commissioning API unavailable');
    }
  };

  const refreshFeatureProfiles = async (quiet = false) => {
    if (!selected || !['basler-pylon', 'hikrobot-mvs'].includes(selected.driver)) {
      setFeatureProfiles([]);
      return;
    }
    try {
      const response = await apiFetch(cameraFeatureProfiles.byDriver(selected.driver));
      if (!response.ok) throw new Error(await readCameraError(response, 'Camera profiles unavailable'));
      const data: CameraFeatureProfileRecord[] = await response.json();
      setFeatureProfiles(data);
      if (selectedFeatureProfile && !data.some((x) => x.id === selectedFeatureProfile)) setSelectedFeatureProfile(undefined);
    } catch (error) {
      if (!quiet) messageApi.error(error instanceof Error ? error.message : 'Camera profiles unavailable');
    }
  };

  const applyCommissioning = async (values: CameraCommissioningProfile) => {
    if (!selected) return;
    setBusy(`${selected.id}:commissioning`);
    try {
      const payload: CameraCommissioningProfile = { ...values, schemaVersion: 1, acquisition: selected.settings };
      const response = await apiFetch(cameraEndpoints.commissioning(selected.id), {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
      });
      if (!response.ok) throw new Error(await readCameraError(response, 'Apply commissioning profile failed'));
      await refreshCommissioning(true);
      await refreshCameras(true);
      messageApi.success('Commissioning profile applied');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : 'Apply commissioning profile failed');
    } finally {
      setBusy(undefined);
    }
  };

  const setFeature = async (feature: CameraFeatureDescriptor) => {
    if (!selected) return;
    setBusy(`feature:${feature.key}`);
    try {
      const response = await apiFetch(cameraEndpoints.feature(selected.id, feature.key), {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ value: featureEdits[feature.key] ?? '' })
      });
      if (!response.ok) throw new Error(await readCameraError(response, `Update ${feature.key} failed`));
      await refreshCommissioning(true);
      messageApi.success(`${feature.key} updated`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : `Update ${feature.key} failed`);
    } finally {
      setBusy(undefined);
    }
  };

  const captureFeatureProfile = async () => {
    if (!selected || !profileId.trim() || !profileName.trim()) return;
    setBusy('profile-capture');
    try {
      const response = await apiFetch(apiUrl('camera-feature-profiles/capture'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ id: profileId.trim(), name: profileName.trim(), cameraId: selected.id })
      });
      if (!response.ok) throw new Error(await readCameraError(response, 'Save feature profile failed'));
      const saved: CameraFeatureProfileRecord = await response.json();
      setSelectedFeatureProfile(saved.id);
      await refreshFeatureProfiles(true);
      messageApi.success(`Saved ${saved.name}`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : 'Save feature profile failed');
    } finally {
      setBusy(undefined);
    }
  };

  const applySavedFeatureProfile = async () => {
    if (!selected || !selectedFeatureProfile) return;
    setBusy('profile-apply');
    try {
      const response = await apiFetch(cameraFeatureProfiles.apply(selectedFeatureProfile, selected.id), { method: 'POST' });
      if (!response.ok) throw new Error(await readCameraError(response, 'Apply saved profile failed'));
      await refreshCommissioning(true);
      await refreshCameras(true);
      messageApi.success('Saved profile applied');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : 'Apply saved profile failed');
    } finally {
      setBusy(undefined);
    }
  };

  const deleteFeatureProfile = async () => {
    if (!selectedFeatureProfile) return;
    setBusy('profile-delete');
    try {
      const response = await apiFetch(cameraFeatureProfiles.byId(selectedFeatureProfile), { method: 'DELETE' });
      if (!response.ok) throw new Error(await readCameraError(response, 'Delete profile failed'));
      setSelectedFeatureProfile(undefined);
      await refreshFeatureProfiles(true);
      messageApi.success('Profile deleted');
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : 'Delete profile failed');
    } finally {
      setBusy(undefined);
    }
  };

  const exportFeatureProfile = () => {
    const record = featureProfiles.find((x) => x.id === selectedFeatureProfile);
    if (!record) return;
    const blob = new Blob([JSON.stringify({ schema: 'visionstudio.camera-feature-profile', version: 1, ...record }, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `${record.id}.camera-profile.json`;
    anchor.click();
    URL.revokeObjectURL(url);
  };

  const importFeatureProfileFile = async (file: File) => {
    setBusy('profile-import');
    try {
      const parsed = JSON.parse(await file.text());
      const body = {
        id: parsed.id ?? file.name.replace(/\.camera-profile\.json$|\.json$/i, '').replace(/[^a-zA-Z0-9_.-]/g, '-'),
        name: parsed.name ?? parsed.id ?? 'Imported Camera Profile',
        driver: parsed.driver ?? selected?.driver,
        sourceCameraId: parsed.sourceCameraId ?? null,
        profile: parsed.profile ?? parsed
      };
      if (!body.driver) throw new Error('Imported profile does not declare a camera driver');
      const response = await apiFetch(apiUrl('camera-feature-profiles/import'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
      });
      if (!response.ok) throw new Error(await readCameraError(response, 'Import profile failed'));
      const saved: CameraFeatureProfileRecord = await response.json();
      setSelectedFeatureProfile(saved.id);
      await refreshFeatureProfiles(true);
      messageApi.success(`Imported ${saved.name}`);
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : 'Import profile failed');
    } finally {
      setBusy(undefined);
    }
  };

  return {
    commissioningForm,
    commissioning,
    features,
    featureEdits,
    setFeatureEdits,
    featureProfiles,
    selectedFeatureProfile,
    setSelectedFeatureProfile,
    profileId,
    setProfileId,
    profileName,
    setProfileName,
    clearCommissioning,
    refreshCommissioning,
    refreshFeatureProfiles,
    applyCommissioning,
    setFeature,
    captureFeatureProfile,
    applySavedFeatureProfile,
    deleteFeatureProfile,
    exportFeatureProfile,
    importFeatureProfileFile
  };
}
