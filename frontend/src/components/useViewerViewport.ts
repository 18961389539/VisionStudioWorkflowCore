import { useEffect, useRef, useState } from 'react';
import type { VisionRoi } from '../types';
import { clamp, clipSegmentToImage, roiBounds } from './imageViewerUtils';
import type { MeasurementGeometry } from './imageViewerUtils';
import type { ViewState } from './imageViewerTypes';

type Bounds = { x: number; y: number; width: number; height: number };

export type ViewerViewportOptions = {
  hostRef: React.RefObject<HTMLDivElement | null>;
  actualWidth: number;
  actualHeight: number;
  roi?: VisionRoi;
  focusTick?: number;
  measurementGeometry?: MeasurementGeometry;
};

/**
 * 画布视口：容器尺寸观测、缩放/平移状态与三种适配目标（全图 / 定位选区 / 定位搜索线）。
 *
 * 抽出来的原因：这段是"视图坐标系"的唯一权威——适配目标需要跨渲染保持（fitTargetRef），
 * 图像尺寸与容器尺寸任一变化都要按**当前意图**重适配，而不是无条件重置成"适应窗口"。
 * 把它留在 1100 行的组件里，任何手势改动都可能顺手破坏这条规则。
 */
export function useViewerViewport({
  hostRef,
  actualWidth,
  actualHeight,
  roi,
  focusTick,
  measurementGeometry,
}: ViewerViewportOptions) {
  const [size, setSize] = useState({ width: 400, height: 260 });
  const [view, setView] = useState<ViewState>({ scale: 1, x: 0, y: 0 });

  // 视图适配目标：fit=全图，定位选区=ROI 包围盒。容器尺寸变化时保持同一目标重适配，
  // 避免"适应窗口"式重置覆盖刚触发的定位意图。
  const fitTargetRef = useRef<Bounds | undefined>(undefined);
  const lastFocusTick = useRef(focusTick ?? 0);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;
    const observer = new ResizeObserver(([entry]) => {
      const r = entry.contentRect;
      setSize({ width: Math.max(10, r.width), height: Math.max(10, r.height) });
    });
    observer.observe(host);
    return () => observer.disconnect();
  }, [hostRef]);

  const setViewToTarget = (target: Bounds, pad: number) => {
    // 留白按容器尺寸自适应：容器很矮（如底部面板展开时）时固定留白会挤掉大部分可用空间
    const effPad = Math.min(pad, Math.min(size.width, size.height) * 0.12);
    const scale = clamp(Math.min((size.width - effPad * 2) / target.width, (size.height - effPad * 2) / target.height), 0.02, 30);
    setView({
      scale,
      x: size.width / 2 - (target.x + target.width / 2) * scale,
      y: size.height / 2 - (target.y + target.height / 2) * scale
    });
  };

  const fit = () => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setViewToTarget(fitTargetRef.current, 12);
  };

  const oneToOne = () => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setView({ scale: 1, x: (size.width - actualWidth) / 2, y: (size.height - actualHeight) / 2 });
  };

  const focusSearchLine = () => {
    if (measurementGeometry?.type !== 'line') return;
    const segment = clipSegmentToImage(measurementGeometry.start, measurementGeometry.end, actualWidth, actualHeight);
    if (!segment) return;
    const margin = 48 + (measurementGeometry.profileWidth ?? 0) / 2;
    const x = Math.max(0, Math.min(segment.start.x, segment.end.x) - margin);
    const y = Math.max(0, Math.min(segment.start.y, segment.end.y) - margin);
    fitTargetRef.current = { x, y,
      width: Math.max(2, Math.min(actualWidth, Math.max(segment.start.x, segment.end.x) + margin) - x),
      height: Math.max(2, Math.min(actualHeight, Math.max(segment.start.y, segment.end.y) + margin) - y) };
    setViewToTarget(fitTargetRef.current, 24);
  };

  // 图像（尺寸）变化：目标重置为全图并适配
  useEffect(() => {
    if (!actualWidth || !actualHeight) return;
    fitTargetRef.current = { x: 0, y: 0, width: actualWidth, height: actualHeight };
    setViewToTarget(fitTargetRef.current, 12);
  }, [actualWidth, actualHeight]);

  // 容器尺寸变化：按当前目标（全图或 ROI）重新适配
  useEffect(() => {
    if (!fitTargetRef.current) return;
    setViewToTarget(fitTargetRef.current, 12);
  }, [size.width, size.height]);

  // 定位选区：属性面板每次触发递增 focusTick，这里把视图适配到当前 ROI 包围盒
  useEffect(() => {
    if (focusTick === undefined || focusTick === lastFocusTick.current) return;
    lastFocusTick.current = focusTick;
    if (!roi) return;
    fitTargetRef.current = roiBounds(roi);
    setViewToTarget(fitTargetRef.current, 28);
  }, [focusTick, roi, size.width, size.height]);

  return { size, view, setView, setViewToTarget, fit, oneToOne, focusSearchLine };
}
