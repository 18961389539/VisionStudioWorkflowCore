import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { I18nProvider, useI18n } from '../i18n/provider';
import { LANG_STORAGE_KEY } from '../i18n/lang';
import Toolbox from '../components/Toolbox';

/**
 * 语言切换接线（2026-10-25）。
 *
 * 此前词表（2258 条）、Provider、catalog 本地化都已具备，但 Provider 没挂、也没有任何切换入口，
 * 语言只能靠手改 localStorage。这组用例守住"挂上了 + 切换会真的重渲染消费方"。
 */

function Probe() {
  const { lang, toggleLang, t, dateLocale } = useI18n();
  return (
    <div>
      <span data-testid="lang">{lang}</span>
      <span data-testid="common">{t('common.none')}</span>
      <span data-testid="locale">{dateLocale}</span>
      <button type="button" onClick={toggleLang}>切换</button>
    </div>
  );
}

beforeEach(() => {
  window.localStorage.clear();
  document.documentElement.lang = 'zh-CN';
});
afterEach(() => cleanup());

describe('i18n 接线', () => {
  it('默认中文，切换后词条、日期区域与 <html lang> 同步变化并持久化', () => {
    render(<I18nProvider><Probe /></I18nProvider>);
    expect(screen.getByTestId('lang').textContent).toBe('zh');
    expect(screen.getByTestId('common').textContent).toBe('无');
    expect(screen.getByTestId('locale').textContent).toBe('zh-CN');

    fireEvent.click(screen.getByRole('button', { name: '切换' }));

    expect(screen.getByTestId('lang').textContent).toBe('en');
    expect(screen.getByTestId('common').textContent).toBe('none');
    expect(screen.getByTestId('locale').textContent).toBe('en-US');
    expect(document.documentElement.lang).toBe('en');
    expect(window.localStorage.getItem(LANG_STORAGE_KEY)).toBe('en');
  });

  it('已存储的偏好优先于默认值', () => {
    window.localStorage.setItem(LANG_STORAGE_KEY, 'en');
    render(<I18nProvider><Probe /></I18nProvider>);
    expect(screen.getByTestId('lang').textContent).toBe('en');
  });

  it('消费 i18n 的组件会随切换重新渲染（工具箱搜索框文案）', () => {
    render(<I18nProvider><Probe /><Toolbox catalog={[]} recentTypes={[]} onAddNode={() => undefined} /></I18nProvider>);
    expect(screen.getByLabelText('搜索节点')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: '切换' }));
    expect(screen.getByLabelText('Search nodes')).toBeTruthy();
  });

  it('Provider 之外使用不抛错（面板/测试可独立渲染）', () => {
    render(<Probe />);
    expect(screen.getByTestId('lang').textContent).toBe('zh');
  });
});
