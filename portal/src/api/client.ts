import { loadSession, notifyUnauthorized, refreshSession, validToken } from './auth';
import type {
  ArtifactItem, CallbackDelivery, DeadLetter, DeadLetterParams, DownloadLink, JournalEvent, ListParams, OperationItem, Paged,
  NewProcessRequest, ProcessDetail, ProcessListItem, ProviderInfo, ReconcileResult, ReprocessResult, UploadedDocument,
} from './types';

export const OPERATOR_KEY = 'operatorId';
const OPERATOR_PATTERN = /^[A-Za-z0-9._@:-]{1,100}$/;

/** Keeps only the characters the API accepts and limits the length to 100. */
export function sanitizeOperatorId(raw: string): string {
  return raw.replace(/[^A-Za-z0-9._@:-]/g, '').slice(0, 100);
}

export function getOperator(): string | null {
  try {
    const v = localStorage.getItem(OPERATOR_KEY);
    return v && OPERATOR_PATTERN.test(v) ? v : null;
  } catch {
    return null;
  }
}

/** Stores the operator id (sanitized). Returns the stored value or null when nothing valid remains. */
export function setOperator(raw: string): string | null {
  const v = sanitizeOperatorId(raw.trim());
  try {
    if (v) localStorage.setItem(OPERATOR_KEY, v);
    else localStorage.removeItem(OPERATOR_KEY);
  } catch {
    /* storage unavailable: the id only lives for this call */
  }
  return v || null;
}

export class ApiError extends Error {
  constructor(
    public status: number,
    public title: string,
    public detail?: string,
    public errors?: Record<string, string[]>,
    public correlationId?: string,
  ) {
    super(detail ? `${title}: ${detail}` : title);
  }
}

async function request<T>(method: string, path: string, body?: unknown, extraHeaders: Record<string, string> = {}, retried = false): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...extraHeaders };
  const isForm = typeof FormData !== 'undefined' && body instanceof FormData;
  if (body !== undefined && !isForm) headers['Content-Type'] = 'application/json';
  const operator = getOperator();
  const token = await validToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  else if (operator) headers['X-Operator-Id'] = operator;

  let res: Response;
  try {
    res = await fetch(path, { method, headers, body: body === undefined ? undefined : isForm ? (body as FormData) : JSON.stringify(body) });
  } catch (e) {
    throw new ApiError(0, 'Network error', e instanceof Error ? e.message : 'The API is unreachable');
  }
  if (res.status === 401) {
    // The access token was refused (e.g. clock skew or revoked early): renew it once and replay the request before giving up.
    if (!retried && token && loadSession()?.refreshToken) {
      const renewed = await refreshSession().catch(() => null);
      if (renewed) return request<T>(method, path, body, extraHeaders, true);
    }
    notifyUnauthorized();
  }
  if (!res.ok) {
    let problem: { title?: string; detail?: string; errors?: Record<string, string[]>; correlationId?: string } = {};
    try {
      problem = await res.json();
    } catch {
      /* not a problem+json body */
    }
    throw new ApiError(res.status, problem.title ?? `HTTP ${res.status}`, problem.detail, problem.errors, problem.correlationId);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

function qs(params: Record<string, string | number | boolean | undefined>): string {
  const sp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== '') sp.set(k, String(v));
  const s = sp.toString();
  return s ? `?${s}` : '';
}

const p = (id: string) => `/v1/signature-processes/${encodeURIComponent(id)}`;

export const api = {
  listProcesses: (params: ListParams) => request<Paged<ProcessListItem>>('GET', `/v1/signature-processes${qs({ ...params })}`),
  getProcess: (id: string) => request<ProcessDetail>('GET', p(id)),
  listOperations: (id: string) => request<Paged<OperationItem>>('GET', `${p(id)}/operations?pageSize=200`),
  listEvents: (id: string, page = 1, pageSize = 50) => request<Paged<JournalEvent>>('GET', `${p(id)}/events${qs({ page, pageSize })}`),
  listArtifacts: (id: string) => request<{ items: ArtifactItem[] }>('GET', `${p(id)}/artifacts`),
  listCallbacks: (id: string) => request<{ items: CallbackDelivery[] }>('GET', `${p(id)}/callbacks`),
  getProvider: (id: string) => request<ProviderInfo>('GET', `${p(id)}/provider`),
  listDeadLetters: (params: DeadLetterParams) => request<Paged<DeadLetter>>('GET', `/v1/dead-letters${qs({ ...params })}`),
  cancel: (id: string) => request<unknown>('POST', `${p(id)}/cancel`),
  reconcile: (id: string) => request<ReconcileResult>('POST', `${p(id)}/reconcile`),
  reprocess: (id: string, body: { operationId?: string; reason?: string }) => request<ReprocessResult>('POST', `${p(id)}/retry`, body),
  downloadLink: (id: string, body: { artifactId: string }) => request<DownloadLink>('POST', `${p(id)}/download-link`, body),
  uploadDocument: (file: File) => {
    const form = new FormData();
    form.append('file', file, file.name);
    return request<UploadedDocument>('POST', '/v1/document-uploads', form);
  },
  createProcess: (body: NewProcessRequest, idempotencyKey: string) =>
    request<{ processId: string }>('POST', '/v1/signature-processes', body, { 'Idempotency-Key': idempotencyKey }),
};
