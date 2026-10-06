import { ApiError } from '../api/client';

export interface NoticeState {
  kind: 'success' | 'error';
  text: string;
}

export function errorNotice(e: unknown): NoticeState {
  if (e instanceof ApiError) return { kind: 'error', text: e.detail ? e.title + ': ' + e.detail : e.title };
  return { kind: 'error', text: e instanceof Error ? e.message : 'Unexpected error' };
}

export function Notice({ notice, onDismiss }: { notice: NoticeState | null; onDismiss: () => void }) {
  if (!notice) return null;
  return (
    <div className={'notice notice-' + notice.kind} role={notice.kind === 'error' ? 'alert' : 'status'}>
      <span>{notice.text}</span>
      <button type="button" className="link" onClick={onDismiss} aria-label="Dismiss message">
        ×
      </button>
    </div>
  );
}

export function LoadError({ error, onRetry }: { error: ApiError; onRetry: () => void }) {
  return (
    <div className="notice notice-error" role="alert">
      <span>
        {error.status === 0 ? 'The API is unreachable. ' : ''}
        {error.message}
      </span>
      <button type="button" onClick={onRetry}>
        Try again
      </button>
    </div>
  );
}
