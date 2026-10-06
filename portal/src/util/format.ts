export const TERMINAL_STATUSES = ['COMPLETED', 'FAILED', 'CANCELLED', 'EXPIRED', 'REJECTED'];
export const REPROCESSABLE_OPERATION_STATUSES = ['FAILED', 'DLQ', 'RETRY_PENDING'];

export const BUSINESS_STATUSES = [
  'CREATED', 'DOCUMENT_RECEIVED', 'VALIDATING', 'READY_FOR_SIGNATURE', 'SIGNATURE_IN_PROGRESS', 'PARTIALLY_SIGNED', 'SIGNED',
  'FINALIZING', 'COMPLETED', 'FAILED', 'CANCELLED', 'EXPIRED', 'REJECTED',
];
export const OPERATIONAL_STATUSES = ['READY', 'PROCESSING', 'RETRY_PENDING', 'SUSPENDED', 'DLQ', 'MANUAL_ACTION'];
export const DOMAINS = ['signature-provider', 'artifact', 'identity-proofing', 'callback'];

export const isTerminal = (status: string) => TERMINAL_STATUSES.includes(status);

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'medium' });
}

export function formatTime(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

export function formatBytes(n: number): string {
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
  return (n / (1024 * 1024)).toFixed(1) + ' MB';
}

const LABELS: Record<string, string> = {
  PROCESS_CREATED: 'Process created',
  DOCUMENT_RECEIVED: 'Original document received',
  DOCUMENT_STORED: 'Original document stored',
  IDENTITY_VALIDATION_REQUESTED: 'Identity proofing started',
  IDENTITY_VALIDATED: 'Identity verified',
  PROVIDER_SELECTED: 'Provider selected',
  PROVIDER_REQUEST_SENT: 'Request sent to provider',
  PROVIDER_ACCEPTED: 'Provider accepted the process',
  SIGNER_NOTIFIED: 'Signers notified',
  SIGNER_OPENED_DOCUMENT: 'Signer opened the document',
  SIGNATURE_STARTED: 'Sent to provider for signature',
  SIGNER_SIGNED: 'Signer signed',
  SIGNATURE_COMPLETED: 'Signed',
  SIGNATURE_REJECTED: 'Signature rejected',
  SIGNED_DOCUMENT_RECEIVED: 'Final document received',
  FINAL_DOCUMENT_STORED: 'Final document stored',
  PROCESS_COMPLETED: 'Process completed',
  PROCESS_CANCELLED: 'Process cancelled',
  CALLBACK_REQUESTED: 'Callback scheduled',
  CALLBACK_DELIVERED: 'Callback delivered',
  DOCUMENT_DOWNLOADED: 'Document downloaded',
  DOWNLOAD_LINK_ISSUED: 'Download link issued',
  OPERATION_RETRY_SCHEDULED: 'Retry scheduled',
  OPERATION_FAILED: 'Operation failed',
  OPERATION_DEAD_LETTERED: 'Operation sent to the DLQ',
  OPERATION_REPROCESS_REQUESTED: 'Reprocess requested',
  RECONCILIATION_REQUESTED: 'Reconciliation requested',
  RECONCILIATION_DISCREPANCY_FOUND: 'Provider discrepancy found',
  RECONCILIATION_CORRECTED: 'State corrected by reconciliation',
  RECONCILIATION_PROVIDER_ERROR: 'Provider error during reconciliation',
  PROVIDER_METADATA_INSPECTED: 'Provider metadata inspected',
};

export function eventLabel(type: string): string {
  return LABELS[type] ?? type;
}

export function actorLabel(actor: { type: string; id: string }): string {
  return actor.type.toLowerCase() + ':' + actor.id;
}
