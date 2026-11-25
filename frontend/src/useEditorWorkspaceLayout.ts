import { useCallback, useEffect, useRef, useState } from 'react';
import {
  HEADER_HEIGHT, LAYOUT_LIMITS, LAYOUT_PRESETS, MIN_CENTER_WIDTH, MIN_WORKSPACE_HEIGHT,
  automaticResultHeight, clampNumber, persistLayout, readLayout, type LayoutPreset, type LayoutState
} from './layout';
import { COLLAPSED_RESULT_BAR_HEIGHT } from './appEditorShared';
import { readStored, removeStored, writeStored, STORAGE_KEYS } from './storage';

/**
 * 编辑器工作区布局与视口尺寸：管理左右/上下分隔、布局预设、专注模式快照与引导卡状态。
 */
export function useEditorWorkspaceLayout() {
  const [layout, setLayout] = useState<LayoutState>(readLayout);
  const [shellSize, setShellSize] = useState({ width: window.innerWidth, height: window.innerHeight });
  const shellRef = useRef<HTMLDivElement>(null);
  const [imagePreviewOpen, setImagePreviewOpen] = useState(false);
  const [focusMode, setFocusMode] = useState(false);
  const focusSnapshotRef = useRef<LayoutState | null>(null);
  const [onboardingOpen, setOnboardingOpen] = useState(() => readStored(STORAGE_KEYS.onboardingDone) !== '1');

  const closeOnboarding = useCallback(() => {
    writeStored(STORAGE_KEYS.onboardingDone, '1');
    setOnboardingOpen(false);
  }, []);

  const reopenOnboarding = useCallback(() => {
    removeStored(STORAGE_KEYS.onboardingDone);
    setOnboardingOpen(true);
  }, []);

  useEffect(() => { persistLayout(layout); }, [layout]);

  useEffect(() => {
    const element = shellRef.current;
    if (!element) return;
    const observer = new ResizeObserver(([entry]) => {
      const width = entry.contentRect.width;
      const height = entry.contentRect.height;
      setShellSize((current) => (Math.abs(current.width - width) < 1 && Math.abs(current.height - height) < 1 ? current : { width, height }));
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const updateLayout = useCallback((patch: Partial<LayoutState>) => {
    setLayout((current) => ({ ...current, ...patch }));
  }, []);

  // 退出专注的统一收口：restoreSnapshot=true 还原进入前的布局快照；
  // false 表示布局改由调用方接管（切换布局预设），快照直接丢弃，避免覆盖用户的新选择。
  const exitFocusMode = useCallback((restoreSnapshot: boolean) => {
    const snapshot = focusSnapshotRef.current;
    focusSnapshotRef.current = null;
    setFocusMode(false);
    if (restoreSnapshot && snapshot) updateLayout({ ...snapshot });
  }, [updateLayout]);

  const applyLayoutPreset = useCallback((preset: LayoutPreset) => {
    if (focusMode) exitFocusMode(false);
    setLayout((current) => ({
      ...current, preset, ...LAYOUT_PRESETS[preset], bottomSizing: 'auto',
      leftCollapsed: false, bottomCollapsed: false, bottomMaximized: false
    }));
  }, [exitFocusMode, focusMode]);

  const toggleFocusMode = useCallback(() => {
    if (focusMode) { exitFocusMode(true); return; }
    focusSnapshotRef.current = layout;
    setFocusMode(true);
    updateLayout({ leftCollapsed: true, bottomCollapsed: true, bottomMaximized: false });
  }, [exitFocusMode, focusMode, layout, updateLayout]);

  const previewVar = useCallback((name: string, value: string) => {
    shellRef.current?.style.setProperty(name, value);
  }, []);

  const leftMax = Math.max(LAYOUT_LIMITS.left.min, shellSize.width - MIN_CENTER_WIDTH);
  const shownLeftBase = clampNumber(layout.left, LAYOUT_LIMITS.left.min, leftMax);
  const bottomMax = Math.max(LAYOUT_LIMITS.bottom.min, shellSize.height - HEADER_HEIGHT - MIN_WORKSPACE_HEIGHT);
  const normalBottom = layout.bottomSizing === 'manual' ? layout.bottom : automaticResultHeight(shellSize.height);
  const shownBottomBase = layout.bottomMaximized ? bottomMax : clampNumber(normalBottom, LAYOUT_LIMITS.bottom.min, bottomMax);
  const shownLeft = layout.leftCollapsed ? 0 : shownLeftBase;
  const shownBottom = layout.bottomCollapsed ? COLLAPSED_RESULT_BAR_HEIGHT : shownBottomBase;
  const presetSizes = LAYOUT_PRESETS[layout.preset === 'custom' ? 'edit' : layout.preset];

  return {
    shellRef,
    layout,
    updateLayout,
    applyLayoutPreset,
    focusMode,
    toggleFocusMode,
    exitFocusMode,
    imagePreviewOpen,
    setImagePreviewOpen,
    onboardingOpen,
    closeOnboarding,
    reopenOnboarding,
    previewVar,
    leftMax,
    bottomMax,
    shownLeft,
    shownBottomBase,
    shownBottom,
    presetSizes
  };
}
