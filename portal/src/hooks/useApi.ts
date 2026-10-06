import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '../api/client';

export interface ApiState<T> {
  data: T | null;
  error: ApiError | null;
  loading: boolean;
  reload: () => void;
}

/** Runs `fn` on mount and whenever `deps` change; `reload()` runs it again. Stale responses are ignored. */
export function useApi<T>(fn: () => Promise<T>, deps: unknown[]): ApiState<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  const seq = useRef(0);

  useEffect(() => {
    const mine = ++seq.current;
    setLoading(true);
    fn()
      .then((d) => {
        if (mine !== seq.current) return;
        setData(d);
        setError(null);
      })
      .catch((e: unknown) => {
        if (mine !== seq.current) return;
        setError(e instanceof ApiError ? e : new ApiError(0, 'Unexpected error', e instanceof Error ? e.message : String(e)));
      })
      .finally(() => {
        if (mine === seq.current) setLoading(false);
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);

  const reload = useCallback(() => setTick((t) => t + 1), []);
  return { data, error, loading, reload };
}
