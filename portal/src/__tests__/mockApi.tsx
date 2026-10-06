import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { vi } from 'vitest';
import { App } from '../App';
import { OperatorProvider } from '../components/OperatorContext';
import type {
  ArtifactItem, CallbackDelivery, DeadLetter, JournalEvent, OperationItem, ProcessDetail, ProcessListItem, ProgressDetail, ProviderInfo, Step,
} from '../api/types';

export interface Call {
  method: string;
  path: string; // pathname and query
  pathname: string;
  headers: Record<string, string>;
  body?: any;
}

type Reply = { status?: number; body?: unknown };
type Handler = Reply | ((call: Call) => Reply | Promise<Reply>);
export interface Route {
  method?: string;
  /** string: exact pathname; RegExp: tested against pathname + query */
  match: string | RegExp;
  handler: Handler;
}

/** Replaces fetch with a tiny in-memory API and records every call. */
export function mockApi(routes: Route[]) {
  const calls: Call[] = [];
  const impl = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://localhost');
    const call: Call = {
      method: (init?.method ?? 'GET').toUpperCase(),
      path: url.pathname + url.search,
      pathname: url.pathname,
      headers: { ...(init?.headers as Record<string, string> | undefined) },
      body: init?.body ? parseBody(init.body) : undefined,
    };
    calls.push(call);
    const route = routes.find(
      (r) => (r.method ?? 'GET') === call.method && (typeof r.match === 'string' ? r.match === call.pathname : r.match.test(call.path)),
    );
    if (!route) return new Response(JSON.stringify({ title: 'Not mocked', detail: call.method + ' ' + call.path }), { status: 500 });
    const reply = typeof route.handler === 'function' ? await route.handler(call) : route.handler;
    const status = reply.status ?? 200;
    if (status === 204) return new Response(null, { status });
    return new Response(JSON.stringify(reply.body ?? {}), {
      status,
      headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
    });
  };
  vi.stubGlobal('fetch', vi.fn(impl));
  return {
    calls,
    find: (method: string, pathRegex: RegExp) => calls.filter((c) => c.method === method && pathRegex.test(c.path)),
  };
}

export function renderApp(path: string, operator?: string) {
  if (operator) localStorage.setItem('operatorId', operator);
  return render(
    <MemoryRouter initialEntries={[path]}>
      <OperatorProvider>
        <App />
      </OperatorProvider>
    </MemoryRouter>,
  );
}

// ---- fixtures -------------------------------------------------------------------------------------------------

export const listItem = (o: Partial<ProcessListItem> = {}): ProcessListItem => ({
  processId: 'sig_1', externalId: 'CONTRACT-1', documentFileName: 'contract.pdf', provider: 'SIMULATED', signersSigned: 1, signersTotal: 2,
  businessStatus: 'SIGNATURE_IN_PROGRESS', operationalStatus: 'READY', createdAt: '2026-10-06T02:41:00Z', updatedAt: '2026-10-06T02:42:00Z',
  sla: 'OK', progress: { completedSteps: 3, totalSteps: 7, currentStep: { order: 4, kind: 'SIGNATURE', signerId: 'sgn_2', channel: null, label: 'Assinatura - Joao Lima', status: 'IN_PROGRESS' } }, ...o,
});

export const detail = (o: Partial<ProcessDetail> = {}): ProcessDetail => ({
  processId: 'sig_1', externalId: 'CONTRACT-1', businessStatus: 'SIGNATURE_IN_PROGRESS', operationalStatus: 'READY', signatureType: 'ADVANCED',
  signers: [
    { id: 'sgn_1', externalId: 'c1', name: 'Maria Souza', document: '*********09', signed: true, signedAt: '2026-10-06T02:45:00Z' },
    { id: 'sgn_2', externalId: 'c2', name: 'Joao Lima', document: '*********09', signed: false, signedAt: null },
  ],
  callback: { callbackId: 'ORIGINACAO_CALLBACK' }, identityValidations: ['FACE_MATCH', { type: 'LIVENESS', required: false }],
  correlationId: 'corr-1', createdAt: '2026-10-06T02:41:00Z', updatedAt: '2026-10-06T02:46:00Z', completedAt: null,
  documentFileName: 'contract.pdf', provider: 'SIMULATED', sla: 'OK', progress: detailProgress(), ...o,
});

export const op = (o: Partial<OperationItem> = {}): OperationItem => ({
  operationId: 'op_1', processId: 'sig_1', type: 'DOCUMENT_DOWNLOAD', status: 'COMPLETED', attempt: 1, maxAttempts: 4, nextRetryAt: null,
  output: null, error: null, createdAt: '2026-10-06T02:41:01Z', updatedAt: '2026-10-06T02:41:02Z', ...o,
});

export const ev = (type: string, i: number, o: Partial<JournalEvent> = {}): JournalEvent => ({
  eventId: 'evt_' + i, processId: 'sig_1', type, timestamp: '2026-10-06T02:4' + (i % 10) + ':00Z', actor: { type: 'SYSTEM', id: 'WORKER' },
  metadata: { n: i }, correlationId: 'corr-1', causationId: i > 0 ? 'evt_' + (i - 1) : null, ...o,
});

export const artifact = (o: Partial<ArtifactItem> = {}): ArtifactItem => ({
  artifactId: 'art_1', type: 'SIGNED_DOCUMENT', contentType: 'application/pdf', sha256: 'a'.repeat(64), size: 2048, fileName: 'signed.pdf',
  createdAt: '2026-10-06T02:50:00Z', ...o,
});

export const delivery = (o: Partial<CallbackDelivery> = {}): CallbackDelivery => ({
  deliveryId: 'cbd_1', operationId: 'op_cb', eventId: 'evt_cb', eventType: 'SIGNATURE_PROCESS.COMPLETED', processStatus: 'COMPLETED',
  destination: 'ORIGINACAO_CALLBACK', status: 'DLQ', attempts: 4, lastStatusCode: 503, lastError: 'Destination responded HTTP 503',
  occurredAt: '2026-10-06T02:50:00Z', createdAt: '2026-10-06T02:50:00Z', deliveredAt: null, ...o,
});

export const deadLetter = (o: Partial<DeadLetter> = {}): DeadLetter => ({
  deadLetterId: 'dlq_1', processId: 'sig_1', operationId: 'op_2', operationType: 'PROVIDER_CREATE_PROCESS', domain: 'signature-provider',
  queue: 'signature-provider-dlq', errorClass: 'TRANSIENT', reason: 'Simulated provider outage (HTTP 503)', attempts: 4,
  createdAt: '2026-10-06T02:55:00Z', resolvedAt: null, ...o,
});

export const providerInfo = (): ProviderInfo => ({
  provider: 'SIMULATED', providerProcessId: 'sim_123', externalReference: 'sig_1', normalizedStatus: 'SIGNED',
  metadata: { Mode: 'Auto', Signers: 2 }, updatedAt: '2026-10-06T02:49:00Z',
});

const paged = <T,>(items: T[], total = items.length, page = 1, pageSize = 50) => ({ items, page, pageSize, total });

/** Routes serving one process: pass overrides to customize any section. */
export function processRoutes(over: {
  detail?: ProcessDetail; ops?: OperationItem[]; events?: JournalEvent[]; eventsTotal?: number; artifacts?: ArtifactItem[];
  callbacks?: CallbackDelivery[]; pendingDl?: DeadLetter[]; resolvedDl?: DeadLetter[]; id?: string;
} = {}): Route[] {
  const id = over.id ?? 'sig_1';
  const base = '/v1/signature-processes/' + id;
  const events = over.events ?? [ev('PROCESS_CREATED', 0), ev('DOCUMENT_STORED', 1), ev('SIGNATURE_STARTED', 2)];
  return [
    { match: base, handler: { body: over.detail ?? detail() } },
    { match: base + '/operations', handler: { body: paged(over.ops ?? [op()]) } },
    {
      match: base + '/events',
      handler: (c) => {
        const page = Number(new URL('http://x' + c.path).searchParams.get('page') ?? '1');
        const size = 50;
        return { body: paged(events.slice((page - 1) * size, page * size), over.eventsTotal ?? events.length, page, size) };
      },
    },
    { match: base + '/artifacts', handler: { body: { items: over.artifacts ?? [artifact()] } } },
    { match: base + '/callbacks', handler: { body: { items: over.callbacks ?? [delivery({ status: 'DELIVERED', deliveredAt: '2026-10-06T02:51:00Z' })] } } },
    {
      match: /^\/v1\/dead-letters\?.*resolved=false/,
      handler: { body: paged(over.pendingDl ?? []) },
    },
    { match: /^\/v1\/dead-letters\?.*resolved=true/, handler: { body: paged(over.resolvedDl ?? []) } },
  ];
}

function parseBody(body: unknown): unknown {
  if (typeof FormData !== 'undefined' && body instanceof FormData) {
    return Object.fromEntries([...body.entries()].map(([k, v]) => [k, typeof v === 'string' ? v : { fileName: v.name, size: v.size }]));
  }
  const text = String(body);
  try {
    return JSON.parse(text);
  } catch {
    return text; // e.g. a form-urlencoded token request
  }
}

/** Unsigned JWT-shaped token, enough for the portal to read the claims. */
export function fakeToken(username: string, roles: string[], expiresInSeconds = 600): string {
  const enc = (o: unknown) => btoa(JSON.stringify(o)).replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_');
  return [enc({ alg: 'none' }), enc({ preferred_username: username, realm_access: { roles }, exp: Math.floor(Date.now() / 1000) + expiresInSeconds }), 'sig'].join('.');
}

/** Plan of the default detail fixture: document, per signer (EMAIL confirmation + signature), final document, callback. */
export function detailProgress(): ProgressDetail {
  const steps: Step[] = [
    { order: 1, kind: 'DOCUMENT_RECEIVED', signerId: null, channel: null, label: 'Documento recebido', status: 'COMPLETED' },
    { order: 2, kind: 'CONFIRMATION', signerId: 'sgn_1', channel: 'EMAIL', label: 'Confirmação por e-mail - Maria Souza', status: 'COMPLETED' },
    { order: 3, kind: 'SIGNATURE', signerId: 'sgn_1', channel: null, label: 'Assinatura - Maria Souza', status: 'COMPLETED' },
    { order: 4, kind: 'CONFIRMATION', signerId: 'sgn_2', channel: 'EMAIL', label: 'Confirmação por e-mail - Joao Lima', status: 'IN_PROGRESS' },
    { order: 5, kind: 'SIGNATURE', signerId: 'sgn_2', channel: null, label: 'Assinatura - Joao Lima', status: 'PENDING' },
    { order: 6, kind: 'FINAL_DOCUMENT', signerId: null, channel: null, label: 'Documento final', status: 'PENDING' },
    { order: 7, kind: 'CALLBACK', signerId: null, channel: null, label: 'Callback', status: 'PENDING' },
  ];
  return { completedSteps: 3, totalSteps: 7, currentStep: steps[3], steps };
}
