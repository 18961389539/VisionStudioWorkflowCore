import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ConfigProvider, theme } from 'antd';
import zhCN from 'antd/locale/zh_CN';
import enUS from 'antd/locale/en_US';
import { useI18n } from './i18n/provider';

export type ThemeMode = 'dark' | 'light';

type ThemeContextValue = {
  mode: ThemeMode;
  toggle: () => void;
};

const STORAGE_KEY = 'visionstudio.theme-mode';
const ThemeContext = createContext<ThemeContextValue>({ mode: 'dark', toggle: () => undefined });

function readStoredMode(): ThemeMode {
  try {
    return window.localStorage.getItem(STORAGE_KEY) === 'light' ? 'light' : 'dark';
  } catch {
    return 'dark';
  }
}

function applyMode(mode: ThemeMode) {
  document.documentElement.dataset.vsTheme = mode;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  // antd 内置文案跟随界面语言（分页、日期、空态、Select 无数据…）。此前固定 zhCN，
  // 英文界面里这些角落仍是中文（2026-10-25 接线）。
  const { lang } = useI18n();
  const [mode, setMode] = useState<ThemeMode>(() => {
    const initial = readStoredMode();
    applyMode(initial);
    return initial;
  });

  useEffect(() => {
    applyMode(mode);
    try {
      window.localStorage.setItem(STORAGE_KEY, mode);
    } catch {
      // 隐私模式 / 禁用存储时忽略持久化失败
    }
  }, [mode]);

  const toggle = useCallback(() => setMode((current) => (current === 'dark' ? 'light' : 'dark')), []);
  const value = useMemo(() => ({ mode, toggle }), [mode, toggle]);

  return (
    <ThemeContext.Provider value={value}>
      <ConfigProvider
        locale={lang === 'en' ? enUS : zhCN}
        theme={{
          algorithm: mode === 'dark' ? theme.darkAlgorithm : theme.defaultAlgorithm,
          token: {
            borderRadius: 6,
            fontSize: 13
          }
        }}
      >
        {children}
      </ConfigProvider>
    </ThemeContext.Provider>
  );
}

export function useThemeMode() {
  return useContext(ThemeContext);
}