const TONES: Record<string, string> = {
  COMPLETED: 'good', SIGNED: 'good', READY: 'good', OK: 'good', DELIVERED: 'good', PASSED: 'good', PROCESSING: 'info',
  SIGNATURE_IN_PROGRESS: 'info', PARTIALLY_SIGNED: 'info', FINALIZING: 'info', VALIDATING: 'info', CREATED: 'neutral',
  DOCUMENT_RECEIVED: 'neutral', READY_FOR_SIGNATURE: 'neutral', RETRY_PENDING: 'warn', SUSPENDED: 'warn', PENDING: 'warn',
  FAILED: 'bad', REJECTED: 'bad', EXPIRED: 'bad', DLQ: 'bad', MANUAL_ACTION: 'bad', ALERT: 'bad', CANCELLED: 'muted',
  IN_PROGRESS: 'info', SENT: 'info', SENDING: 'info', PLANNED: 'neutral', CONFIRMED: 'good', LOCKED: 'bad',
};

export function StatusBadge({ value, label }: { value: string; label?: string }) {
  return <span className={'badge badge-' + (TONES[value] ?? 'neutral')}>{label ?? value.replace(/_/g, ' ')}</span>;
}
