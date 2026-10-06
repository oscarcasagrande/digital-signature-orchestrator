import { useEffect, useRef, useState, type ReactNode } from 'react';

interface Props {
  title: string;
  description: ReactNode;
  confirmLabel: string;
  /** Shows an optional free-text reason field when set. */
  reasonLabel?: string;
  busy?: boolean;
  onConfirm: (reason: string) => void;
  onCancel: () => void;
  children?: ReactNode;
}

/** Modal confirmation for manual operations: Esc cancels, focus starts on the first control. */
export function ConfirmDialog({ title, description, confirmLabel, reasonLabel, busy, onConfirm, onCancel, children }: Props) {
  const [reason, setReason] = useState('');
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const first = ref.current?.querySelector<HTMLElement>('textarea, select, button');
    first?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onCancel();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onCancel]);

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-labelledby="dialog-title" ref={ref}>
        <h2 id="dialog-title">{title}</h2>
        <div className="dialog-body">{description}</div>
        {children}
        {reasonLabel && (
          <label className="field">
            {reasonLabel}
            <textarea value={reason} maxLength={500} rows={3} onChange={(e) => setReason(e.target.value)} />
          </label>
        )}
        <div className="dialog-actions">
          <button type="button" className="secondary" onClick={onCancel} disabled={busy}>
            Cancel
          </button>
          <button type="button" className="primary" onClick={() => onConfirm(reason.trim())} disabled={busy}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  );
}
