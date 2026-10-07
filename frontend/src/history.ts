import type { Edge, Node } from '@xyflow/react';

/**
 * Snapshot of the editable graph document (nodes / edges / selection) used for undo & redo.
 * Snapshots store the state BEFORE a mutation, so undo restores the exact timeline state —
 * including incidental changes (breakpoints, positions) made since the previous entry.
 */
export type GraphSnapshot = {
  nodes: Node[];
  edges: Edge[];
  selectedId: string;
};

type HistoryEntry = {
  label: string;
  coalesceKey?: string;
  at: number;
  snapshot: GraphSnapshot;
};

const HISTORY_LIMIT = 100;
/** Consecutive edits with the same coalesce key inside this window collapse into one undo step. */
const COALESCE_WINDOW_MS = 700;

/** Structural clone with a JSON fallback; node data is plain JSON, so both paths are safe. */
function clone<T>(value: T): T {
  if (typeof structuredClone === 'function') return structuredClone(value);
  return JSON.parse(JSON.stringify(value)) as T;
}

export function captureGraph(nodes: Node[], edges: Edge[], selectedId: string): GraphSnapshot {
  return { nodes: clone(nodes), edges: clone(edges), selectedId };
}

/** Returns fresh clones so neither the stack entry nor React Flow can mutate the other side. */
export function restoreGraph(snapshot: GraphSnapshot): GraphSnapshot {
  return { nodes: clone(snapshot.nodes), edges: clone(snapshot.edges), selectedId: snapshot.selectedId };
}

/**
 * Undo/redo stack over whole-graph snapshots.
 *
 * Coalescing: slider dragging, typing in a parameter field or resizing the ROI fire dozens of
 * mutations per second. Pushing with the same coalesce key inside the window keeps the FIRST
 * snapshot (the pre-edit state) and only extends the window, so one Ctrl+Z reverts the whole
 * continuous edit instead of one slider tick.
 */
export class GraphHistory {
  private undoStack: HistoryEntry[] = [];
  private redoStack: HistoryEntry[] = [];

  get canUndo(): boolean {
    return this.undoStack.length > 0;
  }

  get canRedo(): boolean {
    return this.redoStack.length > 0;
  }

  get undoLabel(): string | undefined {
    return this.undoStack[this.undoStack.length - 1]?.label;
  }

  get redoLabel(): string | undefined {
    return this.redoStack[this.redoStack.length - 1]?.label;
  }

  /** Records the pre-mutation state. Any push invalidates the redo branch. */
  push(label: string, snapshot: GraphSnapshot, coalesceKey?: string): void {
    const now = Date.now();
    const last = this.undoStack[this.undoStack.length - 1];
    if (coalesceKey && last?.coalesceKey === coalesceKey && now - last.at <= COALESCE_WINDOW_MS) {
      last.at = now;
      this.redoStack = [];
      return;
    }
    this.undoStack.push({ label, coalesceKey, at: now, snapshot });
    if (this.undoStack.length > HISTORY_LIMIT) this.undoStack.shift();
    this.redoStack = [];
  }

  /** Pops the previous state; `current` is pushed onto the redo stack. */
  undo(current: GraphSnapshot): GraphSnapshot | undefined {
    const entry = this.undoStack.pop();
    if (!entry) return undefined;
    this.redoStack.push({ label: entry.label, at: Date.now(), snapshot: current });
    return entry.snapshot;
  }

  /** Pops the redo state; `current` is pushed back onto the undo stack. */
  redo(current: GraphSnapshot): GraphSnapshot | undefined {
    const entry = this.redoStack.pop();
    if (!entry) return undefined;
    this.undoStack.push({ label: entry.label, at: Date.now(), snapshot: current });
    return entry.snapshot;
  }

  /** Drops the whole timeline (bulk loads: demo switch, workflow load, draft restore). */
  reset(): void {
    this.undoStack = [];
    this.redoStack = [];
  }
}
