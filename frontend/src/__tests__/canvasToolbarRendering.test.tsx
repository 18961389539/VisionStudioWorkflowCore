import { fireEvent, render, screen } from '@testing-library/react';
import { createRef, type ComponentProps } from 'react';
import { expect, it, vi } from 'vitest';
import CanvasEditingToolbar from '../components/CanvasEditingToolbar';

const counter = vi.hoisted(() => ({ renders: 0 }));
vi.mock('../i18n/provider', () => ({
  useI18n: () => { counter.renders++; return { lang: 'zh' }; },
}));

it('skips the real editing toolbar for position-only frames but updates labels, selection, history and actions', () => {
  counter.renders = 0;
  const focus = vi.fn();
  const firstUndo = vi.fn();
  const secondUndo = vi.fn();
  const noop = () => {};
  const props: ComponentProps<typeof CanvasEditingToolbar> = {
    nodeCount: 1, selectedId: 'n', configurableSelected: true, selectedCount: 1, groupableSelectionCount: 1,
    selectedEdgeIds: [], anyBusy: false, canUndo: false, canRedo: false, undo: firstUndo, redo: noop,
    deleteElements: noop, compactNodes: false, setCompactNodes: noop, openNodeConfiguration: noop,
    imagePreviewOpen: false, focusMode: false, exitFocusMode: noop, setImagePreviewOpen: noop, addCanvasNote: noop,
    createGroup: noop, alignSelection: noop, searchInputRef: createRef(), searchQuery: 'node', setSearchQuery: noop,
    searchOpen: true, setSearchOpen: noop, searchMatches: [{ id: 'n', label: 'node' }],
    focusNode: focus,
  };
  const element = (value: ComponentProps<typeof CanvasEditingToolbar>) => <CanvasEditingToolbar {...value} />;
  const view = render(element(props));
  const initial = counter.renders;
  for (let x = 1; x <= 120; x++) view.rerender(element({
    ...props, searchMatches: [{ id: 'n', label: 'node' }],
  }));
  expect(counter.renders).toBe(initial);
  const changed = { ...props, canUndo: true, undo: secondUndo, selectedCount: 2,
    searchMatches: [{ id: 'n', label: 'node renamed' }] };
  view.rerender(element(changed));
  expect(screen.getByText('node renamed')).toBeTruthy();
  expect(screen.getByRole('toolbar', { name: '多选对齐工具' })).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: '撤销' }));
  expect(secondUndo).toHaveBeenCalledOnce();
  expect(firstUndo).not.toHaveBeenCalled();
  fireEvent.click(screen.getByRole('option', { name: /node renamed/ }));
  expect(focus).toHaveBeenCalledWith('n');
});
