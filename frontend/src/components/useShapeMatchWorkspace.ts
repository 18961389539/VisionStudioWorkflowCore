/**
 * match.shape（形状匹配）工作区的状态、派生与处理器——按节点类型分块（2026-10-23）。
 * 从 NodeConfigurationDialog.tsx 拆出；行为与原内联实现完全一致。
 *
 * 边界约定：
 * - 本 hook 拥有训练/搜索工作流专属状态（工作区页签、训练样本、图像获取、模型训练）；
 * - 弹窗持有共享状态（preview/draft/图像来源/区域编辑模式等），以 setter 注入；
 * - runPreview/apply/locateRoi 留在弹窗（横切多种节点类型），通过本 hook 返回的
 *   状态与 setter 驱动形状分支。
 */
import { useState } from 'react';
import type { Node } from '@xyflow/react';
import type { NodeCatalogItem, RunResult } from '../types';
import type { NodeParameters } from '../nodeConfigurationState';
import { cloneParameters, parameterFingerprint } from '../nodeConfigurationState';

export type ShapePreviewState = { result: RunResult; fingerprint: string; receivedAt: string; preserveTrainingWorkspace?: boolean };

export type ShapeMatchContext = {
  node: Node;
  /** 弹窗打开时携带的运行结果（用于判断已有搜索预览） */
  result?: RunResult;
  isShapeMatch: boolean;
  locked: boolean;
  issues: readonly unknown[];
  externalChange: boolean;
  previewBlockedReason?: string;
  draft: NodeParameters;
  setDraft: (value: NodeParameters) => void;
  draftFingerprint: string;
  onPreview: (parameters: NodeParameters) => Promise<RunResult>;
  /** changedTrainingLabels 用目录参数显示名 */
  catalogItem: NodeCatalogItem;
  /** 模板输入上游节点（locateSavedTrainingImage 的预览定位） */
  templateSourceNodeId: string | undefined;
  /** 训练图像来源键：模板输入节点 ID 或模板路径 */
  trainingSourceKey: string;
  setPreview: (value: ShapePreviewState | undefined) => void;
  setPreviewBusy: (value: boolean) => void;
  setPreviewError: (value: string | undefined) => void;
  setResultExpanded: (value: boolean) => void;
  setPreviewNodeId: (value: string) => void;
  setImageSource: (value: 'preview' | 'output' | 'input') => void;
  setRegionEditMode: (value: 'model' | 'search' | 'clutter') => void;
  setFocusTick: (updater: (value: number) => number) => void;
};

export function useShapeMatchWorkspace(context: ShapeMatchContext) {
  const {
    node, result, isShapeMatch, locked, issues, externalChange, previewBlockedReason,
    draft, setDraft, draftFingerprint, onPreview, catalogItem,
    templateSourceNodeId, trainingSourceKey,
    setPreview, setPreviewBusy, setPreviewError, setResultExpanded,
    setPreviewNodeId, setImageSource, setRegionEditMode, setFocusTick
  } = context;

  const [shapeWorkspaceTab, setShapeWorkspaceTab] = useState<'training' | 'search'>(() => {
    const parameters = (node.data.parameters ?? {}) as NodeParameters;
    return parameters.modelMode === 'legacy' || typeof parameters.trainedModelData === 'string' && parameters.trainedModelData ? 'search' : 'training';
  });
  const existingSearchReport = result?.nodeReports.filter(report => report.nodeId === node.id).slice(-1)[0];
  const hasExistingSearch = Boolean(existingSearchReport && !existingSearchReport.summary.trainingMode
    && !existingSearchReport.summary.trainingSucceeded && !existingSearchReport.summary.trainingPreviewReady && !existingSearchReport.summary.searchPreviewReady);
  const [shapeImageKind, setShapeImageKind] = useState<'training' | 'search' | undefined>(hasExistingSearch ? 'search' : undefined);
  const [trainBusy, setTrainBusy] = useState(false);
  const [trainingImageBusy, setTrainingImageBusy] = useState(false);
  const [searchImageBusy, setSearchImageBusy] = useState(false);
  const [trainingView, setTrainingView] = useState(false);
  const [showSavedTrainingImage, setShowSavedTrainingImage] = useState(false);
  const [trainingImage, setTrainingImage] = useState<{ data: string; width: number; height: number; sourceKey: string; trained: boolean; receivedAt: string }>();
  const [trainingSamples, setTrainingSamples] = useState<{ data: string; width: number; height: number }[]>(() => {
    const parameters = (node.data.parameters ?? {}) as NodeParameters;
    const saved = parameters.trainedSourceImages;
    return Array.isArray(saved) && saved.length > 0
      ? saved.filter((item): item is { data: string; width: number; height: number } => Boolean(item && typeof item === 'object'
        && typeof (item as { data?: unknown }).data === 'string' && typeof (item as { width?: unknown }).width === 'number'
        && typeof (item as { height?: unknown }).height === 'number'))
      : [];
  });
  const [searchWorkspacePreview, setSearchWorkspacePreview] = useState<ShapePreviewState>();

  const trainedModelData = typeof draft.trainedModelData === 'string' ? draft.trainedModelData : '';
  const trainedSourceImage = typeof draft.trainedSourceImage === 'string' ? draft.trainedSourceImage : '';
  const trainedPreviewData = typeof draft.trainedPreviewImage === 'string' ? draft.trainedPreviewImage : '';
  const legacyWorkflow = isShapeMatch && draft.modelMode === 'legacy';
  const trainedSettings = draft.trainedModelSettings && typeof draft.trainedModelSettings === 'object'
    ? draft.trainedModelSettings as Record<string, unknown> : undefined;
  const currentTrainingSettings = {
    binarize: draft.binarize ?? 'auto', threshold: draft.threshold ?? 128, invert: draft.invert ?? false,
    trainingAngleRangeDeg: draft.trainingAngleRangeDeg ?? draft.angleRangeDeg ?? 180, angleStepDeg: draft.angleStepDeg ?? 1,
    pyramidLevels: draft.pyramidLevels ?? 0,
    scaleTolerancePercent: draft.scaleTolerancePercent ?? 0, scaleToleranceXPercent: draft.scaleToleranceXPercent ?? 0,
    scaleToleranceYPercent: draft.scaleToleranceYPercent ?? 0,
    scaleStepPercent: Number(draft.scaleTolerancePercent ?? 0) > 0 || Number(draft.scaleToleranceXPercent ?? 0) > 0 || Number(draft.scaleToleranceYPercent ?? 0) > 0 ? draft.scaleStepPercent ?? 1 : 0,
    trainingMinSampleFraction: draft.trainingMinSampleFraction ?? 0.75, trainingFeatureTolerancePx: draft.trainingFeatureTolerancePx ?? 2,
    ignorePolarity: draft.ignorePolarity ?? false, minContrast: draft.minContrast ?? 5,
    clutterContrast: draft.clutterRoi ? draft.clutterContrast ?? draft.minContrast ?? 5 : 0,
    trainingOriginMode: draft.trainingOriginMode ?? 'centroid', trainingOriginX: draft.trainingOriginX ?? 0,
    trainingOriginY: draft.trainingOriginY ?? 0, roi: draft.roi ?? null, clutterRoi: draft.clutterRoi ?? null,
    templatePath: draft.templatePath ?? ''
  };
  const trainingSettingsWithDefaults = trainedSettings && (() => {
    const normalized: Record<string, unknown> = { scaleTolerancePercent: 0, scaleToleranceXPercent: 0, scaleToleranceYPercent: 0, scaleStepPercent: 1,
      trainingMinSampleFraction: 0.75, trainingFeatureTolerancePx: 2, ignorePolarity: false, minContrast: 5, clutterContrast: 0,
      clutterRoi: null, pyramidLevels: 0, ...trainedSettings };
    if (normalized.trainingAngleRangeDeg === undefined) normalized.trainingAngleRangeDeg = normalized.angleRangeDeg ?? 180;
    delete normalized.angleRangeDeg;
    if (Number(normalized.scaleTolerancePercent) === 0 && Number(normalized.scaleToleranceXPercent) === 0 && Number(normalized.scaleToleranceYPercent) === 0) normalized.scaleStepPercent = 0;
    return normalized;
  })();
  const trainingStale = Boolean(trainedModelData && trainingSettingsWithDefaults && parameterFingerprint(trainingSettingsWithDefaults) !== parameterFingerprint(currentTrainingSettings));
  const shapeSearchBlocked = isShapeMatch && (!trainedModelData && !legacyWorkflow || trainingStale);
  const trainingBusy = trainBusy;
  const capturedTrainingImage = trainingImage?.sourceKey === trainingSourceKey ? trainingImage : undefined;
  const hasLosslessTrainingImage = Boolean(capturedTrainingImage?.data || trainedSourceImage || trainingSamples.length);
  const trainingSampleData = new Set(trainingSamples.map(sample => sample.data));
  const currentTrainingSampleData = capturedTrainingImage?.data || trainedSourceImage;
  const effectiveTrainingSampleCount = trainingSamples.length + (currentTrainingSampleData && !trainingSampleData.has(currentTrainingSampleData) ? 1 : 0);
  const changedTrainingLabels = trainingSettingsWithDefaults ? Object.keys(currentTrainingSettings)
    .filter(key => parameterFingerprint(trainingSettingsWithDefaults[key as keyof typeof trainingSettingsWithDefaults]) !== parameterFingerprint(currentTrainingSettings[key as keyof typeof currentTrainingSettings]))
    .map(key => catalogItem.parameters.find(parameter => parameter.name === key)?.label ?? (key === 'roi' ? '模型区域' : key)).join('、') : '';
  const showTrainedTemplatePreview = Boolean(showSavedTrainingImage && trainingView && isShapeMatch && (trainedSourceImage || trainedPreviewData));

  const fetchTrainingImage = async () => {
    if (locked || externalChange || previewBlockedReason) return;
    setShapeWorkspaceTab('training'); setShapeImageKind(undefined);
    setPreviewBusy(true); setTrainingImageBusy(true); setPreviewError(undefined); setTrainingView(true); setRegionEditMode('model'); setShowSavedTrainingImage(false);
    try {
      const submitted = cloneParameters({ ...draft, modelMode: 'trained', trainingMode: true, trainingPreviewOnly: true });
      const next = await onPreview(submitted);
      const report = next.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
      if (!next.success || !report?.success || report.summary.trainingPreviewReady !== true)
        throw new Error(report?.error || next.error || '无法获取训练图像，请连接 template 输入或填写模板路径。');
      if (typeof report.summary.trainingImageData === 'string') setTrainingImage({ data: report.summary.trainingImageData,
        width: Number(report.summary.trainingSourceWidth), height: Number(report.summary.trainingSourceHeight), sourceKey: trainingSourceKey, trained: false, receivedAt: new Date().toISOString() });
      const safeResult = { ...next, nodeReports: next.nodeReports.map(item => ({ ...item,
        summary: Object.fromEntries(Object.entries(item.summary).filter(([key]) => key !== 'trainingImageData')) })) };
      setPreview({ result: safeResult, fingerprint: draftFingerprint, receivedAt: new Date().toISOString(), preserveTrainingWorkspace: true });
      setShapeImageKind('training');
      setPreviewNodeId(node.id); setImageSource('output');
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '训练图像获取失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); setTrainingImageBusy(false); }
  };
  const addCurrentTrainingSample = () => {
    if (!capturedTrainingImage?.data || locked) return;
    const current = { data: capturedTrainingImage.data, width: capturedTrainingImage.width, height: capturedTrainingImage.height };
    const samples = trainingSamples.filter(sample => sample.data !== current.data);
    const reference = samples[0];
    if (reference && (reference.width !== current.width || reference.height !== current.height)) {
      setPreviewError('多样本训练要求图像尺寸一致。请采集与现有样本相同尺寸的图像。');
      return;
    }
    if (samples.length >= 4) {
      setPreviewError('最多可联合训练 4 张图像。');
      return;
    }
    if (samples.reduce((total, sample) => total + sample.data.length, current.data.length) > 32 * 1024 * 1024) {
      setPreviewError('样本图像总大小超过限制，请裁剪图像或减少样本数量。');
      return;
    }
    setTrainingSamples([...samples, current]);
    setPreviewError(undefined);
  };
  const fetchSearchImage = async () => {
    if (locked || externalChange || previewBlockedReason) return;
    setShapeWorkspaceTab('search'); setShapeImageKind(undefined);
    setPreviewBusy(true); setSearchImageBusy(true); setPreviewError(undefined); setTrainingView(false); setRegionEditMode('search'); setShowSavedTrainingImage(false);
    try {
      const submitted = cloneParameters({ ...draft, modelMode: 'trained', trainingMode: false, searchPreviewOnly: true });
      const next = await onPreview(submitted);
      const report = next.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
      if (!next.success || !report?.success || report.summary.searchPreviewReady !== true)
        throw new Error(report?.error || next.error || '无法获取场景图像，请确认形状匹配节点已连接 image 输入。');
      const scenePreview = { result: next, fingerprint: draftFingerprint, receivedAt: new Date().toISOString(), preserveTrainingWorkspace: true };
      setPreview(scenePreview); setSearchWorkspacePreview(scenePreview);
      setShapeImageKind('search');
      setPreviewNodeId(node.id); setImageSource('output');
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '场景图像获取失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); setSearchImageBusy(false); }
  };
  const trainShapeModel = async () => {
    if (locked || issues.length || externalChange || previewBlockedReason) return;
    if (!hasLosslessTrainingImage) {
      setPreviewError('请先获取训练图像，再训练模型。这样保存的模型才能关联原始无损图像。');
      return;
    }
    const currentImage = capturedTrainingImage?.data ? { data: capturedTrainingImage.data, width: capturedTrainingImage.width, height: capturedTrainingImage.height }
      : trainedSourceImage ? { data: trainedSourceImage, width: Number(draft.trainedSourceWidth ?? 0), height: Number(draft.trainedSourceHeight ?? 0) } : undefined;
    const sampleSet = [...trainingSamples];
    if (currentImage && !sampleSet.some(sample => sample.data === currentImage.data)) sampleSet.push(currentImage);
    if (!sampleSet.length) {
      setPreviewError('请先获取训练图像，再训练模型。这样保存的模型才能关联原始无损图像。');
      return;
    }
    if (sampleSet.length > 4 || sampleSet.some(sample => sample.width !== sampleSet[0].width || sample.height !== sampleSet[0].height)) {
      setPreviewError('多样本训练最多支持 4 张同尺寸图像。请清除不兼容样本后重试。');
      return;
    }
    const totalSourceLength = sampleSet.reduce((total, sample) => total + sample.data.length, 0);
    if (sampleSet.length > 1 && totalSourceLength > 32 * 1024 * 1024) {
      setPreviewError('样本图像总大小超过限制，请清除样本或裁剪训练图像。');
      return;
    }
    setShapeWorkspaceTab('training'); setShapeImageKind(undefined); setTrainingView(true);
    setShowSavedTrainingImage(false);
    setPreviewBusy(true); setTrainBusy(true); setPreviewError(undefined);
    try {
      const trainingRuns: { result: RunResult; summary: Record<string, unknown> }[] = [];
      for (let sampleIndex = 0; sampleIndex < sampleSet.length; sampleIndex++) {
        const sample = sampleSet[sampleIndex];
        const firstSample = trainingRuns[0]?.summary;
        const submitted = cloneParameters({ ...draft, modelMode: 'trained', trainingMode: true, trainingImageData: sample.data,
          ...(sampleIndex > 0 && firstSample ? {
            trainingReferenceModelData: String(firstSample.trainingModelData ?? ''),
            trainingReferenceOriginImageX: Number(firstSample.trainingOriginImageX ?? 0),
            trainingReferenceOriginImageY: Number(firstSample.trainingOriginImageY ?? 0)
          } : {}) });
        const next = await onPreview(submitted);
        const report = next.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
        const summary = report?.summary ?? {};
        if (!next.success || !report?.success || summary.trainingSucceeded !== true || typeof summary.trainingModelData !== 'string' || !summary.trainingModelData)
          throw new Error(report?.error || next.error || `第 ${trainingRuns.length + 1} 张样本训练失败，请检查图像和模型区域。`);
        trainingRuns.push({ result: next, summary });
      }
      const modelVariants = trainingRuns.map(run => ({ data: String(run.summary.trainingModelData), width: Number(run.summary.trainingWidth),
        height: Number(run.summary.trainingHeight), originX: Number(run.summary.trainingOriginX), originY: Number(run.summary.trainingOriginY),
        sourceOriginX: Number(run.summary.trainingOriginImageX), sourceOriginY: Number(run.summary.trainingOriginImageY) }));
      const combinedModelBytes = modelVariants.reduce((total, variant) => total + Math.floor(variant.data.length * 3 / 4), 0);
      if (combinedModelBytes > 16 * 1024 * 1024)
        throw new Error('多样本模型输入超过 16 MB 限制，请减少样本数量或裁剪模型区域。');
      let next = trainingRuns[trainingRuns.length - 1].result;
      let summary = trainingRuns[0].summary;
      let combinedModelData = modelVariants[0].data;
      if (modelVariants.length > 1) {
        const modelSetData = `shape-model-set-v1:${JSON.stringify({ version: 1, models: modelVariants })}`;
        if (new TextEncoder().encode(modelSetData).length > 24 * 1024 * 1024)
          throw new Error('多样本模型输入超过 24 MB 工作流请求限制，请减少样本数量或裁剪图像。');
        const combined = await onPreview(cloneParameters({ ...draft, modelMode: 'trained', trainingMode: true,
          trainingImageData: sampleSet[0].data, trainingCombineModelData: modelSetData,
          trainingCombineSourceImages: JSON.stringify(sampleSet.map(sample => sample.data)) }));
        const combinedReport = combined.nodeReports.filter((item) => item.nodeId === node.id).slice(-1)[0];
        const combinedSummary = combinedReport?.summary ?? {};
        if (!combined.success || !combinedReport?.success || combinedSummary.trainingSucceeded !== true
          || typeof combinedSummary.trainingModelData !== 'string' || !combinedSummary.trainingModelData)
          throw new Error(combinedReport?.error || combined.error || '复合模型训练失败，请检查样本对齐、模型区域和特征出现比例。');
        next = combined;
        summary = combinedSummary;
        combinedModelData = String(combinedSummary.trainingModelData);
      }
      if (new TextEncoder().encode(combinedModelData).length > 24 * 1024 * 1024)
        throw new Error('训练模型超过工作流资产大小限制，请减少样本数量或裁剪图像。');
      const snapshot = {
        binarize: draft.binarize ?? 'auto', threshold: draft.threshold ?? 128, invert: draft.invert ?? false,
        trainingAngleRangeDeg: draft.trainingAngleRangeDeg ?? draft.angleRangeDeg ?? 180, angleStepDeg: draft.angleStepDeg ?? 1,
        pyramidLevels: draft.pyramidLevels ?? 0,
        scaleTolerancePercent: draft.scaleTolerancePercent ?? 0, scaleToleranceXPercent: draft.scaleToleranceXPercent ?? 0,
        scaleToleranceYPercent: draft.scaleToleranceYPercent ?? 0, scaleStepPercent: draft.scaleStepPercent ?? 1,
        trainingMinSampleFraction: draft.trainingMinSampleFraction ?? 0.75, trainingFeatureTolerancePx: draft.trainingFeatureTolerancePx ?? 2,
        ignorePolarity: draft.ignorePolarity ?? false, minContrast: draft.minContrast ?? 5,
        clutterContrast: draft.clutterRoi ? draft.clutterContrast ?? draft.minContrast ?? 5 : 0,
        trainingOriginMode: draft.trainingOriginMode ?? 'centroid', trainingOriginX: draft.trainingOriginX ?? 0,
        trainingOriginY: draft.trainingOriginY ?? 0, roi: draft.roi ?? null, clutterRoi: draft.clutterRoi ?? null,
        templatePath: draft.templatePath ?? ''
      };
      const completed = { ...draft, modelMode: 'trained', trainingMode: false, trainedModelData: combinedModelData,
        trainedModelHash: String(summary.trainingModelHash ?? trainingRuns.map(run => String(run.summary.trainingModelHash ?? '')).filter(Boolean).join(':')),
        trainedModelConfig: String(summary.trainingConfig ?? ''), trainedModelSettings: snapshot,
        trainedSourceImage: sampleSet[0].data, trainedSourceImages: sampleSet.length > 1 ? sampleSet : undefined,
        trainedContour: Array.isArray(summary.trainingContour) ? summary.trainingContour : [],
        trainedContours: Array.isArray(summary.trainingContours) ? summary.trainingContours : [],
        trainedModelWarnings: [...(Array.isArray(summary.trainingWarnings) ? summary.trainingWarnings as string[] : []),
          ...trainingRuns.flatMap((run, index) => Array.isArray(run.summary.trainingWarnings)
            ? (run.summary.trainingWarnings as string[]).map(warning => `样本 ${index + 1}：${warning}`) : [])],
        trainedSampleCount: sampleSet.length,
        trainedFeaturePointCount: Number(summary.trainingFeaturePointCount ?? 0),
        trainedContourCount: Number(summary.trainingContourCount ?? 0),
        trainedSymmetryPeriodDeg: summary.trainingSymmetryPeriodDeg ?? null,
        trainedPreviewImage: typeof summary.trainingPreviewImage === 'string' ? summary.trainingPreviewImage : '',
        trainedSourceWidth: Number(summary.trainingSourceWidth ?? 0), trainedSourceHeight: Number(summary.trainingSourceHeight ?? 0),
        trainedWidth: Number(summary.trainingWidth ?? 0), trainedHeight: Number(summary.trainingHeight ?? 0),
        trainedOriginX: Number(summary.trainingOriginX ?? 0), trainedOriginY: Number(summary.trainingOriginY ?? 0),
        trainedOriginImageX: Number(summary.trainingOriginImageX ?? 0), trainedOriginImageY: Number(summary.trainingOriginImageY ?? 0),
        trainedPoseOffsetX: Number(summary.trainingPoseOffsetX ?? 0), trainedPoseOffsetY: Number(summary.trainingPoseOffsetY ?? 0) };
      setDraft(completed);
      setTrainingSamples(sampleSet.length > 1 ? sampleSet : []);
      if (capturedTrainingImage && !showTrainedTemplatePreview) setTrainingImage({ ...capturedTrainingImage, trained: true });
      setShapeImageKind('training');
      const safeResult = { ...next, nodeReports: next.nodeReports.map((item) => {
        if (item.nodeId !== node.id || !item.summary.trainingSucceeded) return item;
        const safeSummary = Object.fromEntries(Object.entries(item.summary).filter(([key]) => key !== 'trainingModelData' && key !== 'trainingPreviewImage'));
        return { ...item, summary: safeSummary };
      }) };
      setPreview({ result: safeResult, fingerprint: parameterFingerprint(completed), receivedAt: new Date().toISOString(), preserveTrainingWorkspace: true });
    } catch (error) {
      setPreviewError(error instanceof Error ? error.message : '形状模型训练失败。'); setResultExpanded(true);
    } finally { setPreviewBusy(false); setTrainBusy(false); }
  };
  const locateSavedTrainingImage = () => {
    setShapeWorkspaceTab('training'); setShapeImageKind('training'); setRegionEditMode('model');
    setTrainingView(true); setShowSavedTrainingImage(true); setPreviewNodeId(templateSourceNodeId ?? node.id); setImageSource('output'); setFocusTick((value) => value + 1);
  };
  const changeShapeWorkspaceTab = (key: string) => {
    if (locked) return;
    const next = key === 'search' ? 'search' : 'training';
    setShapeWorkspaceTab(next);
    setShapeImageKind(undefined);
    if (next === 'training') {
      setTrainingView(true); setRegionEditMode('model');
      if (capturedTrainingImage) { setShowSavedTrainingImage(false); setShapeImageKind('training'); }
      else if (trainedSourceImage || trainedPreviewData) locateSavedTrainingImage();
      else void fetchTrainingImage();
    } else {
      setTrainingView(false); setRegionEditMode('search'); setShowSavedTrainingImage(false);
      if (searchWorkspacePreview || hasExistingSearch) { setPreview(searchWorkspacePreview); setShapeImageKind('search'); setPreviewNodeId(node.id); setImageSource('output'); }
      else void fetchSearchImage();
    }
  };

  return {
    // 状态与 setter（弹窗的 runPreview / locateRoi / JSX 直接驱动）
    shapeWorkspaceTab, setShapeWorkspaceTab, changeShapeWorkspaceTab,
    shapeImageKind, setShapeImageKind,
    trainingView, setTrainingView,
    showSavedTrainingImage, setShowSavedTrainingImage,
    trainingSamples, setTrainingSamples,
    searchWorkspacePreview, setSearchWorkspacePreview,
    trainingBusy, trainingImageBusy, searchImageBusy,
    // 派生
    trainedModelData, trainedSourceImage, trainedPreviewData, legacyWorkflow, trainedSettings,
    trainingStale, shapeSearchBlocked, capturedTrainingImage, hasLosslessTrainingImage,
    trainingSampleData, effectiveTrainingSampleCount, changedTrainingLabels, showTrainedTemplatePreview,
    // 处理器
    fetchTrainingImage, addCurrentTrainingSample, fetchSearchImage, trainShapeModel, locateSavedTrainingImage
  };
}
