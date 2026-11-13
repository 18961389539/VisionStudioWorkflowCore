/**
 * 编辑器的低频面板 / 弹窗开关（巨型组件拆分，2026-11-13）。
 * 每个开关独立持有，不做互斥；与拆分前一致。
 */
import { useState } from 'react';

export function useEditorPanels() {
  const [pluginsOpen, setPluginsOpen] = useState(false);
  const [camerasOpen, setCamerasOpen] = useState(false);
  const [robotsOpen, setRobotsOpen] = useState(false);
  const [devicesOpen, setDevicesOpen] = useState(false);
  const [jobsOpen, setJobsOpen] = useState(false);
  const [tracesOpen, setTracesOpen] = useState(false);
  /** 从运行结果跳转到追溯时预置的运行 ID（打开即定位到那次运行） */
  const [traceRunId, setTraceRunId] = useState<string>();
  const [commandOpen, setCommandOpen] = useState(false);
  const [validationOpen, setValidationOpen] = useState(false);
  const [modelPackagesOpen, setModelPackagesOpen] = useState(false);
  const [modelAssetsOpen, setModelAssetsOpen] = useState(false);
  const [tuningOpen, setTuningOpen] = useState(false);
  const [modulesOpen, setModulesOpen] = useState(false);
  const [calibrationOpen, setCalibrationOpen] = useState(false);
  const [framesOpen, setFramesOpen] = useState(false);
  const [productionOpen, setProductionOpen] = useState(false);
  const [diagnosticsOpen, setDiagnosticsOpen] = useState(false);
  const [hardwareProvenanceOpen, setHardwareProvenanceOpen] = useState(false);
  const [aboutOpen, setAboutOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  return {
    pluginsOpen, setPluginsOpen, camerasOpen, setCamerasOpen, robotsOpen, setRobotsOpen, devicesOpen, setDevicesOpen,
    jobsOpen, setJobsOpen, tracesOpen, setTracesOpen, traceRunId, setTraceRunId, commandOpen, setCommandOpen,
    validationOpen, setValidationOpen, modelPackagesOpen, setModelPackagesOpen, modelAssetsOpen, setModelAssetsOpen,
    tuningOpen, setTuningOpen, modulesOpen, setModulesOpen, calibrationOpen, setCalibrationOpen, framesOpen, setFramesOpen,
    productionOpen, setProductionOpen, diagnosticsOpen, setDiagnosticsOpen, hardwareProvenanceOpen, setHardwareProvenanceOpen,
    aboutOpen, setAboutOpen, shortcutsOpen, setShortcutsOpen
  };
}
