import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Button, theme as antdTheme } from 'antd';
import { afterEach, describe, expect, it } from 'vitest';
import { ThemeProvider, useThemeMode } from '../theme';

function ThemeProbe() {
  const { mode, toggle } = useThemeMode();
  const { token } = antdTheme.useToken();
  return (
    <div>
      <span data-testid="mode">{mode}</span>
      <span data-testid="primary">{token.colorPrimary}</span>
      <span data-testid="bg">{token.colorBgLayout}</span>
      <Button onClick={toggle}>切换主题</Button>
    </div>
  );
}

describe('ThemeProvider token synchronization', () => {
  afterEach(() => {
    cleanup();
    window.localStorage.clear();
    delete document.documentElement.dataset.vsTheme;
  });

  it('maps --vs-* dark and light tokens into Ant Design ConfigProvider', () => {
    render(
      <ThemeProvider>
        <ThemeProbe />
      </ThemeProvider>
    );

    expect(screen.getByTestId('mode').textContent).toBe('dark');
    expect(document.documentElement.dataset.vsTheme).toBe('dark');
    expect(screen.getByTestId('bg').textContent?.toLowerCase()).toBe('#11151b');
    const darkPrimary = screen.getByTestId('primary').textContent?.toLowerCase();
    expect(darkPrimary).not.toBe('#1677ff');

    fireEvent.click(screen.getByRole('button', { name: '切换主题' }));

    expect(screen.getByTestId('mode').textContent).toBe('light');
    expect(document.documentElement.dataset.vsTheme).toBe('light');
    expect(screen.getByTestId('primary').textContent?.toLowerCase()).toBe('#1668dc');
    expect(screen.getByTestId('bg').textContent?.toLowerCase()).toBe('#f4f6f9');
    expect(screen.getByTestId('primary').textContent?.toLowerCase()).not.toBe(darkPrimary);
  });
});
