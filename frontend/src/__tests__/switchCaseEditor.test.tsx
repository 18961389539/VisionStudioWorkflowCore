import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import SwitchCaseEditor from '../components/SwitchCaseEditor';

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const casesOf = (value: string) => JSON.parse(value) as Array<string | { label: string; port: string }>;

describe('SwitchCaseEditor', () => {
  it('edits, appends and removes labels while keeping casesJson in sync', () => {
    const onChange = vi.fn();
    const view = render(<SwitchCaseEditor value='["OK","NG"]' onChange={onChange} />);
    expect((screen.getByRole('textbox', { name: /分支 1 标签|Case 1 label/ }) as HTMLInputElement).value).toBe('OK');

    fireEvent.change(screen.getByRole('textbox', { name: /分支 1 标签|Case 1 label/ }), { target: { value: 'PASS' } });
    expect(onChange).toHaveBeenLastCalledWith('["PASS","NG"]');

    view.rerender(<SwitchCaseEditor value='["PASS","NG"]' onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /添加分支|Add case/ }));
    const added = casesOf(onChange.mock.calls.at(-1)![0]);
    expect(added).toHaveLength(3);
    expect(added.slice(0, 2)).toEqual(['PASS', 'NG']);

    view.rerender(<SwitchCaseEditor value='["PASS","NG","分支 3"]' onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /删除分支 2|Remove case 2/ }));
    expect(casesOf(onChange.mock.lastCall?.[0])).toEqual([{ label: 'PASS', port: 'case1' }, { label: '分支 3', port: 'case3' }]);
  });

  it('moves the fixed port with its label and retains it when the label is renamed', () => {
    const onChange = vi.fn();
    const view = render(<SwitchCaseEditor value='["OK","NG"]' onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /上移分支 2|Move case 2 up/ }));
    const moved = onChange.mock.lastCall?.[0];
    expect(casesOf(moved)).toEqual([{ label: 'NG', port: 'case2' }, { label: 'OK', port: 'case1' }]);

    view.rerender(<SwitchCaseEditor value={moved} onChange={onChange} />);
    fireEvent.change(screen.getByRole('textbox', { name: /分支 1 标签|Case 1 label/ }), { target: { value: 'REVIEW' } });
    expect(casesOf(onChange.mock.lastCall?.[0])).toEqual([{ label: 'REVIEW', port: 'case2' }, { label: 'OK', port: 'case1' }]);
  });

  it('confirms removal of a connected branch and reserves its port until the draft is applied', () => {
    const onChange = vi.fn();
    const connections = new Map([['case2', { affectedLabels: ['保存失败图像', '多路汇合'], mergeResolved: true }]]);
    const view = render(<SwitchCaseEditor value='["A","B","C"]' connections={connections} onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: /删除分支 2|Remove case 2/ }));
    expect(screen.getByText('保存失败图像')).toBeTruthy();
    expect(screen.getByText('多路汇合')).toBeTruthy();
    expect(onChange).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: /确认删除|^Remove case$/ }));
    const removed = onChange.mock.lastCall?.[0];
    expect(casesOf(removed)).toEqual([{ label: 'A', port: 'case1' }, { label: 'C', port: 'case3' }]);

    view.rerender(<SwitchCaseEditor value={removed} connections={connections} onChange={onChange} />);
    expect(screen.getByRole('status').textContent).toContain('case2');
    fireEvent.click(screen.getByRole('button', { name: /添加分支|Add case/ }));
    expect(casesOf(onChange.mock.lastCall?.[0])).toMatchObject([{ port: 'case1' }, { port: 'case3' }, { port: 'case4' }]);
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
    const labels = casesOf(onChange.mock.calls.at(-1)![0]);
    expect(labels).toHaveLength(3);
    expect(labels.slice(0, 2)).toEqual(['分支 3', 'Case 3']);
    expect(new Set(labels).size).toBe(labels.length);
  });

  it('keeps invalid JSON editable and can restore the default cases', () => {
    const onChange = vi.fn();
    render(<SwitchCaseEditor value='not-json' onChange={onChange} />);
    expect((screen.getByRole('textbox', { name: /分支 JSON|Cases JSON/ }) as HTMLTextAreaElement).value).toBe('not-json');
    fireEvent.click(screen.getByRole('button', { name: /恢复默认分支|Restore defaults/ }));
    expect(casesOf(onChange.mock.calls.at(-1)![0])).toEqual(['OK', 'NG']);
  });
});
