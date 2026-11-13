import React from 'react';
import ReactDOM from 'react-dom/client';
import '@xyflow/react/dist/style.css';
import './styles.css';
import App from './App';
import SecurityGate from './security';
import { ThemeProvider } from './theme';
import { I18nProvider } from './i18n/provider';
import { getInitialLang } from './i18n/lang';
import { hasCatalogData } from './i18n/catalog';
import ErrorBoundary from './components/ErrorBoundary';

async function bootstrap() {
  // 中文词表按需加载：英文模式完全不下发；中文首屏先取回词表再挂载，
  // 避免界面先显示英文、chunk 到达后再闪成中文。
  if (getInitialLang() === 'zh' && !hasCatalogData()) {
    try { await import('./i18n/catalog.zh'); } catch { /* chunk 不可用时回落英文原文 */ }
  }

  ReactDOM.createRoot(document.getElementById('root')!).render(
    <React.StrictMode>
      <ErrorBoundary>
        {/* I18nProvider 在 ThemeProvider 外层：antd 的内置文案（分页、日期、空态）要跟随当前语言 */}
        <I18nProvider>
          <ThemeProvider>
            <SecurityGate><App /></SecurityGate>
          </ThemeProvider>
        </I18nProvider>
      </ErrorBoundary>
    </React.StrictMode>
  );
}

void bootstrap();
