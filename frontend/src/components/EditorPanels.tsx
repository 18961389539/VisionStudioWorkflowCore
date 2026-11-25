/**
 * 编辑器低频面板装配（巨型组件拆分，2026-11-13）：按需加载的面板常量与 LazyPanel 挂载点。
 * 面板开关与业务回调由 App 注入；挂载条件与错误边界范围与拆分前一致。
 */
import { lazy } from 'react';
import { message } from 'antd';
import { LazyPanel } from '../appEditorShared';
import type { NodeCatalogItem, RunResult, WorkflowModuleVersion, WorkflowPayload } from '../types';
import type { useWorkflowChanges } from '../useWorkflowChanges';

type MessageApi = ReturnType<typeof message.useMessage>[0];

const CameraPanel = lazy(() => import('./CameraPanel'));
const RobotPanel = lazy(() => import('./RobotPanel'));
const DevicePanel = lazy(() => import('./DevicePanel'));
const ProductionPanel = lazy(() => import('./ProductionPanel'));
const DiagnosticsPanel = lazy(() => import('./DiagnosticsPanel'));
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
const SpcDashboard = lazy(() => import('./SpcDashboard'));

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
  spcOpen: boolean;
  setSpcOpen: (value: boolean) => void;
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
  productionOpen, setProductionOpen, spcOpen, setSpcOpen, diagnosticsOpen, setDiagnosticsOpen,
  aboutOpen, setAboutOpen, hardwareProvenanceOpen, setHardwareProvenanceOpen,
  pluginsOpen, setPluginsOpen, refreshCatalog
}: EditorPanelsProps) {
  return (
    <>
      <LazyPanel open={camerasOpen} label="相机" onClose={() => setCamerasOpen(false)}><CameraPanel open onClose={() => setCamerasOpen(false)} /></LazyPanel>
      <LazyPanel open={robotsOpen} label="机器人" onClose={() => setRobotsOpen(false)}><RobotPanel open onClose={() => setRobotsOpen(false)} /></LazyPanel>
      <LazyPanel open={devicesOpen} label="设备" onClose={() => setDevicesOpen(false)}><DevicePanel open onClose={() => setDevicesOpen(false)} /></LazyPanel>
      <LazyPanel open={framesOpen} label="图像帧" onClose={() => setFramesOpen(false)}><FrameTreePanel open onClose={() => setFramesOpen(false)} /></LazyPanel>
      <LazyPanel open={calibrationOpen} label="标定" onClose={() => setCalibrationOpen(false)}><CalibrationPanel
        open
        onClose={() => setCalibrationOpen(false)}
        canApplyToSelectedNode={['calibration.planar', 'calibration.cameraIntrinsics', 'image.undistort', 'calibration.handEye2D', 'calibration.handEye3D'].includes(String(selectedNode?.data.typeKey ?? ''))}
        selectedNodeType={selectedNode ? String(selectedNode.data.typeKey) : undefined}
        onApplyToSelectedNode={applyCalibrationParameters}
      /></LazyPanel>
      <LazyPanel open={jobsOpen} label="作业" onClose={() => setJobsOpen(false)}><JobPanel open onClose={() => setJobsOpen(false)} workflow={workflowPayload} prepareWorkflowChange={workflowChanges.prepare} onRunResult={applyResult} onOpenValidation={() => setValidationOpen(true)} /></LazyPanel>
      <LazyPanel open={tracesOpen} label="追溯" onClose={() => { setTracesOpen(false); setTraceRunId(undefined); }}>
        <TracePanel open initialRunId={traceRunId} onClose={() => { setTracesOpen(false); setTraceRunId(undefined); }} />
      </LazyPanel>
      <LazyPanel open={modelPackagesOpen} label="模型包" onClose={() => setModelPackagesOpen(false)}><ModelPackagePanel open onClose={() => setModelPackagesOpen(false)} /></LazyPanel>
      <LazyPanel open={modelAssetsOpen} label="模型资产" onClose={() => setModelAssetsOpen(false)}><ModelAssetPanel open onClose={() => setModelAssetsOpen(false)} /></LazyPanel>
      <LazyPanel open={validationOpen} label="数据集验证" onClose={() => setValidationOpen(false)}><DatasetValidationPanel open onClose={() => setValidationOpen(false)} currentWorkflow={workflowPayload} /></LazyPanel>
      <LazyPanel open={tuningOpen} label="参数调优" onClose={() => setTuningOpen(false)}><ParameterTuningPanel open onClose={() => setTuningOpen(false)} currentWorkflow={workflowPayload} catalog={catalog} initialNodeId={selectedId} prepareWorkflowChange={workflowChanges.prepare} onApplied={() => { setTuningOpen(false); messageApi.success('已将调优后的候选方案应用到设计器'); }} /></LazyPanel>
      <LazyPanel open={modulesOpen} label="流程模块" onClose={() => setModulesOpen(false)}><WorkflowModulePanel open onClose={() => setModulesOpen(false)} currentWorkflow={workflowPayload} selectedNodeId={selectedId} selectedNodeType={String(selectedNode?.data.typeKey ?? '')} prepareWorkflowChange={workflowChanges.prepare} onInsertModule={insertModule} onUpgradeSelected={upgradeSelectedModule} /></LazyPanel>
      <LazyPanel open={productionOpen} label="生产运行" onClose={() => setProductionOpen(false)}><ProductionPanel open onClose={() => setProductionOpen(false)} /></LazyPanel>
      <LazyPanel open={spcOpen} label="SPC 看板" onClose={() => setSpcOpen(false)}><SpcDashboard open onClose={() => setSpcOpen(false)} /></LazyPanel>
      <LazyPanel open={diagnosticsOpen} label="诊断" onClose={() => setDiagnosticsOpen(false)}><DiagnosticsPanel open onClose={() => setDiagnosticsOpen(false)} /></LazyPanel>
      <LazyPanel open={aboutOpen} label="关于" onClose={() => setAboutOpen(false)}><AboutPanel open onClose={() => setAboutOpen(false)} /></LazyPanel>
      <LazyPanel open={hardwareProvenanceOpen} label="硬件溯源" onClose={() => setHardwareProvenanceOpen(false)}><HardwareProvenancePanel open onClose={() => setHardwareProvenanceOpen(false)} /></LazyPanel>
      <LazyPanel open={pluginsOpen} label="插件包" onClose={() => setPluginsOpen(false)}><PluginPackagePanel open onClose={() => setPluginsOpen(false)} onCatalogRefresh={refreshCatalog} /></LazyPanel>
    </>
  );
}
