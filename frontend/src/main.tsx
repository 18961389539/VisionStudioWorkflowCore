import React from 'react';
import ReactDOM from 'react-dom/client';
import '@xyflow/react/dist/style.css';
import './styles.css';
import App from './App';
import SecurityGate from './security';
import { ThemeProvider } from './theme';
import { I18nProvider } from './i18n/provider';
import ErrorBoundary from './components/ErrorBoundary';

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
