import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ConfigProvider, theme } from 'antd';
import zhCN from 'antd/locale/zh_CN';
import enUS from 'antd/locale/en_US';
import { useI18n } from './i18n/provider';
import { STORAGE_KEYS, readStored, writeStored } from './storage';

export type ThemeMode = 'dark' | 'light';

type ThemeContextValue = {
  mode: ThemeMode;
  toggle: () => void;
};


const ThemeContext = createContext<ThemeContextValue>({ mode: 'dark', toggle: () => undefined });

function readStoredMode(): ThemeMode {
  return readStored(STORAGE_KEYS.themeMode) === 'light' ? 'light' : 'dark';
}

function applyMode(mode: ThemeMode) {
  document.documentElement.dataset.vsTheme = mode;
}

const ANT_THEME_TOKENS: Record<ThemeMode, Record<string, string | number>> = {
  dark: {
    borderRadius: 6,
    fontSize: 13,
    colorPrimary: '#5d94d7',
    colorInfo: '#71a8ee',
    colorSuccess: '#4f9d69',
    colorWarning: '#e3a04a',
    colorError: '#e64b59',
    colorBgLayout: '#11151b',
    colorBgContainer: '#171c23',
    colorBgElevated: '#181d25',
    colorBorder: '#2a313c',
    colorBorderSecondary: '#242b34',
    colorText: '#d8dee9',
    colorTextSecondary: '#aeb9c7'
  },
  light: {
    borderRadius: 6,
    fontSize: 13,
    colorPrimary: '#1668dc',
    colorInfo: '#0958d9',
    colorSuccess: '#2f7d4a',
    colorWarning: '#9a5b00',
    colorError: '#c62828',
    colorBgLayout: '#f4f6f9',
    colorBgContainer: '#ffffff',
    colorBgElevated: '#ffffff',
    colorBorder: '#d7dde5',
    colorBorderSecondary: '#e8ecf1',
    colorText: '#1f2328',
    colorTextSecondary: '#3f4753'
  }
};

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
    writeStored(STORAGE_KEYS.themeMode, mode);
  }, [mode]);

  const toggle = useCallback(() => setMode((current) => (current === 'dark' ? 'light' : 'dark')), []);
  const value = useMemo(() => ({ mode, toggle }), [mode, toggle]);

  return (
    <ThemeContext.Provider value={value}>
      <ConfigProvider
        locale={lang === 'en' ? enUS : zhCN}
        // antd 默认给两个汉字的按钮中间插空格（"运 行"/"保 存"），那是西文排版习惯；
        // 中文两字词是整体，插空格反而像被拆开。关掉后按钮更紧凑，也便于按名称定位。
        button={{ autoInsertSpace: false }}
        theme={{
          algorithm: mode === 'dark' ? theme.darkAlgorithm : theme.defaultAlgorithm,
          token: ANT_THEME_TOKENS[mode]
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