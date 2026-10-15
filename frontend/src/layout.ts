export type LayoutPreset = 'edit' | 'image';

export type LayoutState = {
  preset: LayoutPreset | 'custom';
  /** 左栏（工具箱）宽度 px */
  left: number;
  /** 下栏（结果区）高度 px */
  bottom: number;
  /** auto 随窗口取约 27%；手动拖动后保留像素高度。旧布局迁入 auto。 */
  bottomSizing: 'auto' | 'manual';
  leftCollapsed: boolean;
  bottomCollapsed: boolean;
  bottomMaximized: boolean;
};

export const LAYOUT_LIMITS = {
  left: { min: 160, max: 460 },
  bottom: { min: 80, max: 560 }
} as const;

type PresetSizes = Pick<LayoutState, 'left' | 'bottom'>;

export const LAYOUT_PRESETS: Record<LayoutPreset, PresetSizes> = {
  // 流程编辑：画布优先，结果区给足表格空间
  edit: { left: 220, bottom: 220 },
  // 宽画布：工具箱收至最小宽度，图像仍通过独立查看入口打开。
  image: { left: 160, bottom: 140 }
};

export const DEFAULT_LAYOUT: LayoutState = {
  preset: 'edit',
  ...LAYOUT_PRESETS.edit,
  bottomSizing: 'auto',
  leftCollapsed: false,
  // 首屏收起结果区：首次使用/尚未运行时画布优先（2026-10-11 主页改进 P0）。
  // 运行出结果、点开节点详情、执行校验时都会自动展开，用户手动展开后由 localStorage 记住。
  bottomCollapsed: true,
  bottomMaximized: false
};

/** 中央区域的最小保留宽度/高度，避免分隔条把画布挤没 */
export const MIN_CENTER_WIDTH = 360;
export const MIN_WORKSPACE_HEIGHT = 120;

const LAYOUT_KEY = 'visionstudio.layout';

export const clampNumber = (value: number, min: number, max: number) => Math.min(Math.max(value, min), max);

const readNumber = (value: unknown, fallback: number, min: number, max: number) =>
  typeof value === 'number' && Number.isFinite(value) ? clampNumber(value, min, max) : fallback;

const readFlag = (value: unknown, fallback: boolean) => (typeof value === 'boolean' ? value : fallback);

export function readLayout(): LayoutState {
  try {
    const raw = window.localStorage.getItem(LAYOUT_KEY);
    if (!raw) return DEFAULT_LAYOUT;
    const parsed = JSON.parse(raw) as Partial<LayoutState>;
    const preset = parsed.preset === 'edit' || parsed.preset === 'image' || parsed.preset === 'custom' ? parsed.preset : DEFAULT_LAYOUT.preset;
    return {
      preset,
      left: readNumber(parsed.left, DEFAULT_LAYOUT.left, LAYOUT_LIMITS.left.min, LAYOUT_LIMITS.left.max),
      bottom: readNumber(parsed.bottom, DEFAULT_LAYOUT.bottom, LAYOUT_LIMITS.bottom.min, LAYOUT_LIMITS.bottom.max),
      bottomSizing: parsed.bottomSizing === 'manual' ? 'manual' : 'auto',
      leftCollapsed: readFlag(parsed.leftCollapsed, false),
      bottomCollapsed: readFlag(parsed.bottomCollapsed, DEFAULT_LAYOUT.bottomCollapsed),
      bottomMaximized: readFlag(parsed.bottomMaximized, false)
    };
  } catch {
    return DEFAULT_LAYOUT;
  }
}

export function automaticResultHeight(shellHeight: number): number {
  return clampNumber(Math.round(Math.max(0, shellHeight - 48) * 0.27), 120, LAYOUT_LIMITS.bottom.max);
}

export function persistLayout(state: LayoutState) {
  try {
    window.localStorage.setItem(LAYOUT_KEY, JSON.stringify(state));
  } catch {
    // 隐私模式 / 禁用存储时忽略持久化失败
  }
}
