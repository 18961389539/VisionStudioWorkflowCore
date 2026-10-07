export type LayoutPreset = 'edit' | 'image';

export type LayoutState = {
  preset: LayoutPreset | 'custom';
  /** 左栏（工具箱）宽度 px */
  left: number;
  /** 右栏（属性面板）宽度 px */
  right: number;
  /** 下栏（结果区）高度 px */
  bottom: number;
  /** 图像区占中央高度的百分比 */
  image: number;
  leftCollapsed: boolean;
  rightCollapsed: boolean;
  bottomCollapsed: boolean;
  bottomMaximized: boolean;
};

export const LAYOUT_LIMITS = {
  left: { min: 160, max: 460 },
  right: { min: 220, max: 560 },
  bottom: { min: 80, max: 560 },
  image: { min: 15, max: 80 }
} as const;

type PresetSizes = Pick<LayoutState, 'left' | 'right' | 'bottom' | 'image'>;

export const LAYOUT_PRESETS: Record<LayoutPreset, PresetSizes> = {
  // 流程编辑：三栏均衡，结果区给足表格空间
  edit: { left: 220, right: 290, bottom: 220, image: 40 },
  // 图像调试：放大图像区、收窄结果区，属性面板加宽便于调参数
  image: { left: 200, right: 340, bottom: 140, image: 62 }
};

export const DEFAULT_LAYOUT: LayoutState = {
  preset: 'edit',
  ...LAYOUT_PRESETS.edit,
  leftCollapsed: false,
  rightCollapsed: false,
  bottomCollapsed: false,
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
      right: readNumber(parsed.right, DEFAULT_LAYOUT.right, LAYOUT_LIMITS.right.min, LAYOUT_LIMITS.right.max),
      bottom: readNumber(parsed.bottom, DEFAULT_LAYOUT.bottom, LAYOUT_LIMITS.bottom.min, LAYOUT_LIMITS.bottom.max),
      image: readNumber(parsed.image, DEFAULT_LAYOUT.image, LAYOUT_LIMITS.image.min, LAYOUT_LIMITS.image.max),
      leftCollapsed: readFlag(parsed.leftCollapsed, false),
      rightCollapsed: readFlag(parsed.rightCollapsed, false),
      bottomCollapsed: readFlag(parsed.bottomCollapsed, false),
      bottomMaximized: readFlag(parsed.bottomMaximized, false)
    };
  } catch {
    return DEFAULT_LAYOUT;
  }
}

export function persistLayout(state: LayoutState) {
  try {
    window.localStorage.setItem(LAYOUT_KEY, JSON.stringify(state));
  } catch {
    // 隐私模式 / 禁用存储时忽略持久化失败
  }
}