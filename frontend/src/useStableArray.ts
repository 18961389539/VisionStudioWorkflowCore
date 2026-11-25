import { useLayoutEffect, useRef } from 'react';

export function useStableArray<T>(values: T[], equal: (left: T, right: T) => boolean): T[] {
  const committed = useRef(values);
  const previous = committed.current;
  const stable = previous.length === values.length && previous.every((item, index) => equal(item, values[index]))
    ? previous : values;
  useLayoutEffect(() => { committed.current = stable; }, [stable]);
  return stable;
}
