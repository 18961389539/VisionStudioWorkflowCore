/**
 * 编辑器低频面板装配（巨型组件拆分，2026-11-13）：按需加载的面板常量与 LazyPanel 挂载点。
 * 面板开关与业务回调由 App 注入；挂载条件与错误边界范围与拆分前一致。
 */
import { lazy } from 'react';
import { message } from 'antd';
import ErrorBoundary from './ErrorBoundary';
import CameraPanel from './CameraPanel';
import RobotPanel from './RobotPanel';
import DevicePanel from './DevicePanel';
import ProductionPanel from './ProductionPanel';
import DiagnosticsPanel from './DiagnosticsPanel';
import { LazyPanel } from '../appEditorShared';
import type { NodeCatalogItem, RunResult, WorkflowModuleVersion, WorkflowPayload } from '../types';
import type { useWorkflowChanges } from '../useWorkflowChanges';

type MessageApi = ReturnType<typeof message.useMessage>[0];

const JobPanel = lazy(() => import('./JobPanel'));
const TracePanel = lazy(() => import('./TracePanel'));
const DatasetValidationPanel = lazy(() => import('./DatasetValidationPanel'));
const ModelPackagePanel = lazy(() => import('./ModelPackagePanel'));
const ModelAssetPanel = lazy(() => import('./ModelAssetPanel'));
const ParameterTuningPanel = lazy(() => import('./ParameterTuningPanel'));
const WorkflowModulePanel = lazy(() => import('./WorkflowModulePanel'));
const CalibrationPanel = lazy(() => import('./CalibrationPanel'));
const FrameTreePanel = lazy(() => import('./FrameTreePanel'));
const HardwareProvenancePanel = lazy(() => import('./HardwareProvenancePanel'));
const PluginPackagePanel = lazy(() => import('./PluginPackagePanel'));
const AboutPanel = lazy(() => import('./AboutPanel'));

export type EditorPanelsProps = {
  camerasOpen: boolean;
  setCamerasOpen: (value: boolean) => void;
  robotsOpen: boolean;
  setRobotsOpen: (value: boolean) => void;
  devicesOpen: boolean;
  setDevicesOpen: (value: boolean) => void;
  framesOpen: boolean;
  setFramesOpen: (value: boolean) => void;
  calibrationOpen: boolean;
  setCalibrationOpen: (value: boolean) => void;
  selectedNode: { data: { typeKey?: unknown } } | undefined;
  applyCalibrationParameters: (parameters: Record<string, unknown>) => void;
  jobsOpen: boolean;
  setJobsOpen: (value: boolean) => void;
  workflowPayload: WorkflowPayload;
  workflowChanges: ReturnType<typeof useWorkflowChanges>;
  applyResult: (result: RunResult) => void;
  tracesOpen: boolean;
  setTracesOpen: (value: boolean) => void;
  traceRunId: string | undefined;
  setTraceRunId: (value: string | undefined) => void;
  modelPackagesOpen: boolean;
  setModelPackagesOpen: (value: boolean) => void;
  modelAssetsOpen: boolean;
  setModelAssetsOpen: (value: boolean) => void;
  validationOpen: boolean;
  setValidationOpen: (value: boolean) => void;
  tuningOpen: boolean;
  setTuningOpen: (value: boolean) => void;
  catalog: NodeCatalogItem[];
  selectedId: string;
  messageApi: MessageApi;
  modulesOpen: boolean;
  setModulesOpen: (value: boolean) => void;
  insertModule: (version: WorkflowModuleVersion) => void;
  upgradeSelectedModule: (version: WorkflowModuleVersion) => void;
  productionOpen: boolean;
  setProductionOpen: (value: boolean) => void;
  diagnosticsOpen: boolean;
  setDiagnosticsOpen: (value: boolean) => void;
  aboutOpen: boolean;
  setAboutOpen: (value: boolean) => void;
  hardwareProvenanceOpen: boolean;
  setHardwareProvenanceOpen: (value: boolean) => void;
  pluginsOpen: boolean;
  setPluginsOpen: (value: boolean) => void;
  refreshCatalog: () => Promise<void>;
};

export default function EditorPanels({
  camerasOpen, setCamerasOpen, robotsOpen, setRobotsOpen, devicesOpen, setDevicesOpen,
  framesOpen, setFramesOpen, calibrationOpen, setCalibrationOpen, selectedNode, applyCalibrationParameters,
  jobsOpen, setJobsOpen, workflowPayload, workflowChanges, applyResult,
  tracesOpen, setTracesOpen, traceRunId, setTraceRunId,
  modelPackagesOpen, setModelPackagesOpen, modelAssetsOpen, setModelAssetsOpen,
  validationOpen, setValidationOpen, tuningOpen, setTuningOpen, catalog, selectedId, messageApi,
  modulesOpen, setModulesOpen, insertModule, upgradeSelectedModule,
  productionOpen, setProductionOpen, diagnosticsOpen, setDiagnosticsOpen,
  aboutOpen, setAboutOpen, hardwareProvenanceOpen, setHardwareProvenanceOpen,
  pluginsOpen, setPluginsOpen, refreshCatalog
}: EditorPanelsProps) {
  return (
    <>
      <ErrorBoundary variant="panel" scope="相机" onDismiss={() => setCamerasOpen(false)}>
        <CameraPanel open={camerasOpen} onClose={() => setCamerasOpen(false)} />
      </ErrorBoundary>
      <ErrorBoundary variant="panel" scope="机器人" onDismiss={() => setRobotsOpen(false)}>
        <RobotPanel open={robotsOpen} onClose={() => setRobotsOpen(false)} />
      </ErrorBoundary>
      <ErrorBoundary variant="panel" scope="设备" onDismiss={() => setDevicesOpen(false)}>
        <DevicePanel open={devicesOpen} onClose={() => setDevicesOpen(false)} />
      </ErrorBoundary>
      <LazyPanel open={framesOpen} label="图像帧" onClose={() => setFramesOpen(false)}><FrameTreePanel open onClose={() => setFramesOpen(false)} /></LazyPanel>
      <LazyPanel open={calibrationOpen} label="标定" onClose={() => setCalibrationOpen(false)}><CalibrationPanel
        open
        onClose={() => setCalibrationOpen(false)}
        canApplyToSelectedNode={['calibration.planar', 'calibration.cameraIntrinsics', 'image.undistort', 'calibration.handEye2D', 'calibration.handEye3D'].includes(String(selectedNode?.data.typeKey ?? ''))}
        selectedNodeType={selectedNode ? String(selectedNode.data.typeKey) : undefined}
        onApplyToSelectedNode={applyCalibrationParameters}
      /></LazyPanel>
      <LazyPanel open={jobsOpen} label="作业" onClose={() => setJobsOpen(false)}><JobPanel open onClose={() => setJobsOpen(false)} workflow={workflowPayload} prepareWorkflowChange={workflowChanges.prepare} onRunResult={applyResult} /></LazyPanel>
      <LazyPanel open={tracesOpen} label="追溯" onClose={() => { setTracesOpen(false); setTraceRunId(undefined); }}>
        <TracePanel open initialRunId={traceRunId} onClose={() => { setTracesOpen(false); setTraceRunId(undefined); }} />
      </LazyPanel>
      <LazyPanel open={modelPackagesOpen} label="模型包" onClose={() => setModelPackagesOpen(false)}><ModelPackagePanel open onClose={() => setModelPackagesOpen(false)} /></LazyPanel>
      <LazyPanel open={modelAssetsOpen} label="模型资产" onClose={() => setModelAssetsOpen(false)}><ModelAssetPanel open onClose={() => setModelAssetsOpen(false)} /></LazyPanel>
      <LazyPanel open={validationOpen} label="数据集验证" onClose={() => setValidationOpen(false)}><DatasetValidationPanel open onClose={() => setValidationOpen(false)} currentWorkflow={workflowPayload} /></LazyPanel>
      <LazyPanel open={tuningOpen} label="参数调优" onClose={() => setTuningOpen(false)}><ParameterTuningPanel open onClose={() => setTuningOpen(false)} currentWorkflow={workflowPayload} catalog={catalog} initialNodeId={selectedId} prepareWorkflowChange={workflowChanges.prepare} onApplied={() => { setTuningOpen(false); messageApi.success('已将调优后的候选方案应用到设计器'); }} /></LazyPanel>
      <LazyPanel open={modulesOpen} label="流程模块" onClose={() => setModulesOpen(false)}><WorkflowModulePanel open onClose={() => setModulesOpen(false)} currentWorkflow={workflowPayload} selectedNodeId={selectedId} selectedNodeType={String(selectedNode?.data.typeKey ?? '')} prepareWorkflowChange={workflowChanges.prepare} onInsertModule={insertModule} onUpgradeSelected={upgradeSelectedModule} /></LazyPanel>
      <ErrorBoundary variant="panel" scope="生产运行" onDismiss={() => setProductionOpen(false)}>
        <ProductionPanel open={productionOpen} onClose={() => setProductionOpen(false)} />
      </ErrorBoundary>
      <ErrorBoundary variant="panel" scope="诊断" onDismiss={() => setDiagnosticsOpen(false)}>
        <DiagnosticsPanel open={diagnosticsOpen} onClose={() => setDiagnosticsOpen(false)} />
      </ErrorBoundary>
      <LazyPanel open={aboutOpen} label="关于" onClose={() => setAboutOpen(false)}><AboutPanel open onClose={() => setAboutOpen(false)} /></LazyPanel>
      <LazyPanel open={hardwareProvenanceOpen} label="硬件溯源" onClose={() => setHardwareProvenanceOpen(false)}><HardwareProvenancePanel open onClose={() => setHardwareProvenanceOpen(false)} /></LazyPanel>
      <LazyPanel open={pluginsOpen} label="插件包" onClose={() => setPluginsOpen(false)}><PluginPackagePanel open onClose={() => setPluginsOpen(false)} onCatalogRefresh={refreshCatalog} /></LazyPanel>
    </>
  );
}
