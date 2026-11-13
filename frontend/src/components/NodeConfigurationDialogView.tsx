/**
 * NodeConfigurationDialog 的渲染层（巨型组件拆分，2026-11-13）。
 *
 * 纯展示：状态与派生值来自 ./useNodeConfigurationDialogModel，交互动作由弹窗外壳
 * （NodeConfigurationDialog）注入。文案、data-testid 与 ARIA 名称与拆分前保持一致。
 */
import type { CSSProperties } from 'react';
import type { Node } from '@xyflow/react';
import { Alert, Button, Collapse, InputNumber, Segmented, Select, Space, Tag, Tooltip } from 'antd';
import type { VisionRoi, PortDescriptor } from '../types';
import NodeDescription from './NodeDescription';
import MetrologyObjectEditor from './MetrologyObjectEditor';
import ImageViewer from './ImageViewer';
import PropertyPanel from './PropertyPanel';
import MatchingAutoTunePanel from './MatchingAutoTunePanel';
import CaliperCircleQuickSettings from './CaliperCircleQuickSettings';
import CaliperCircleDiagnosticsPanel from './CaliperCircleDiagnosticsPanel';
import CaliperEdgeQuickSettings from './CaliperEdgeQuickSettings';
import CaliperEdgeDiagnosticsPanel from './CaliperEdgeDiagnosticsPanel';
import SplitHandle from './SplitHandle';
import SwitchCaseEditor from './SwitchCaseEditor';
import { runImagePortLabel } from '../useRunImages';
import { cloneParameters } from '../nodeConfigurationState';
import { withSwitchPorts } from '../switchPorts';
import { canonicalParameterGroup, portDisplayName } from '../i18n/catalog';
import { parameterMeta } from './nodeConfigurationGeometry';
import { parameterVisibility } from './nodeConfigurationDialogModel';
import type { useNodeConfigurationDialogModel } from './useNodeConfigurationDialogModel';

export type NodeConfigurationActions = {
  changeParameter: (key: string, value: unknown) => void;
  changeCropRoi: (roi?: VisionRoi) => void;
  changePerspectiveQuad: (roi?: VisionRoi) => void;
  runPreview: () => Promise<void>;
  loadMetrologyImage: () => Promise<void>;
  captureRoiReference: () => Promise<void>;
  saveAsAsset: () => Promise<void>;
  clearCropSelection: () => void;
  undoCropSelectionClear: () => void;
  locateRoi: (key?: string) => void;
};

export type NodeConfigurationView = ReturnType<typeof useNodeConfigurationDialogModel> & NodeConfigurationActions;

function PortTable({ ports, input }: { ports: PortDescriptor[]; input?: boolean }) {
  if (!ports.length) return <p className="node-info-muted">无{input ? '输入' : '输出'}端口</p>;
  return <div className="node-info-table-wrap"><table className="node-info-table">
    <thead><tr><th>端口</th><th>数据类型</th>{input && <th>连接要求</th>}</tr></thead>
    <tbody>{ports.map((port) => <tr key={port.name}>
      <td title={port.label && port.label !== port.name ? port.name : undefined}>{portDisplayName(port.name, port.label)}</td>
      <td>{port.dataType}</td>
      {input && <td>{port.required ? '必需' : port.requiredWhen ? '所选操作需要时必需' : '可选'}</td>}
    </tr>)}</tbody>
  </table></div>;
}

export function NodeConfigurationDetails({ model }: { model: NodeConfigurationView }) {
  const { catalogItem, draft, hasOverlayPort } = model;
  return <div className="node-info">
    <NodeDescription text={catalogItem.description ?? undefined} />
    <h3>输入端口</h3><PortTable ports={catalogItem.inputs} input />
    <h3>输出端口</h3><PortTable ports={withSwitchPorts(catalogItem, draft).outputs} />
    {catalogItem.parameters.some((parameter) => !parameter.internal) && <>
      <h3>参数说明</h3>
      <dl className="node-info-parameters">{catalogItem.parameters.filter((parameter) => !parameter.internal).map((parameter) => {
        // P1（评审）：说明行补充“默认值 · 范围 · 步进”，描述缺失时不再重复兜底句
        const meta = parameterMeta(parameter);
        return <div key={parameter.name}>
          <dt>{parameter.label}{parameter.unit ? `（${parameter.unit}）` : ''}</dt>
          <dd>
            {parameter.description && <span>{parameter.description}</span>}
            {meta && <span className="node-info-param-meta">{meta}</span>}
            {parameter.options?.length ? <span>可选：{parameter.options.map((option) => option.label).join('、')}</span> : null}
          </dd>
        </div>;
      })}</dl>
    </>}
    <Collapse size="small" items={[{ key: 'technical', label: '技术信息', children: <div className="node-info-technical">
      <span>节点类型：<code>{catalogItem.type}</code></span>
      <span>来源：{catalogItem.pluginId ?? 'builtin'}</span>
      <div>
        {catalogItem.capabilities?.supportsRoi && <Tag>支持 ROI</Tag>}
        {hasOverlayPort && <Tag>overlay 图像端口</Tag>}
        {catalogItem.capabilities?.emitsOverlay && !hasOverlayPort && <Tag>可视化叠加</Tag>}
        {catalogItem.capabilities?.deterministic !== undefined && <Tag>{catalogItem.capabilities.deterministic ? '确定性执行' : '非确定性执行'}</Tag>}
      </div>
    </div> }]} />
  </div>;
}

export function NodeConfigurationWorkspace({ model, shapeMode }: { model: NodeConfigurationView; shapeMode?: 'training' | 'search' }) {
  const {
    node, catalogItem, nodes, previewBlockedReason, circleSamplingNotice,
    circleFitConditionNotice, caliperCircleBoundsNotice, caliperLineBoundsNotice, caliperSearchLineBoundsNotice, baseline, imageSource,
    setImageSource, selectedOutputAvailable, selectedInputAvailable, imageCatalogLoading, imageCatalogError, retryImageCatalog,
    imageOutputPorts, imageOutputPortName, setImageOutputPortName, changeShapeWorkspaceTab, trainingSamples, setTrainingSamples,
    trainingImageBusy, searchImageBusy, trainedModelData, trainedSourceImage, trainedPreviewData, legacyWorkflow,
    trainedSettings, trainingStale, shapeSearchBlocked, capturedTrainingImage, hasLosslessTrainingImage, effectiveTrainingSampleCount,
    changedTrainingLabels, showTrainedTemplatePreview, fetchTrainingImage, addCurrentTrainingSample, fetchSearchImage, locateSavedTrainingImage,
    imageShare, workspaceWidth, columns, previewShare, commitShare,
    bindWorkspace, nodeDuration, draft, setDraft, assetBusy, assetMessage,
    previewNodeId, setPreviewNodeId, previewBusy, caliperDiagnosticMode, setCaliperDiagnosticMode, regionEditMode,
    roiImageView, setRoiImageView, measurementImageView, setMeasurementImageView, focusTick,
    dirty, changedCount, externalChange, locked, roiInputEdge, directCropMaskSource,
    croppedImageNeedsRoiWarning, connectedInputPorts, modelAssetId, modelAssetVersion, isImageCrop, isPerspective,
    isRoiTransform, isCaliperCircle, isCaliperEdge, isMetrology, connectedRoiImageFrameMismatch, connectedRoiImageFrameMismatchMessage,
    templateSourceNodeId, roiInputConnected, roiMaskInputConnected, cropRoiSourceId, cropRoiInputConnected, legacyCropBoundsPresent,
    cropClearUndoAvailable, measurementFitUndoAvailable, searchLineInputEdge, circleInputEdge, transformInputEdge, directCorrectedCircleTransformConflict,
    correctedCircleTransformConflict, correctedSearchLineTransformConflict, connectedCircleImageFrameMismatch, connectedSearchLineImageFrameMismatch, connectedCircleTransformSourceMismatch, connectedSearchLineTransformSourceMismatch,
    measurementGeometryConflict, correctedCircleTransformMessage, correctedSearchLineTransformMessage, circleImageFrameMismatchMessage, searchLineImageFrameMismatchMessage, circleTransformSourceMismatchMessage,
    searchLineTransformSourceMismatchMessage, cropMeasurementReferenceNodeId, cropMeasurementReferencePortName, debugImageConnected, poseEdge, poseSource,
    referenceImage, referenceMismatch, displayedResult, receivedAt, stale, showCaliperDiagnostics,
    caliperDiagnostics, edgeDiagnostics, edgeDiagnosticKey, edgeInspectionPosition, inspectEdgePosition, matchReport,
    matchStale, matchNumber, nodeLabels, cropRoiForImage, issues, scriptPreviewReport,
    scriptPreviewBlockedReason, measurementCurrentImageAvailable, connectedRoiPreviewGeometry, showConnectedRoiPreview, measurementGeometryFrameKind, connectedCircle,
    circleSourceRoiType, circleInputIsBoundingApproximation, connectedSearchLine, canDisplayConnectedSearchLine, canEditMeasurementGeometry, measurementImageFrameReady,
    measurementImageFrameUnavailable, measurementGeometry, measurementTransformInvalid, measurementTransformSourceNotice, showCurrentMeasurementImage, viewerMeasurementGeometry,
    currentMeasurementGeometryUnavailable, transformedBoundsPreflightUnavailable, updateMeasurementGeometry, fitMeasurementGeometryToImage, undoMeasurementGeometryFit, imageInputSourceId,
    roiSourceNode, resultOverlaysForViewer, showCapturedTrainingImage, showMeasurementReferenceImage, diagnosticImageMatches, edgeDiagnosticGeometry,
    diagnosticOverlays, diagnosticMarkers, displayImageSource, overlaysWithTrainedContour, showImage, canEditRoi,
    canLocateRoiInImage, changeParameter, changeCropRoi, changePerspectiveQuad, runPreview, loadMetrologyImage,
    captureRoiReference, saveAsAsset, clearCropSelection, undoCropSelectionClear, locateRoi,
  } = model;
  const draftNode: Node = { ...node, data: { ...node.data, parameters: draft } };
  const perspectiveRoi = (() => {
    const defaults = [0, 0, 100, 0, 100, 100, 0, 100];
    const values = ['x1', 'y1', 'x2', 'y2', 'x3', 'y3', 'x4', 'y4']
      .map((key, index) => Number(draft[key] ?? defaults[index]));
    if (values.some((value) => !Number.isFinite(value))) return undefined;
    return {
      type: 'Polygon' as const,
      points: [0, 2, 4, 6].map((index) => ({ x: values[index], y: values[index + 1] }))
    };
  })();
  const roiTransformEditHint = roiInputConnected
    ? 'roi 输入端口已连接，上游 ROI 优先于本节点参数。请在上游 ROI 节点编辑区域。'
    : referenceMismatch ? '图像或定位来源、模型原点已变化，请重新设为基准后再编辑、运行或应用。'
      : !referenceImage ? '先获取图像并设为基准，系统会同时保存该次图像与实际位姿，再在基准图上绘制 ROI。旧流程的手动基准参数继续兼容。'
        : roiImageView === 'reference' ? '在已保存基准图上编辑 ROI。重新设为基准会同步迁移已有区域；当前图与修正结果只读。'
          : '当前图像只读。切回已保存基准图像编辑区域，避免对当前图上的选区重复施加位移。';
  const { portHiddenParameterNames, numericHiddenParameterNames, editorHiddenParameterNames, unsupportedCropParameterNames } = parameterVisibility({
    type: catalogItem.type, draft, connectedInputPorts, isMetrology, isCaliperCircle, isCaliperEdge
  });
  const trainingMode = shapeMode === 'training';
  const shapePanelCatalogItem = shapeMode ? {
    ...catalogItem,
    parameters: catalogItem.parameters.filter((parameter) => {
      const group = canonicalParameterGroup(parameter.group);
      if (parameter.name === 'angleRangeDeg' && !legacyWorkflow) return false;
      if (parameter.name === 'templatePath' && templateSourceNodeId) return false;
      if (parameter.name === 'scoreUsingClutter' && !draft.clutterRoi) return false;
      if (['trainingOriginX', 'trainingOriginY'].includes(parameter.name) && draft.trainingOriginMode !== 'manual') return false;
      if (parameter.name === 'scaleStepPercent' && Number(draft.scaleTolerancePercent ?? 0) === 0
        && Number(draft.scaleToleranceXPercent ?? 0) === 0 && Number(draft.scaleToleranceYPercent ?? 0) === 0) return false;
      if (parameter.name === 'minArea') return !trainingMode;
      return trainingMode
        ? ['Training', 'Origin Estimation', 'Candidates', 'Auto Tune', 'Model', 'General'].includes(group)
        : ['Matching', 'Multi-target', 'Search', 'Managed Chamfer', 'Diagnostics', 'General'].includes(group);
    }).map((parameter) => ({
      ...parameter,
      group: ({
        Training: '模型训练', 'Origin Estimation': '原点估算', Candidates: '特征筛选', General: '训练图像来源',
        Matching: '匹配条件', 'Multi-target': '多目标定位', Search: '搜索范围'
      } as Record<string, string>)[['trainingAngleRangeDeg', 'scaleTolerancePercent', 'scaleToleranceXPercent', 'scaleToleranceYPercent'].includes(parameter.name)
        ? 'Training' : canonicalParameterGroup(parameter.group)] ?? parameter.group ?? undefined
    }))
  } : searchLineInputEdge ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
    !['startX', 'startY', 'endX', 'endY'].includes(parameter.name))
  } : circleInputEdge ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
    !['centerX', 'centerY', 'radius'].includes(parameter.name))
  } : isRoiTransform ? { ...catalogItem, parameters: catalogItem.parameters.filter(parameter =>
    !(referenceImage && ['baseX', 'baseY', 'baseThetaDeg'].includes(parameter.name))
    && !(poseEdge && ['currentX', 'currentY', 'currentThetaDeg'].includes(parameter.name))) } : catalogItem;
  const roiFields = isPerspective ? [] : shapeMode ? trainingMode
    ? [
      { key: 'roi', label: '模型区域', emptyText: '未设置 · 使用整幅训练图像', showEmpty: true },
      { key: 'clutterRoi', label: '杂乱区域', emptyText: '未设置 · 不启用杂乱度评分', showEmpty: true }
    ]
    : [{ key: 'searchRoi', label: '搜索区域', emptyText: '未设置 · 使用整幅场景图像', showEmpty: true }]
    : isImageCrop ? cropRoiInputConnected ? []
      : [{ key: 'roi', label: '裁剪区域', emptyText: legacyCropBoundsPresent && !draft.roi
        ? '正在使用旧版裁剪坐标；载入输入图像后可在画布核对或调整' : '未绘制 · 使用旧版坐标或整幅图像', showEmpty: true }]
    : isRoiTransform ? roiInputConnected ? [] : [{ key: 'roi', label: '基准 ROI', emptyText: '设定基准后在基准图上绘制', showEmpty: true }]
      : roiMaskInputConnected || showMeasurementReferenceImage ? [] : undefined;
  return <div ref={bindWorkspace} tabIndex={-1} className={'node-configuration-workspace' + (showImage ? '' : ' without-image')}
    style={{ '--node-columns': columns(imageShare) } as CSSProperties}>
    {showImage && <section className={'node-configuration-image' + (isRoiTransform ? ' has-roi-reference' : '')} aria-label={shapeMode === 'training' ? '模型训练图像' : shapeMode === 'search' ? '搜索推理图像' : '图像预览'}>
      <div className="node-configuration-image-source">
        {showCaliperDiagnostics ? <span>诊断图像 · 已应用本次 ROI 与坐标变换</span> : shapeMode ? <>
          <span>{trainingMode ? '训练图像' : '搜索场景图像'}</span>
          <Button size="small" onClick={() => void (trainingMode ? fetchTrainingImage() : fetchSearchImage())}
            loading={trainingMode ? trainingImageBusy : searchImageBusy}
            disabled={locked || externalChange || Boolean(previewBlockedReason)}
            title={previewBlockedReason || (trainingMode ? '读取训练源图像并编辑模型区域' : '读取运行场景图像并编辑搜索区域')}>
            {trainingMode ? '获取训练图像' : '获取场景图像'}
          </Button>
          {trainingMode && trainedModelData && (trainedSourceImage || trainedPreviewData) &&
            <Button size="small" disabled={locked} onClick={locateSavedTrainingImage}>查看已保存训练图像</Button>}
        </> : isImageCrop ? <>
          <span>裁剪输入图像</span>
          <Segmented size="small" value={imageSource === 'output' ? 'output' : 'input'} onChange={(value) => setImageSource(value as 'input' | 'output')}
            options={[{ label: '输入图像', value: 'input', disabled: !selectedInputAvailable }, { label: '裁剪输出', value: 'output', disabled: !selectedOutputAvailable }]} />
        </> : isRoiTransform ? <>
          <Button size="small" type={referenceImage ? 'default' : 'primary'} loading={previewBusy}
            aria-label={referenceImage ? '重新设为基准' : '获取图像并设为基准'}
            disabled={locked || externalChange || Boolean(previewBlockedReason) || !debugImageConnected}
            title={!debugImageConnected ? '请先将场景图像连接到调试图像端口' : previewBlockedReason || '同时保存本次图像与实际定位位姿，并迁移已有 ROI'}
            onClick={() => void captureRoiReference()}>{referenceImage ? '重新设为基准' : '获取图像并设为基准'}</Button>
          <Segmented size="small" value={roiImageView} onChange={value => setRoiImageView(value as typeof roiImageView)}
            options={[{ label: '已保存基准', value: 'reference', disabled: !referenceImage },
              { label: '当前输入', value: 'input', disabled: !selectedInputAvailable },
              { label: '修正结果', value: 'output', disabled: !selectedOutputAvailable }]} />
        </> : (canEditMeasurementGeometry || canDisplayConnectedSearchLine)
          && (measurementGeometryFrameKind === 'transform-input'
            || measurementGeometryFrameKind === 'saved-reference' && measurementImageFrameReady) ? <>
          <span>卡尺几何图像</span>
          <Segmented size="small" aria-label="卡尺几何图像坐标系" value={measurementImageView}
            onChange={(value) => {
              const next = value as 'reference' | 'current';
              setMeasurementImageView(next);
              if (next === 'current') { setPreviewNodeId(node.id); setImageSource('input'); }
              else if (measurementGeometryFrameKind === 'transform-input' && cropMeasurementReferenceNodeId) {
                setPreviewNodeId(cropMeasurementReferenceNodeId); setImageSource('output');
              }
            }}
            options={[{ label: '基准图像', value: 'reference' },
              { label: '当前测量图像', value: 'current', disabled: !measurementCurrentImageAvailable }]} />
        </>
          : canEditMeasurementGeometry && measurementGeometryFrameKind === 'transform-input' && cropMeasurementReferenceNodeId
            ? <span>几何基准图像 · {nodeLabels[cropMeasurementReferenceNodeId] ?? cropMeasurementReferenceNodeId}</span>
            : <>
          <span>预览节点</span><Select size="small" aria-label="预览节点" showSearch optionFilterProp="label" value={previewNodeId}
            style={{ minWidth: 130, flex: '1 1 130px' }} options={nodes.map((item) => ({ value: item.id, label: nodeLabels[item.id] }))}
            onChange={(id) => { setPreviewNodeId(id); setImageSource('output'); }} />
          <Segmented size="small" value={imageSource} onChange={(value) => setImageSource(value as 'preview' | 'output' | 'input')}
            options={[{ label: '最终预览', value: 'preview' }, { label: '输出', value: 'output', disabled: !selectedOutputAvailable }, { label: '输入', value: 'input', disabled: !selectedInputAvailable }]} />
        </>}
        {!showCaliperDiagnostics && imageSource === 'output' && imageOutputPorts.length > 1 && <Select size="small" aria-label="输出图像端口"
          value={imageOutputPortName} style={{ minWidth: 110 }} options={imageOutputPorts.map((entry) => ({ value: entry.portName, label: runImagePortLabel(nodes, previewNodeId, entry.portName) }))}
          onChange={setImageOutputPortName} />}
        {!showCaliperDiagnostics && canEditMeasurementGeometry && measurementGeometry && measurementImageFrameReady && !showCurrentMeasurementImage
          && !(measurementGeometry.type === 'circle' && circleInputEdge) && <Button size="small" disabled={locked}
            onClick={fitMeasurementGeometryToImage}
            title="将搜索线或标称圆居中并缩放到当前几何图像内；必要时收窄沿边缘跨度或径向搜索带，保留阈值、极性、数量和圆弧设置。">
            适配几何到图像
          </Button>}
        {!showCaliperDiagnostics && measurementFitUndoAvailable && <Button size="small" disabled={locked} onClick={undoMeasurementGeometryFit}
          title="撤销最近一次适配几何到图像">撤销几何适配</Button>}
      </div>
      <div className="node-image-provenance"><span>{displayImageSource.label}</span>
        {imageCatalogLoading && <span>正在加载节点图像…</span>}
        {imageCatalogError && <span role="alert">节点图像加载失败：{imageCatalogError} <Button size="small" onClick={retryImageCatalog}>重试加载</Button></span>}
        {isRoiTransform && roiImageView === 'reference' && draft.referenceCapturedAt
          ? <span>基准采集于 {new Date(String(draft.referenceCapturedAt)).toLocaleString()}</span>
          : showCapturedTrainingImage ? <span>采集于 {new Date(capturedTrainingImage!.receivedAt).toLocaleString()}</span>
            : !showTrainedTemplatePreview && receivedAt && <span>更新于 {new Date(receivedAt).toLocaleString()}</span>}
        {stale && displayedResult && !(isRoiTransform && roiImageView === 'reference') && <Tag color="gold">上次运行图像 · 当前参数待验证</Tag>}
      </div>
      {isRoiTransform && <Alert showIcon type={roiInputConnected || referenceMismatch || !referenceImage ? 'warning' : canEditRoi ? 'success' : 'info'}
        title={roiInputConnected ? '当前 ROI 来自上游节点' : canEditRoi ? '正在编辑基准 ROI' : '基准 ROI 编辑说明'}
        description={<Space wrap>
          <span>{roiTransformEditHint}</span>
          {roiInputConnected && roiSourceNode?.data.typeKey === 'roi.transform' && <span>
            上游定位修正 ROI 已经过一次位姿变换；本节点仍会按自身的基准位姿与当前位姿再次变换。只有需要叠加第二次修正时才保留此连接；否则请将原始 ROI 接入本节点，或将上游 ROI 直接接到目标裁剪/测量节点。
          </span>}
          {!debugImageConnected && <span>获取基准前，请将场景图像连接到“调试图像”端口。</span>}
          {referenceImage && roiImageView !== 'reference' && <Button size="small" disabled={locked}
            onClick={() => setRoiImageView('reference')}>切换到已保存基准</Button>}
        </Space>} />}
      {roiMaskInputConnected && <Alert showIcon type="info" title={`ROI 掩膜来自上游：${nodeLabels[roiInputEdge!.source] ?? roiInputEdge!.source}`}
        description="此节点的 roi 输入优先于本地选区，且按当前图像坐标解释。请在上游编辑区域；当前节点不会再次移动 ROI。有效运行报告且坐标系匹配时，轮廓会在输入图像上只读显示。" />}
      {connectedRoiImageFrameMismatch && <Alert showIcon type="error" title="ROI 与目标图像坐标系不匹配"
        description={connectedRoiImageFrameMismatchMessage} />}
      {croppedImageNeedsRoiWarning && <Alert showIcon type="warning" title="上游裁剪可能形成伪边缘"
        description={directCropMaskSource
          ? <span>如果裁剪使用圆形、多边形或笔刷 ROI，选区外像素会清零，可能形成伪边缘。请将“{nodeLabels[directCropMaskSource.id] ?? directCropMaskSource.id}”的 croppedRoi 输出接到本节点 roi 输入，以屏蔽选区外采样；灰度转换等不改变坐标的处理可以放在两者之间，模糊等邻域滤波后请避开 ROI 边界测量。</span>
          : <span>图像处理链中存在非矩形裁剪，选区外像素会清零，可能形成伪边缘；多次裁剪或坐标变换后，单个 croppedRoi 可能无法直接覆盖当前图像。请在当前图像坐标系提供完整 ROI 掩膜，或调整流程避免将裁剪边界当作真实边缘。</span>} />}
      {circleSamplingNotice && <Alert showIcon type="info" title="圆卡尺实际步长已自动调整" description={circleSamplingNotice} />}
      {circleFitConditionNotice && <Alert showIcon type="warning" title="圆弧覆盖可能不足以稳定拟合" description={circleFitConditionNotice} />}
      {measurementTransformInvalid && <Alert showIcon type="warning" title="当前 transform 不符合卡尺测量要求"
        description="本提示依据最近一次运行报告中的实际输入。卡尺只接受有限、方向保持的 px→px 刚性变换（平移和旋转）；缩放、剪切、透视、镜像或非像素单位会导致测量失败。请改用刚性像素变换，或让测量几何直接使用当前图像坐标。" />}
      {!showCaliperDiagnostics && measurementImageFrameUnavailable && <Alert showIcon type="warning" title="当前图像不能用于测量几何编辑"
        description={measurementGeometryFrameKind === 'transform-input' && measurementImageView === 'reference'
          ? `基准图像必须来自裁剪输入实际连接的端口“${cropMeasurementReferencePortName}”。请切回该输出端口；若未保存该端口图像，请重新运行以获取基准。其它端口和最终预览不会替代基准图像。`
          : '只有实际连接的测量输入图像或节点的 image 透传输出可用于当前坐标系编辑。其它端口和最终预览仅供查看；请切换图像来源或重新运行以获取对应图像。'} />}
      {!showCaliperDiagnostics && showCurrentMeasurementImage && viewerMeasurementGeometry && <Alert showIcon type="info" title="映射几何只读"
        description={canEditMeasurementGeometry
          ? '当前视图用于核对本次运行 transform 后的实际位置；请切回基准图像拖动或重绘卡尺几何。'
          : '当前视图用于核对本次运行 transform 后的实际位置；上游几何保持只读，请在来源节点中调整。'} />}
      {!showCaliperDiagnostics && currentMeasurementGeometryUnavailable && <Alert showIcon type="info" title="尚无当前图像中的卡尺位置"
        description="运行此节点以读取本次实际使用的 transform；在此之前不会把基准图像坐标直接叠加到当前测量图像。" />}
      {measurementTransformSourceNotice && <Alert showIcon type="info" title="transform 来源可能不适用于卡尺"
        description={measurementTransformSourceNotice} />}
      {!showCaliperDiagnostics && transformedBoundsPreflightUnavailable && <Alert showIcon type="info" title="暂时无法预估变换后的图像边界"
        description="当前节点的有效运行报告或测量图尺寸尚不可用。预检不会用基准图坐标冒充当前图像坐标；先运行一次上游定位与变换，再检查变换后的卡尺范围。" />}
      {!showCaliperDiagnostics && caliperCircleBoundsNotice && <Alert showIcon type={caliperCircleBoundsNotice.type} title={caliperCircleBoundsNotice.title}
        description={caliperCircleBoundsNotice.description} />}
      {caliperLineBoundsNotice && <Alert showIcon type={caliperLineBoundsNotice.type} title={caliperLineBoundsNotice.title}
        description={caliperLineBoundsNotice.description} />}
      {!showCaliperDiagnostics && caliperSearchLineBoundsNotice && <Alert showIcon type={caliperSearchLineBoundsNotice.type} title={caliperSearchLineBoundsNotice.title}
        description={caliperSearchLineBoundsNotice.description} />}
      {searchLineInputEdge && (!showCaliperDiagnostics || measurementGeometryConflict) && <Alert showIcon type={correctedSearchLineTransformConflict || connectedSearchLineImageFrameMismatch || connectedSearchLineTransformSourceMismatch ? 'error' : 'info'}
        title={correctedSearchLineTransformConflict ? '上游几何已处于变换目标坐标系'
          : connectedSearchLineTransformSourceMismatch ? '上游搜索线与 transform 源坐标系不匹配'
            : connectedSearchLineImageFrameMismatch ? '上游搜索线与测量图像坐标不匹配'
              : `搜索线来自上游：${nodeLabels[searchLineInputEdge.source] ?? searchLineInputEdge.source}`}
        description={correctedSearchLineTransformMessage ?? searchLineTransformSourceMismatchMessage ?? searchLineImageFrameMismatchMessage ?? (transformInputEdge
          ? connectedSearchLine && measurementGeometryFrameKind
            ? `蓝色线显示运行报告中的上游基准搜索线；它会在执行时经过“${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}”变换到当前图像。几何仅在匹配的基准图像上只读显示。`
            : `上游搜索线会再通过“${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}”的变换映射到当前图像；请确认上游线处于基准坐标，已是当前坐标的线不要重复变换。`
          : connectedSearchLine
            ? '蓝色线显示本次运行实际使用的上游搜索线（只读）；要移动搜索线，请在上游节点中调整几何。'
            : '上游 searchLine 会覆盖本节点的起点和终点参数；运行节点以读取实际搜索线，然后可在图像上查看其位置。要移动搜索线，请在上游节点中调整几何。')} />}
      {circleInputEdge && (!showCaliperDiagnostics || measurementGeometryConflict) && <Alert showIcon type={correctedCircleTransformConflict || connectedCircleImageFrameMismatch || connectedCircleTransformSourceMismatch ? 'error' : transformInputEdge ? 'warning' : 'info'}
        title={correctedCircleTransformConflict ? directCorrectedCircleTransformConflict
          ? '定位修正圆不能再次应用 transform' : '上游几何已处于变换目标坐标系'
          : connectedCircleTransformSourceMismatch ? '上游圆与 transform 源坐标系不匹配'
            : connectedCircleImageFrameMismatch ? '上游圆与测量图像坐标不匹配'
              : `标称圆来自上游：${nodeLabels[circleInputEdge.source] ?? circleInputEdge.source}`}
        description={correctedCircleTransformMessage ?? circleTransformSourceMismatchMessage ?? circleImageFrameMismatchMessage ?? (transformInputEdge
          ? connectedCircle && measurementGeometryFrameKind
            ? `圆心和标称半径锁定为运行报告中的上游基准圆；图像预览显示对应基准图像，可拖动圆弧和搜索带手柄，变换会在执行时映射到当前图像。`
            : `该圆还会经过“${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}”的变换；请确认上游圆处于基准坐标，已是当前坐标的圆不能重复变换。`
          : connectedCircle
            ? '圆心和标称半径锁定为本次运行的上游输入；可在图像上拖动蓝/紫圆弧端点和青/绿搜索带手柄，调整本节点的搜索范围。'
            : '上游 circle 会覆盖本节点的圆心和标称半径参数；运行此节点以读取本次使用的圆，之后可在图像上调整圆弧和搜索带。')} />}
      {circleInputIsBoundingApproximation && <Alert showIcon type="warning" title="上游提供的是 ROI 包围圆"
        description={`定位修正节点的 ROI 类型为 ${circleSourceRoiType}；其 circle 端口输出包围圆，不是圆拟合结果。请核对圆心和半径，并按实际尺寸设置径向搜索带；若目标需要精确圆心/半径，请连接圆检测或圆拟合节点。`} />}
      {!showCaliperDiagnostics && !searchLineInputEdge && !circleInputEdge && transformInputEdge && <Alert showIcon type="info"
        title={`几何将按变换跟随：${nodeLabels[transformInputEdge.source] ?? transformInputEdge.source}`}
        description={canEditMeasurementGeometry
          ? `本节点几何属于基准图像坐标；当前显示的${showMeasurementReferenceImage ? '已保存基准图像' : '变换输入图像'}可直接拖动或重绘，transform 会在运行时映射到测量图像。ROI 掩膜仍使用当前图像坐标。`
          : '本节点几何属于基准图像坐标；transform 会在运行时映射到测量图像。可从支持的定位修正 ROI 或图像裁剪变换自动打开对应基准图像。ROI 掩膜仍使用当前图像坐标。'} />}
      <div className="node-configuration-viewer">{isMetrology ? <MetrologyObjectEditor value={draft.objects}
        onChange={value => changeParameter('objects', value)} imageSrc={displayImageSource.url}
        width={displayImageSource.width} height={displayImageSource.height} disabled={locked || previewBusy}
        aligned={String(draft.alignMode ?? 'none') !== 'none'} overlays={resultOverlaysForViewer.filter(overlay => overlay.nodeId === node.id)} onLoadImage={() => void loadMetrologyImage()} /> : <ImageViewer previewUrl={displayImageSource.url} imageWidth={displayImageSource.width} imageHeight={displayImageSource.height}
        overlays={showCaliperDiagnostics ? diagnosticOverlays : overlaysWithTrainedContour}
        roi={showCaliperDiagnostics ? undefined : canEditRoi ? (isImageCrop ? cropRoiForImage : isPerspective ? perspectiveRoi : shapeMode === 'search' ? draft.searchRoi as VisionRoi | undefined
          : regionEditMode === 'clutter' ? draft.clutterRoi as VisionRoi | undefined : draft.roi as VisionRoi | undefined)
          : showConnectedRoiPreview ? connectedRoiPreviewGeometry : undefined} showRoiTools={!showCaliperDiagnostics && (canEditRoi || isRoiTransform)}
        roiPointLimit={isPerspective ? 4 : undefined} roiClearable={!isPerspective}
        measurementGeometry={showCaliperDiagnostics ? edgeDiagnosticGeometry : viewerMeasurementGeometry}
        measurementMarkers={showCaliperDiagnostics ? diagnosticMarkers : undefined}
        measurementGeometryEditable={!showCaliperDiagnostics && Boolean(measurementGeometry) && canEditMeasurementGeometry && !locked && !showCurrentMeasurementImage}
        caliperDiagnosticSelection={isCaliperCircle && showCaliperDiagnostics && diagnosticImageMatches && caliperDiagnostics.manifest ? {
          center: caliperDiagnostics.manifest.center, radius: caliperDiagnostics.manifest.nominalRadius,
          spokes: caliperDiagnostics.manifest.spokes, index: caliperDiagnostics.index, onSelect: caliperDiagnostics.select
        } : undefined}
        onMeasurementGeometryChange={updateMeasurementGeometry}
        roiEnabled={!showCaliperDiagnostics && canEditRoi && !locked && Boolean(displayImageSource.url)} roiTargetLabel={isImageCrop ? '裁剪输入图像' : isPerspective ? '透视四边形（左上 → 右上 → 右下 → 左下）' : isRoiTransform ? '基准 ROI' : shapeMode === 'search' ? '搜索区域' : regionEditMode === 'clutter' ? '杂乱区域' : shapeMode === 'training' ? '模型区域' : imageInputSourceId ? `输入图像 · ${nodeLabels[imageInputSourceId] ?? imageInputSourceId}` : String(node.data.label ?? node.id)}
        loading={previewBusy} onRoiChange={(roi) => isImageCrop ? changeCropRoi(roi) : isPerspective ? changePerspectiveQuad(roi) : changeParameter(shapeMode === 'search' ? 'searchRoi' : regionEditMode === 'clutter' ? 'clutterRoi' : 'roi', roi)} nodeLabels={nodeLabels} focusTick={focusTick}
        emptyHint={showCaliperDiagnostics ? (isCaliperEdge ? edgeDiagnostics.loading : caliperDiagnostics.loading) ? '正在读取本次运行的诊断图像…' : '本次诊断图像不可用；请读取诊断或重新运行此节点。' : isRoiTransform ? '获取图像并设为基准，然后在已保存的图像上绘制 ROI' : isImageCrop ? '运行节点以加载输入图像，然后绘制裁剪区域' : isPerspective ? '运行此节点加载输入图像，然后依次点击左上、右上、右下、左下四个角' : shapeMode === 'training' ? regionEditMode === 'clutter' ? '在训练图像上绘制预期为空的杂乱区域' : '获取训练图像后可绘制模型区域' : shapeMode === 'search' ? '获取场景图像后可绘制搜索区域' : '运行此节点以显示图像与矢量覆盖层'}
        emptyAction={{
          label: isRoiTransform ? '获取图像并设为基准' : isImageCrop ? '运行以获取输入图像' : isPerspective ? '运行以获取输入图像' : shapeMode === 'training' ? '获取训练图像' : shapeMode === 'search' ? '获取场景图像' : '运行此节点',
          onClick: () => void (isRoiTransform ? captureRoiReference() : shapeMode === 'training' ? fetchTrainingImage() : shapeMode === 'search' ? fetchSearchImage() : runPreview()),
          disabled: locked || (!shapeMode && !isRoiTransform && issues.length > 0) || externalChange || Boolean(previewBlockedReason) || (isRoiTransform && !debugImageConnected),
          title: previewBlockedReason || (shapeMode ? '读取当前步骤的图像' : '使用暂存参数测试此节点（Ctrl+Enter）')
        }} />}</div>
    </section>}
    {showImage && <SplitHandle axis="x" label="调整图像与参数宽度" value={imageShare} min={35} max={80} scale={100 / workspaceWidth}
      collapsed={false} collapsible={false} collapseGlyph="" expandGlyph="" onToggleCollapse={() => {}}
      preview={previewShare} commit={commitShare} reset={() => commitShare(67)} />}
    <section className="node-configuration-parameters" aria-label={trainingMode ? '模型训练参数' : shapeMode === 'search' ? '搜索推理参数' : '节点参数'}>
      <div className="node-parameter-heading"><strong>{shapeMode ? trainingMode ? '模型训练参数（暂存）' : '搜索推理参数（暂存）' : '参数（暂存）'}</strong>
        <span className="node-parameter-tools">
          {dirty && <Tag color="gold" className="node-parameter-dirty" title="与打开弹窗时的值不同">已改 {changedCount} 项</Tag>}
          <Button size="small" type="link" disabled={!dirty || locked} onClick={() => setDraft(cloneParameters(baseline))}>还原为打开时</Button>
        </span>
      </div>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap', padding: '0 10px 8px' }}>
        <span>节点超时</span>
        <InputNumber size="small" min={0} max={600000} step={100} style={{ width: 120 }} aria-label="节点超时（毫秒）"
          value={typeof draft.__timeoutMs === 'number' ? draft.__timeoutMs : undefined} placeholder="不限制" disabled={locked}
          onChange={(value) => changeParameter('__timeoutMs', typeof value === 'number' && value > 0 ? Math.round(value) : undefined)} />
        <span>毫秒</span>
        <Tooltip title="超过后本次运行在此节点失败并返回 node_timeout 错误码，而不是让一个慢节点吃掉整轮预算。留空表示只受整轮预算限制。取消是协作式的：卡在原生库内部且不轮询取消的调用不会被强行打断。">
          <span style={{ cursor: 'help', textDecoration: 'underline dotted' }}>说明</span>
        </Tooltip>
      </div>
      {nodeDuration && <div data-testid="node-duration-hint" style={{ padding: '0 10px 8px', fontSize: 12, color: 'var(--vs-text-3)' }}>
        {nodeDuration.samples < 20
          ? `耗时样本 ${nodeDuration.samples}/20，P95 ${Math.round(nodeDuration.p95Ms)} 毫秒（样本不足，暂不建议超时值）`
          : nodeDuration.suggestedTimeoutMs === undefined
            ? `P95 ${Math.round(nodeDuration.p95Ms)} 毫秒 · 暂无建议值`
            : `P95 ${Math.round(nodeDuration.p95Ms)} 毫秒 · 建议超时 ${nodeDuration.suggestedTimeoutMs} 毫秒`}
        {nodeDuration.timeoutCount > 0 && <Tag color="red" style={{ marginLeft: 6 }}>已触发超时 {nodeDuration.timeoutCount} 次</Tag>}
        {nodeDuration.tooTight && <Tag color="orange" style={{ marginLeft: 6 }}>最近运行配置偏紧</Tag>}
      </div>}
      {(isCaliperCircle || isCaliperEdge) && <div className="caliper-workspace-mode"><Segmented size="small" aria-label={isCaliperEdge ? '单边缘卡尺工作模式' : '圆卡尺工作模式'}
        value={caliperDiagnosticMode ? 'diagnostics' : 'settings'}
        options={[{ label: isCaliperEdge ? '选线与调参' : '圈选与调参', value: 'settings' }, { label: isCaliperEdge ? '边缘剖面诊断' : '单卡尺诊断', value: 'diagnostics' }]}
        onChange={value => setCaliperDiagnosticMode(value === 'diagnostics')} /></div>}
      {isCaliperCircle && showCaliperDiagnostics && <CaliperCircleDiagnosticsPanel manifest={caliperDiagnostics.manifest} profile={caliperDiagnostics.profile}
        index={caliperDiagnostics.index} available={caliperDiagnostics.available} loading={caliperDiagnostics.loading} error={caliperDiagnostics.error}
        stale={stale} busy={previewBusy} onSelect={caliperDiagnostics.select} onRetry={caliperDiagnostics.retry} onRun={() => void runPreview()}
        runDisabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || measurementGeometryConflict || connectedRoiImageFrameMismatch} />}
      {isCaliperEdge && showCaliperDiagnostics && <CaliperEdgeDiagnosticsPanel key={edgeDiagnosticKey ?? 'no-edge-diagnostic'}
        data={edgeDiagnostics.data} available={edgeDiagnostics.available} loading={edgeDiagnostics.loading} error={edgeDiagnostics.error}
        stale={stale} busy={previewBusy} onRetry={edgeDiagnostics.retry} onRun={() => void runPreview()}
        inspectionPosition={edgeInspectionPosition} onInspect={inspectEdgePosition}
        runDisabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || measurementGeometryConflict || connectedRoiImageFrameMismatch} />}
      {isCaliperEdge && <CaliperEdgeQuickSettings parameters={draft} parameterDescriptors={catalogItem.parameters} disabled={locked}
        geometry={measurementGeometry?.type === 'line' ? measurementGeometry : undefined}
        geometryDisabled={showCaliperDiagnostics || !canEditMeasurementGeometry || !measurementImageFrameReady || showCurrentMeasurementImage || measurementGeometryConflict}
        geometryHint={searchLineInputEdge ? '搜索线来自上游，几何只读；可在此调节采样参数。'
          : showCaliperDiagnostics ? '当前图像显示运行快照；切回“选线与调参”调整搜索线。'
            : showCurrentMeasurementImage ? '当前映射几何只读；切回基准图像调整搜索线。'
              : !measurementGeometry ? '运行节点加载匹配的图像后，可拖动或重绘搜索线。' : undefined}
        onGeometryChange={updateMeasurementGeometry} onChange={patch => setDraft(current => ({ ...current, ...patch }))} />}
      {isCaliperCircle && <CaliperCircleQuickSettings parameters={draft} parameterDescriptors={catalogItem.parameters} disabled={locked}
        onChange={patch => setDraft(current => ({ ...current, ...patch }))} />}
      {isRoiTransform && <div className="shape-training-panel">
        <strong>{referenceImage ? '已保存基准位姿' : '建立基准'}</strong>
        {referenceImage && <p>X {Number(draft.baseX).toFixed(2)} px · Y {Number(draft.baseY).toFixed(2)} px · 角度 {Number(draft.baseThetaDeg).toFixed(2)}°</p>}
        <p>{poseEdge ? `当前位姿来自 ${String(poseSource?.data.label ?? poseEdge.source)}；以每次运行的实际定位结果为准。` : '当前位姿使用下方手动参数。'}</p>
        <p>下游测量：将“变换”输出连接到测量节点的 transform，搜索线、方向和采样布局即可跟随。ROI 输出已经是当前图像坐标，不要重复变换。</p>
      </div>}
      {shapeMode === 'training' && <div className="shape-training-panel">
        <strong>建立形状模型</strong>
        <p>获取图像 → 绘制模型区域 → 可选绘制模型周围“正常时不应有边缘”的杂乱区域 → 可重复采集并加入最多 4 张同尺寸样本 → 融合稳定轮廓训练复合模型 → 测试搜索。每张图像都会锁定；后续样本先按首张模型定位，再自动变换模型区域、杂乱区域和姿态。</p>
        {Boolean(draft.clutterRoi) && <Button size="small" onClick={() => changeParameter('clutterRoi', undefined)} disabled={locked}>清除杂乱区域（关闭杂乱度评分）</Button>}
        <p>训练来源：{templateSourceNodeId ? `模板输入 ← ${nodeLabels[templateSourceNodeId] ?? templateSourceNodeId}（优先使用此输入）` : String(draft.templatePath || '请填写下方模板图像路径或连接模板输入')}</p>
        {(capturedTrainingImage || trainingSamples.length > 0) && <div className="shape-training-panel">
          <p>样本训练：{effectiveTrainingSampleCount}/4 张。系统会保留达到出现比例的共同轮廓，过滤单张样本中的偶发特征；当前图像会自动参与训练。</p>
          <Space wrap>
            <Button size="small" disabled={!capturedTrainingImage || locked || effectiveTrainingSampleCount >= 4}
              onClick={addCurrentTrainingSample}>加入当前图像为样本</Button>
            {trainingSamples.length > 0 && <Button size="small" disabled={locked} onClick={() => setTrainingSamples([])}>清空额外样本</Button>}
          </Space>
        </div>}
        {!hasLosslessTrainingImage && <Alert showIcon type="info" title="先获取训练图像" description={trainedModelData && trainedPreviewData ? '此模型只有旧版压缩预览。请重新获取原图后再训练，避免继续使用有损预览。' : '获取图像会锁定当前像素，训练和以后重新训练都会使用这份无损图像。'} />}
        {showCapturedTrainingImage && !capturedTrainingImage!.trained && <p>当前图像尚未用于训练；测试搜索仍使用原有模型。点击“{trainedModelData ? '重新训练模型' : '训练模型'}”后使用当前图像。</p>}
        {legacyWorkflow
          ? <Alert showIcon type="info" title="旧模板图像兼容模式" description="本次运行使用 template 输入或模板路径；已保存的训练模型仍保留，训练后可切换到训练模型模式。" />
          : trainedModelData
            ? <Alert showIcon type={trainingStale ? 'warning' : 'success'} title={trainingStale ? '训练参数已变化，需要重新训练' : draft.trainedModelData !== baseline.trainedModelData ? '模型已训练，待应用到工作流' : '模型已保存在此工作流'} description={trainingStale ? `已修改：${changedTrainingLabels}。重新训练后即可测试搜索。` : `样本 ${draft.trainedSampleCount ?? 1} 张 · 模型尺寸 ${draft.trainedWidth ?? '—'} × ${draft.trainedHeight ?? '—'} px · ${Number(draft.trainedFeaturePointCount ?? 0).toLocaleString()} 个边缘特征点 · ${draft.trainedContourCount ?? 0} 段轮廓 · 可直接测试搜索，满意后点击“应用”。`} />
            : <Alert showIcon type="info" title="尚未训练" description="先获取训练图像并设置模型区域，再训练形状模型。" />}
        {Array.isArray(draft.trainedModelWarnings) && draft.trainedModelWarnings.length > 0 && <Alert showIcon type="warning" title="训练质量提示" description={(draft.trainedModelWarnings as string[]).join(' ')} />}
        {trainedModelData && !legacyWorkflow && <div className="shape-asset-actions">
          <Button size="small" loading={assetBusy} disabled={locked || issues.length > 0 || trainingStale}
            onClick={() => void saveAsAsset()}>{modelAssetId ? '追加为资产版本' : '保存为资产'}</Button>
          {modelAssetId && <span className="shape-asset-hint">资产 {modelAssetId} · v{modelAssetVersion > 0 ? modelAssetVersion : '最新'}</span>}
          {assetMessage && <span className="shape-asset-hint">{assetMessage}</span>}
        </div>}
        {trainedModelData && !trainingStale && !legacyWorkflow && <Button onClick={() => void runPreview()}
          disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason)}>下一步：测试搜索</Button>}
      </div>}
      {shapeMode === 'search' && <div className="shape-training-panel shape-search-panel">
        <strong>搜索与定位</strong>
        <p>设置角度/尺度窗口、最低分数、局部形变、特征对比度、杂乱评分、目标数量和搜索区域，然后运行搜索。角度与尺度窗口会限制在模型训练覆盖内，调整它们无需重训；局部形变默认关闭，增大后可容忍边缘小幅移动，但会降低定位约束。运行时对比度阈值可剔除弱边缘，设为 0 时关闭；相对阈值需要无损训练图。启用“杂乱度参与评分”后，分数按匹配分 ×（1 − 杂乱分）调整，同时保留原生分数供核对。区域留空时搜索整张场景图。</p>
        {matchReport?.success && typeof matchReport.summary.found === 'boolean' && <Alert showIcon
          type={matchStale ? 'info' : matchReport.summary.found ? 'success' : 'warning'}
          title={`${matchStale ? '上次搜索 · ' : ''}${matchReport.summary.found ? '已找到目标' : '未找到目标'}${matchStale ? ' · 当前参数待验证' : ''}`}
          description={matchReport.summary.found
            ? `数量 ${matchNumber('count', 'count', 0)} · 最佳分数 ${matchNumber('score', 'bestScore', 3)}${typeof matchReport.summary.matchedNativeScore === 'number' ? `（原生 ${matchNumber('nativeScore', 'matchedNativeScore', 3)}）` : ''}${typeof matchReport.summary.matchedContrast === 'number' ? ` · 特征对比度 ${matchNumber('contrast', 'matchedContrast', 1)}` : ''}${typeof matchReport.summary.matchedContrastFraction === 'number' ? ` · 相对对比度 ${matchNumber('contrastFraction', 'matchedContrastFraction', 3)}` : ''} · X ${matchNumber('x')} / Y ${matchNumber('y')} px · 角度 ${matchNumber('angle', 'matchedAngle')}°`
            : '请检查目标是否在搜索区域内、场景与模型是否一致，以及最小匹配分数设置。'} />}
        {!legacyWorkflow && <p>模型覆盖：角度 ±{String(trainedSettings?.trainingAngleRangeDeg ?? trainedSettings?.angleRangeDeg ?? draft.trainingAngleRangeDeg ?? 180)}° · 尺度 {Number(trainedSettings?.scaleToleranceXPercent ?? draft.scaleToleranceXPercent ?? 0) > 0 || Number(trainedSettings?.scaleToleranceYPercent ?? draft.scaleToleranceYPercent ?? 0) > 0
          ? `X ${100 - Number(trainedSettings?.scaleToleranceXPercent ?? draft.scaleToleranceXPercent ?? 0)}%～${100 + Number(trainedSettings?.scaleToleranceXPercent ?? draft.scaleToleranceXPercent ?? 0)}% · Y ${100 - Number(trainedSettings?.scaleToleranceYPercent ?? draft.scaleToleranceYPercent ?? 0)}%～${100 + Number(trainedSettings?.scaleToleranceYPercent ?? draft.scaleToleranceYPercent ?? 0)}%`
          : `${100 - Number(trainedSettings?.scaleTolerancePercent ?? draft.scaleTolerancePercent ?? 0)}%～${100 + Number(trainedSettings?.scaleTolerancePercent ?? draft.scaleTolerancePercent ?? 0)}%`}
          <Button size="small" type="link" disabled={locked} onClick={() => changeShapeWorkspaceTab('training')}>调整模型范围</Button></p>}
        {Boolean(draft.searchRoi) && <Button size="small" onClick={() => changeParameter('searchRoi', undefined)} disabled={locked}>清除搜索区域（恢复全图搜索）</Button>}
        {!trainedModelData && !legacyWorkflow && <Alert showIcon type="warning" title="尚未训练模型" description="先建立模型，即可在当前窗口运行搜索；无需先应用或关闭窗口。" />}
        {trainedModelData && trainingStale && <Alert showIcon type="warning" title="训练参数已变化，需要重新训练" description={`已修改：${changedTrainingLabels}。`} />}
        {shapeSearchBlocked && <Button disabled={locked} onClick={() => changeShapeWorkspaceTab('training')}>前往模型训练</Button>}
      </div>}
      {isImageCrop && <div className="shape-training-panel crop-settings-panel">
        <strong>选择裁剪区域</strong>
        <p>在输入图像上绘制矩形、圆形、多边形或笔刷选区；越出图像边缘的部分会裁切，完全未覆盖图像像素时会报错。笔刷支持添加和擦除，输出图像按选区与图像交集的紧包围框裁剪，选区外清零；ROI 蒙版端口输出二值选区。裁剪后原点移到新图左上角，可将“变换”端口连接到下游测量节点以换算原图坐标。</p>
        {cropRoiInputConnected && <Alert showIcon type="info" title={`裁剪区域来自上游：${nodeLabels[cropRoiSourceId!] ?? cropRoiSourceId}`}
          description="已连接的 roi 输入会覆盖本节点的本地选区。请在上游 ROI 节点编辑；本地选区不会参与本次裁剪。有效运行报告且坐标系匹配时，轮廓会在输入图像上只读显示。"
          style={{ marginBlock: 10 }} />}
        {!cropRoiInputConnected && (draft.roi || legacyCropBoundsPresent) && <Button size="small" disabled={locked} onClick={clearCropSelection}>清除选区（使用整图）</Button>}
        {cropClearUndoAvailable && <Button size="small" disabled={locked} onClick={undoCropSelectionClear}>撤销清除选区</Button>}
        <Button type="primary" size="small" onClick={() => void runPreview()} loading={previewBusy}
          disabled={locked || issues.length > 0 || externalChange || Boolean(previewBlockedReason) || connectedRoiImageFrameMismatch}
          title={connectedRoiImageFrameMismatchMessage || previewBlockedReason || '按当前 ROI 预览裁剪图像与 ROI 蒙版'}>预览裁剪结果</Button>
      </div>}
      {issues.length > 0 && <Alert type="error" title="请修正参数后再运行或应用" description={issues.join('；')} />}
      {(catalogItem.type === 'match.template' || (catalogItem.type === 'match.shape' && shapeMode !== 'search')) && <MatchingAutoTunePanel
        nodeType={catalogItem.type === 'match.template' ? 'match.template' : 'match.shape'}
        parameters={draft as Record<string, unknown>}
        disabled={locked || referenceMismatch}
        onApply={(values) => { for (const [key, value] of Object.entries(values)) changeParameter(key, value); }} />}
      {catalogItem.type === 'flow.switch' && <SwitchCaseEditor value={draft.casesJson} disabled={locked}
        onChange={(value) => changeParameter('casesJson', value)} />}
      {['coordinate.point', 'coordinate.point3D', 'coordinate.pose'].includes(catalogItem.type)
        && connectedInputPorts.some(port => ['x', 'y', 'z', 'thetaDeg'].includes(port))
        && <Alert type="info" showIcon title="已接线的坐标分量使用上游输入，未接线分量使用本地参数。" style={{ marginBottom: 10 }} />}
      <PropertyPanel node={draftNode} catalogItem={shapePanelCatalogItem} onChange={changeParameter} disabled={locked || referenceMismatch}
        connectedInputPorts={connectedInputPorts}
        onRunPreview={catalogItem.type === 'script.csharp' ? () => void runPreview() : undefined}
        previewBusy={previewBusy} scriptPreview={scriptPreviewReport}
        scriptPreviewBlockedReason={scriptPreviewBlockedReason}
        hiddenParameterNames={[...portHiddenParameterNames, ...numericHiddenParameterNames, ...editorHiddenParameterNames, ...unsupportedCropParameterNames]}
        roiFields={roiFields} onLocateRoi={canLocateRoiInImage ? locateRoi : undefined} />
    </section>
  </div>;
}
