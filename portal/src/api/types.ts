// Types mirror the JSON returned by the platform API (/v1). Personal data arrives already masked.

export type Sla = 'OK' | 'ALERT';

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface ProcessListItem {
  processId: string;
  externalId: string;
  documentFileName: string | null;
  provider: string | null;
  signersSigned: number;
  signersTotal: number;
  businessStatus: string;
  operationalStatus: string;
  createdAt: string;
  updatedAt: string;
  sla: Sla;
  progress: Progress;
}

export type StepStatus = 'PENDING' | 'IN_PROGRESS' | 'COMPLETED' | 'FAILED' | 'CANCELLED';

export interface Step {
  order: number;
  kind: 'DOCUMENT_RECEIVED' | 'CONFIRMATION' | 'SIGNATURE' | 'FINAL_DOCUMENT' | 'CALLBACK';
  signerId: string | null;
  channel: string | null;
  label: string;
  status: StepStatus;
}

export interface Progress {
  completedSteps: number;
  totalSteps: number;
  currentStep: Step | null;
}

export interface ProgressDetail extends Progress {
  steps: Step[];
}

export interface Confirmation {
  channel: string;
  status: string; // PLANNED | SENDING | SENT | EXPIRED | CONFIRMED | LOCKED
  sendCount: number;
  lastSentAt: string | null;
  expiresAt: string | null;
  attemptsRemaining: number | null;
  confirmedAt: string | null;
}

export interface Signer {
  id: string;
  externalId: string;
  name: string;
  document: string; // masked by the API
  signed: boolean;
  signedAt: string | null;
  email?: string | null; // masked by the API
  phone?: string | null; // masked by the API
  signatureType?: string | null;
  order?: number | null;
  confirmations?: Confirmation[];
}

export interface NewSigner {
  name: string;
  document: string;
  email?: string;
  phone?: string;
  order?: number;
}

export interface NewProcessRequest {
  externalId: string;
  document: { fileName: string; source: { type: 'UPLOAD'; uploadId: string } };
  signers: NewSigner[];
  defaults: { signatureType: string; confirmation: string[] };
}

export interface UploadedDocument {
  uploadId: string;
  fileName: string;
  size: number;
  sha256: string;
}

export interface ProcessDetail {
  processId: string;
  externalId: string;
  businessStatus: string;
  operationalStatus: string;
  signatureType: string;
  signers: Signer[];
  callback: { url?: string; callbackId?: string } | null;
  identityValidations: Array<string | { type: string; required?: boolean }> | null;
  correlationId: string;
  createdAt: string;
  updatedAt: string;
  completedAt: string | null;
  documentFileName: string | null;
  provider: string | null;
  sla: Sla;
  progress: ProgressDetail;
}

export interface OperationItem {
  operationId: string;
  processId: string;
  type: string;
  status: string;
  attempt: number;
  maxAttempts: number;
  nextRetryAt: string | null;
  output: unknown;
  error: { message?: string; type?: string; errorClass?: string } | null;
  createdAt: string;
  updatedAt: string;
}

export interface JournalEvent {
  eventId: string;
  processId: string;
  type: string;
  timestamp: string;
  actor: { type: string; id: string };
  metadata: Record<string, unknown>;
  correlationId: string;
  causationId: string | null;
}

export interface ArtifactItem {
  artifactId: string;
  type: string;
  contentType: string;
  sha256: string;
  size: number;
  fileName: string;
  createdAt: string;
}

export interface CallbackDelivery {
  deliveryId: string;
  operationId: string;
  eventId: string;
  eventType: string;
  processStatus: string;
  destination: string;
  status: string;
  attempts: number;
  lastStatusCode: number | null;
  lastError: string | null;
  occurredAt: string;
  createdAt: string;
  deliveredAt: string | null;
}

export interface DeadLetter {
  deadLetterId: string;
  processId: string;
  operationId: string;
  operationType: string;
  domain: string;
  queue: string;
  errorClass: string;
  reason: string;
  attempts: number;
  createdAt: string;
  resolvedAt: string | null;
}

export interface ProviderInfo {
  provider: string;
  providerProcessId: string;
  externalReference: string;
  normalizedStatus: string;
  metadata: unknown;
  updatedAt: string;
}

export interface ReconcileResult {
  processId: string;
  outcome: 'CORRECTED' | 'CONSISTENT' | 'NOT_APPLICABLE' | 'PROVIDER_ERROR';
  internalStatus: string;
  providerStatus: string | null;
  corrected: boolean;
  resultingStatus: string | null;
  error: string | null;
}

export interface ReprocessResult {
  processId: string;
  operationId: string;
  operationType: string;
  operationalStatus: string;
}

export interface DownloadLink {
  url: string;
  expiresAt: string;
}

export interface ListParams {
  status?: string;
  operationalStatus?: string;
  q?: string;
  page?: number;
  pageSize?: number;
}

export interface DeadLetterParams {
  domain?: string;
  resolved?: boolean;
  processId?: string;
  page?: number;
  pageSize?: number;
}
