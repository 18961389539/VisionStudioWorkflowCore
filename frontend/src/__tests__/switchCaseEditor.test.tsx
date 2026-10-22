import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import SwitchCaseEditor from '../components/SwitchCaseEditor';

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const labelsOf = (value: string) => JSON.parse(value) as string[];

describe('SwitchCaseEditor', () => {
  it('edits, appends and removes labels while keeping casesJson in sync', () => {
    const onChange = vi.fn();
    const view = render(<SwitchCaseEditor value='["OK","NG"]' onChange={onChange} />);
    expect((screen.getByRole('textbox', { name: /分支 1 标签|Case 1 label/ }) as HTMLInputElement).value).toBe('OK');

    fireEvent.change(screen.getByRole('textbox', { name: /分支 1 标签|Case 1 label/ }), { target: { value: 'PASS' } });
    expect(labelsOf(onChange.mock.calls.at(-1)![0])).toEqual(['PASS', 'NG']);

    view.rerender(<SwitchCaseEditor value='["PASS","NG"]' onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /添加分支|Add case/ }));
    const added = labelsOf(onChange.mock.calls.at(-1)![0]);
    expect(added).toHaveLength(3);
    expect(added.slice(0, 2)).toEqual(['PASS', 'NG']);

    view.rerender(<SwitchCaseEditor value='["PASS","NG","分支 3"]' onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /删除分支 2|Remove case 2/ }));
    expect(labelsOf(onChange.mock.calls.at(-1)![0])).toEqual(['PASS', '分支 3']);
  });

  it('flags empty and duplicate labels but keeps them editable', () => {
    const onChange = vi.fn();
    render(<SwitchCaseEditor value='["OK","OK",""]' onChange={onChange} />);
    expect(screen.getByRole('alert').textContent).toContain('分支标签不能为空');
    expect(screen.getByRole('alert').textContent).toContain('分支标签必须唯一');
    expect(screen.getAllByRole('textbox')).toHaveLength(3);
  });

  it('adds a unique default label even when imported labels already use the generated name', () => {
    const onChange = vi.fn();
    render(<SwitchCaseEditor value='["分支 3","Case 3"]' onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /添加分支|Add case/ }));
    const labels = labelsOf(onChange.mock.calls.at(-1)![0]);
    expect(labels).toHaveLength(3);
    expect(labels.slice(0, 2)).toEqual(['分支 3', 'Case 3']);
    expect(new Set(labels).size).toBe(labels.length);
  });

  it('keeps invalid JSON editable and can restore the default cases', () => {
    const onChange = vi.fn();
    render(<SwitchCaseEditor value='not-json' onChange={onChange} />);
    expect((screen.getByRole('textbox', { name: /分支 JSON|Cases JSON/ }) as HTMLTextAreaElement).value).toBe('not-json');
    fireEvent.click(screen.getByRole('button', { name: /恢复默认分支|Restore defaults/ }));
    expect(labelsOf(onChange.mock.calls.at(-1)![0])).toEqual(['OK', 'NG']);
  });
});
