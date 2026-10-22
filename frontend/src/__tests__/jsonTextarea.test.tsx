import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import JsonTextarea from '../components/JsonTextarea';
import { isJsonParameter } from '../jsonExamples';

beforeEach(() => {
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

describe('JsonTextarea', () => {
  it('formats valid JSON without changing its data', () => {
    const onChange = vi.fn();
    render(<JsonTextarea label="样本 JSON" description="samples 字段说明" value='{"b":1,"a":[2,3]}' onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: '格式化 JSON' }));
    const formatted = onChange.mock.calls[0][0] as string;
    expect(formatted).toContain('\n  "b": 1');
    expect(JSON.parse(formatted)).toEqual({ b: 1, a: [2, 3] });
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.getByText('samples 字段说明')).toBeTruthy();
  });

  it('reports a parse error and leaves invalid text untouched', () => {
    const onChange = vi.fn();
    render(<JsonTextarea label="样本 JSON" value='{"b":' onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: '格式化 JSON' }));
    expect(screen.getByRole('alert').textContent).toContain('JSON 无效');
    expect(onChange).not.toHaveBeenCalled();
  });
});

describe('JSON parameter detection', () => {
  it('recognizes JSON fields whose names do not end in Json', () => {
    expect(isJsonParameter('measure.metrology', 'objects')).toBe(true);
    expect(isJsonParameter('jlvision.worldPlane', 'cameraParameters')).toBe(true);
    expect(isJsonParameter('jlvision.worldPlane', 'worldPoseJson')).toBe(true);
    expect(isJsonParameter('measure.metrology', 'objectsText')).toBe(false);
  });
});

describe('JsonTextarea examples', () => {
  it('offers an example only while the field is empty or a placeholder', () => {
    const onChange = vi.fn();
    const example = '{"samples":[]}';
    const view = render(<JsonTextarea label="样本 JSON" value="{}" example={example} onChange={onChange} />);
    fireEvent.click(screen.getByRole('button', { name: '插入示例' }));
    expect(onChange).toHaveBeenCalledWith(example);

    view.rerender(<JsonTextarea label="样本 JSON" value='{"samples":[1]}' example={example} onChange={onChange} />);
    expect(screen.queryByRole('button', { name: '插入示例' })).toBeNull();
  });
});
