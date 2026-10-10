import React from 'react';
import ReactDOM from 'react-dom/client';
import '@xyflow/react/dist/style.css';
import './styles.css';
import App from './App';
import SecurityGate from './security';
import { ThemeProvider } from './theme';
import ErrorBoundary from './components/ErrorBoundary';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ErrorBoundary>
      <ThemeProvider>
        <SecurityGate><App /></SecurityGate>
      </ThemeProvider>
    </ErrorBoundary>
  </React.StrictMode>
);
