import { useCallback, useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import type { ArtifactItem, CallbackDelivery, DeadLetter, JournalEvent, OperationItem, ProcessDetail as Detail, ProviderInfo } from '../api/types';
import { ConfirmDialog } from '../components/ConfirmDialog';
import { JsonView } from '../components/JsonView';
import { errorNotice, LoadError, Notice, type NoticeState } from '../components/Notice';
import { useOperator } from '../components/OperatorContext';
import { ProgressCell, StepBadge, currentStepText, groupSteps } from '../components/Progress';
import { StatusBadge } from '../components/StatusBadge';
import { Tabs, type TabDef } from '../components/Tabs';
import { Timeline } from '../components/Timeline';
import { useApi } from '../hooks/useApi';
import { openDownload } from '../util/navigation';
import {
  REPROCESSABLE_OPERATION_STATUSES, actorLabel, eventLabel, formatBytes, formatDateTime, isTerminal,
} from '../util/format';

const TABS: TabDef[] = [
  { key: 'overview', label: 'Overview' },
  { key: 'signers', label: 'Signers' },
  { key: 'steps', label: 'Etapas' },
  { key: 'identity', label: 'Identity Proofing' },
  { key: 'operations', label: 'Operations' },
  { key: 'artifacts', label: 'Artifacts' },
  { key: 'provider', label: 'Provider Metadata' },
  { key: 'audit', label: 'Audit Trail' },
  { key: 'callbacks', label: 'Callbacks' },
  { key: 'errors', label: 'Errors' },
];

const EVENTS_PAGE = 50;
const FAILURE_EVENTS = ['OPERATION_FAILED', 'OPERATION_DEAD_LETTERED', 'OPERATION_RETRY_SCHEDULED', 'RECONCILIATION_PROVIDER_ERROR'];

type Dialog =
  | { kind: 'cancel' }
  | { kind: 'reconcile' }
  | { kind: 'reprocess'; operationId?: string; type?: string }
  | { kind: 'callback'; operationId: string; eventType: string }
  | { kind: 'download'; artifact: ArtifactItem }
  | { kind: 'inspect' };

export function ProcessDetail() {
  const { id = '' } = useParams();
  const [params, setParams] = useSearchParams();
  const tab = TABS.some((t) => t.key === params.get('tab')) ? (params.get('tab') as string) : 'overview';
  const { operator, reason } = useOperator();

  const proc = useApi(() => api.getProcess(id), [id]);
  const ops = useApi(() => api.listOperations(id), [id]);
  const artifacts = useApi(() => api.listArtifacts(id), [id]);
  const callbacks = useApi(() => api.listCallbacks(id), [id]);
  const pendingDl = useApi(() => api.listDeadLetters({ processId: id, resolved: false, pageSize: 200 }), [id]);
  const resolvedDl = useApi(() => api.listDeadLetters({ processId: id, resolved: true, pageSize: 200 }), [id]);

  const [events, setEvents] = useState<JournalEvent[]>([]);
  const [eventsTotal, setEventsTotal] = useState(0);
  const [eventsPage, setEventsPage] = useState(1);
  const [eventsError, setEventsError] = useState<string | null>(null);

  const [dialog, setDialog] = useState<Dialog | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<NoticeState | null>(null);
  const [providerInfo, setProviderInfo] = useState<ProviderInfo | null>(null);

  const loadEvents = useCallback(
    async (page: number, replace: boolean) => {
      try {
        const r = await api.listEvents(id, page, EVENTS_PAGE);
        setEvents((prev) => (replace ? r.items : [...prev, ...r.items]));
        setEventsTotal(r.total);
        setEventsPage(page);
        setEventsError(null);
      } catch (e) {
        setEventsError(errorNotice(e).text);
      }
    },
    [id],
  );

  useEffect(() => {
    void loadEvents(1, true);
  }, [loadEvents]);

  const reloadAll = () => {
    proc.reload();
    ops.reload();
    artifacts.reload();
    callbacks.reload();
    pendingDl.reload();
    resolvedDl.reload();
    void loadEvents(1, true);
  };

  const setTab = (key: string) => {
    const next = new URLSearchParams(params);
    next.set('tab', key);
    setParams(next, { replace: true });
  };

  if (proc.error?.status === 404) {
    return (
      <section>
        <h1>Process not found</h1>
        <p>
          No process with id <code>{id}</code>. <Link to="/">Back to the list</Link>
        </p>
      </section>
    );
  }
  if (proc.error && !proc.data) {
    return (
      <section>
        <Link to="/">← Processes</Link>
        <LoadError error={proc.error} onRetry={proc.reload} />
      </section>
    );
  }
  if (!proc.data) return <p role="status">Loading…</p>;

  const p: Detail = proc.data;
  const terminal = isTerminal(p.businessStatus);
  const operations: OperationItem[] = ops.data?.items ?? [];
  const reprocessable = operations.filter((o) => o.type !== 'CALLBACK_SEND' && REPROCESSABLE_OPERATION_STATUSES.includes(o.status));
  const disabledReason = reason;

  async function run(action: () => Promise<NoticeState | void>) {
    setBusy(true);
    try {
      const n = await action();
      if (n) setNotice(n);
      setDialog(null);
      reloadAll();
    } catch (e) {
      setDialog(null);
      setNotice(errorNotice(e));
      reloadAll();
    } finally {
      setBusy(false);
    }
  }

  const confirm = (reason: string) => {
    const d = dialog;
    if (!d) return;
    void run(async () => {
      switch (d.kind) {
        case 'cancel':
          await api.cancel(id);
          return { kind: 'success', text: 'Process cancelled.' };
        case 'reconcile': {
          const r = await api.reconcile(id);
          if (r.outcome === 'CORRECTED')
            return { kind: 'success', text: 'Corrected: ' + r.internalStatus + ' → ' + r.resultingStatus + ' (provider status ' + r.providerStatus + ').' };
          if (r.outcome === 'CONSISTENT')
            return { kind: 'success', text: 'Consistent with the provider (' + r.providerStatus + '). Nothing changed.' };
          if (r.outcome === 'NOT_APPLICABLE') return { kind: 'success', text: 'Not applicable: the process has no provider registration yet.' };
          return { kind: 'error', text: 'Provider error: ' + (r.error ?? 'unavailable') };
        }
        case 'reprocess': {
          const opId = d.operationId ?? reprocessable[reprocessable.length - 1]?.operationId;
          const r = await api.reprocess(id, { operationId: opId, reason: reason || undefined });
          return { kind: 'success', text: 'Operation ' + r.operationType + ' (' + r.operationId + ') queued for reprocessing.' };
        }
        case 'callback': {
          const r = await api.reprocess(id, { operationId: d.operationId, reason: reason || undefined });
          return { kind: 'success', text: 'Callback delivery (' + r.operationId + ') queued to be sent again.' };
        }
        case 'download': {
          const link = await api.downloadLink(id, { artifactId: d.artifact.artifactId });
          openDownload(link.url);
          return { kind: 'success', text: 'Download started. The link expires at ' + formatDateTime(link.expiresAt) + '.' };
        }
        case 'inspect': {
          setProviderInfo(await api.getProvider(id));
          setTab('provider');
          return { kind: 'success', text: 'Provider metadata loaded (the access was recorded in the audit trail).' };
        }
      }
    });
  };

  const actionButton = (label: string, kind: Dialog, disabled: boolean, why?: string, ariaLabel?: string) => (
    <button
      type="button"
      className="secondary"
      disabled={!operator || disabled}
      title={disabledReason ?? (disabled ? why : undefined)}
      aria-label={ariaLabel}
      onClick={() => setDialog(kind)}
    >
      {label}
    </button>
  );

  return (
    <section aria-labelledby="process-title">
      <p>
        <Link to="/">← Processes</Link>
      </p>
      <header className="detail-header">
        <div>
          <h1 id="process-title">{p.processId}</h1>
          <p className="sub">
            {p.externalId} · {p.documentFileName ?? 'no document name'} · created {formatDateTime(p.createdAt)}
          </p>
        </div>
        <div className="badges">
          <StatusBadge value={p.businessStatus} />
          <StatusBadge value={p.operationalStatus} />
          <StatusBadge value={p.sla} label={'SLA ' + p.sla} />
        </div>
      </header>

      <div className="actions" role="group" aria-label="Manual operations">
        {actionButton('Reconcile Provider', { kind: 'reconcile' }, terminal, 'The process is already in a final state')}
        {actionButton('Reprocess From Operation', { kind: 'reprocess' }, reprocessable.length === 0, 'No failed operation to reprocess')}
        {actionButton('Cancel Process', { kind: 'cancel' }, terminal, 'The process is already in a final state')}
        {actionButton('Inspect Provider Metadata', { kind: 'inspect' }, false)}
        <button type="button" className="link" onClick={reloadAll}>
          Refresh
        </button>
        {!operator && <span className="hint">{reason === 'Set your operator id first' ? 'Set your operator id (top right) to enable manual operations.' : reason}</span>}
      </div>

      <Notice notice={notice} onDismiss={() => setNotice(null)} />

      <h2>Timeline</h2>
      <Timeline events={events} />
      {events.length < eventsTotal && (
        <button type="button" className="secondary" onClick={() => void loadEvents(eventsPage + 1, false)}>
          Load more events
        </button>
      )}
      {eventsError && <p role="alert" className="notice notice-error">{eventsError}</p>}

      <Tabs tabs={TABS} active={tab} onChange={setTab}>
        {tab === 'overview' && <Overview p={p} />}
        {tab === 'signers' && <Signers p={p} />}
        {tab === 'steps' && <Steps p={p} />}
        {tab === 'identity' && <Identity p={p} />}
        {tab === 'operations' && (
          <Operations items={operations} error={ops.error} reload={ops.reload}
            retry={(o) => setDialog({ kind: 'reprocess', operationId: o.operationId, type: o.type })} disabled={!operator} why={disabledReason} />
        )}
        {tab === 'artifacts' && (
          <Artifacts items={artifacts.data?.items ?? []} error={artifacts.error} reload={artifacts.reload}
            download={(a) => setDialog({ kind: 'download', artifact: a })} disabled={!operator} why={disabledReason} />
        )}
        {tab === 'provider' && <Provider info={providerInfo} onInspect={() => setDialog({ kind: 'inspect' })} disabled={!operator} why={disabledReason} />}
        {tab === 'audit' && <Audit events={events} total={eventsTotal} more={() => void loadEvents(eventsPage + 1, false)} />}
        {tab === 'callbacks' && (
          <Callbacks items={callbacks.data?.items ?? []} error={callbacks.error} reload={callbacks.reload}
            retry={(c) => setDialog({ kind: 'callback', operationId: c.operationId, eventType: c.eventType })} disabled={!operator} why={disabledReason} />
        )}
        {tab === 'errors' && (
          <Errors operations={operations} events={events} deadLetters={[...(pendingDl.data?.items ?? []), ...(resolvedDl.data?.items ?? [])]} />
        )}
      </Tabs>

      {dialog && (
        <ConfirmDialog
          title={dialogTitle(dialog)}
          description={dialogText(dialog, p)}
          confirmLabel={dialogConfirm(dialog)}
          reasonLabel={dialog.kind === 'reprocess' || dialog.kind === 'callback' ? 'Reason (optional)' : undefined}
          busy={busy}
          onConfirm={confirm}
          onCancel={() => setDialog(null)}
        >
          {dialog.kind === 'reprocess' && !dialog.operationId && (
            <label className="field">
              Operation
              <select defaultValue={reprocessable[reprocessable.length - 1]?.operationId}
                onChange={(e) => setDialog({ kind: 'reprocess', operationId: e.target.value })}>
                {reprocessable.map((o) => (
                  <option key={o.operationId} value={o.operationId}>{o.type} · {o.status} · {o.operationId}</option>
                ))}
              </select>
            </label>
          )}
        </ConfirmDialog>
      )}
    </section>
  );
}

function dialogTitle(d: Dialog): string {
  switch (d.kind) {
    case 'cancel': return 'Cancel Process';
    case 'reconcile': return 'Reconcile Provider';
    case 'reprocess': return d.operationId ? 'Retry Operation' : 'Reprocess From Operation';
    case 'callback': return 'Retry Callback';
    case 'download': return 'Download Artifact';
    case 'inspect': return 'Inspect Provider Metadata';
  }
}

function dialogConfirm(d: Dialog): string {
  switch (d.kind) {
    case 'cancel': return 'Cancel process';
    case 'reconcile': return 'Reconcile';
    case 'reprocess': return 'Reprocess';
    case 'callback': return 'Send again';
    case 'download': return 'Download';
    case 'inspect': return 'Inspect';
  }
}

function dialogText(d: Dialog, p: Detail) {
  switch (d.kind) {
    case 'cancel': return <p>Cancel process <strong>{p.processId}</strong>? Pending operations stop and the consumer is notified. This cannot be undone.</p>;
    case 'reconcile': return <p>Compare process <strong>{p.processId}</strong> with the provider and correct any divergence. The action is recorded in the audit trail.</p>;
    case 'reprocess': return <p>{d.operationId ? <>Retry operation <strong>{d.type ?? d.operationId}</strong>.</> : 'Resume the process from the failed operation.'} Completed operations are not repeated.</p>;
    case 'callback': return <p>Send the <strong>{d.eventType}</strong> callback again. The destination and signature are unchanged.</p>;
    case 'download': return <p>Create a temporary signed link for <strong>{d.artifact.type}</strong> ({d.artifact.fileName}). The link is recorded in the audit trail.</p>;
    case 'inspect': return <p>Show the native provider metadata of this process. The access is recorded in the audit trail.</p>;
  }
}

function Overview({ p }: { p: Detail }) {
  const dest = p.callback ? (p.callback.callbackId ?? p.callback.url ?? '—') : 'none';
  const rows: Array<[string, React.ReactNode]> = [
    ['Process id', p.processId], ['External id', p.externalId], ['Document', p.documentFileName ?? '—'], ['Provider', p.provider ?? '—'],
    ['Signature type', p.signatureType], ['Business status', <StatusBadge key="b" value={p.businessStatus} />],
    ['Operational status', <StatusBadge key="o" value={p.operationalStatus} />], ['SLA', <StatusBadge key="s" value={p.sla} />],
    ['Signers', p.signers.filter((s) => s.signed).length + '/' + p.signers.length], ['Created', formatDateTime(p.createdAt)],
    ['Updated', formatDateTime(p.updatedAt)], ['Completed', formatDateTime(p.completedAt)], ['Callback destination', dest],
    ['Correlation id', p.correlationId],
  ];
  return (
    <dl className="props">
      {rows.map(([k, v]) => (
        <div key={k}>
          <dt>{k}</dt>
          <dd>{v}</dd>
        </div>
      ))}
    </dl>
  );
}

function Signers({ p }: { p: Detail }) {
  return (
    <div className="table-wrap">
      <table>
        <caption className="sr-only">Signers</caption>
        <thead>
          <tr><th scope="col">Name</th><th scope="col">External id</th><th scope="col">Document</th><th scope="col">Signed</th><th scope="col">Signed at</th></tr>
        </thead>
        <tbody>
          {p.signers.map((s) => (
            <tr key={s.id}>
              <td>{s.name}</td><td>{s.externalId}</td><td>{s.document}</td>
              <td>{s.signed ? 'Yes' : 'No'}</td><td>{formatDateTime(s.signedAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Identity({ p }: { p: Detail }) {
  const v = p.identityValidations ?? [];
  if (v.length === 0) return <p className="empty">No identity validations were requested.</p>;
  return (
    <>
      <p className="hint">Capabilities requested for this process (executed through an identity proofing session).</p>
      <ul className="plain">
        {v.map((x) => {
          const type = typeof x === 'string' ? x : x.type;
          const required = typeof x === 'string' ? true : x.required !== false;
          return (
            <li key={type}>
              {type} <span className="sub">{required ? 'required' : 'optional'}</span>
            </li>
          );
        })}
      </ul>
    </>
  );
}

interface SectionProps {
  error: import('../api/client').ApiError | null;
  reload: () => void;
}

function Operations({ items, error, reload, retry, disabled, why }: SectionProps & {
  items: OperationItem[]; retry: (o: OperationItem) => void; disabled: boolean; why?: string;
}) {
  if (error) return <LoadError error={error} onRetry={reload} />;
  if (items.length === 0) return <p className="empty">No operations yet.</p>;
  return (
    <div className="table-wrap">
      <table>
        <caption className="sr-only">Operations</caption>
        <thead>
          <tr>
            <th scope="col">Operation</th><th scope="col">Type</th><th scope="col">Status</th><th scope="col">Attempts</th>
            <th scope="col">Next retry</th><th scope="col">Error</th><th scope="col">Actions</th>
          </tr>
        </thead>
        <tbody>
          {items.map((o) => (
            <tr key={o.operationId}>
              <td className="mono">{o.operationId}</td>
              <td>{o.type}</td>
              <td><StatusBadge value={o.status} /></td>
              <td>{o.attempt}/{o.maxAttempts}</td>
              <td>{formatDateTime(o.nextRetryAt)}</td>
              <td className="wrap">{o.error ? (o.error.errorClass ? o.error.errorClass + ': ' : '') + (o.error.message ?? '') : '—'}</td>
              <td>
                {o.type !== 'CALLBACK_SEND' && REPROCESSABLE_OPERATION_STATUSES.includes(o.status) && (
                  <button type="button" className="secondary" disabled={disabled} title={why}
                    aria-label={'Retry Operation ' + o.type} onClick={() => retry(o)}>
                    Retry Operation
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Artifacts({ items, error, reload, download, disabled, why }: SectionProps & {
  items: ArtifactItem[]; download: (a: ArtifactItem) => void; disabled: boolean; why?: string;
}) {
  if (error) return <LoadError error={error} onRetry={reload} />;
  if (items.length === 0) return <p className="empty">No artifacts stored yet.</p>;
  return (
    <div className="table-wrap">
      <table>
        <caption className="sr-only">Artifacts</caption>
        <thead>
          <tr>
            <th scope="col">Type</th><th scope="col">File</th><th scope="col">Content type</th><th scope="col">Size</th>
            <th scope="col">SHA-256</th><th scope="col">Created</th><th scope="col">Actions</th>
          </tr>
        </thead>
        <tbody>
          {items.map((a) => (
            <tr key={a.artifactId}>
              <td>{a.type}</td><td>{a.fileName}</td><td>{a.contentType}</td><td>{formatBytes(a.size)}</td>
              <td className="mono" title={a.sha256}>{a.sha256.slice(0, 16)}…</td>
              <td>{formatDateTime(a.createdAt)}</td>
              <td>
                <button type="button" className="secondary" disabled={disabled} title={why}
                  aria-label={'Download Artifact ' + a.type} onClick={() => download(a)}>
                  Download Artifact
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Provider({ info, onInspect, disabled, why }: { info: ProviderInfo | null; onInspect: () => void; disabled: boolean; why?: string }) {
  return (
    <>
      <p className="hint">Native provider data is only shown on request; each access is recorded in the audit trail.</p>
      {!info && (
        <button type="button" className="secondary" onClick={onInspect} disabled={disabled} title={why}>
          Inspect Provider Metadata
        </button>
      )}
      {info && (
        <>
          <dl className="props">
            {([['Provider', info.provider], ['Provider process', info.providerProcessId], ['External reference', info.externalReference],
              ['Normalized status', info.normalizedStatus], ['Updated', formatDateTime(info.updatedAt)]] as Array<[string, string]>).map(([k, v]) => (
              <div key={k}><dt>{k}</dt><dd>{v}</dd></div>
            ))}
          </dl>
          <JsonView value={info.metadata} label="Native provider metadata" />
        </>
      )}
    </>
  );
}

function Audit({ events, total, more }: { events: JournalEvent[]; total: number; more: () => void }) {
  if (events.length === 0) return <p className="empty">No events yet.</p>;
  return (
    <>
      <div className="table-wrap">
        <table>
          <caption className="sr-only">Audit trail</caption>
          <thead>
            <tr>
              <th scope="col">Time</th><th scope="col">Event</th><th scope="col">Actor</th>
              <th scope="col">Correlation id</th><th scope="col">Causation id</th><th scope="col">Details</th>
            </tr>
          </thead>
          <tbody>
            {events.map((e) => (
              <tr key={e.eventId}>
                <td>{formatDateTime(e.timestamp)}</td>
                <td>{eventLabel(e.type)}<div className="sub mono">{e.type}</div></td>
                <td>{actorLabel(e.actor)}</td>
                <td className="mono">{e.correlationId}</td>
                <td className="mono">{e.causationId ?? '—'}</td>
                <td>
                  <details>
                    <summary>Metadata</summary>
                    <JsonView value={e.metadata} label={'Metadata of ' + e.type} />
                  </details>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="hint">Showing {events.length} of {total} events.</p>
      {events.length < total && (
        <button type="button" className="secondary" onClick={more}>
          Load more events
        </button>
      )}
    </>
  );
}

function Callbacks({ items, error, reload, retry, disabled, why }: SectionProps & {
  items: CallbackDelivery[]; retry: (c: CallbackDelivery) => void; disabled: boolean; why?: string;
}) {
  if (error) return <LoadError error={error} onRetry={reload} />;
  if (items.length === 0) return <p className="empty">No callbacks for this process.</p>;
  return (
    <div className="table-wrap">
      <table>
        <caption className="sr-only">Callback deliveries</caption>
        <thead>
          <tr>
            <th scope="col">Event</th><th scope="col">Destination</th><th scope="col">Status</th><th scope="col">Attempts</th>
            <th scope="col">Last response</th><th scope="col">Last error</th><th scope="col">Delivered</th><th scope="col">Actions</th>
          </tr>
        </thead>
        <tbody>
          {items.map((c) => (
            <tr key={c.deliveryId}>
              <td>{c.eventType}<div className="sub mono">{c.eventId}</div></td>
              <td className="wrap">{c.destination}</td>
              <td><StatusBadge value={c.status} /></td>
              <td>{c.attempts}</td>
              <td>{c.lastStatusCode ?? '—'}</td>
              <td className="wrap">{c.lastError ?? '—'}</td>
              <td>{formatDateTime(c.deliveredAt)}</td>
              <td>
                {REPROCESSABLE_OPERATION_STATUSES.includes(c.status) && (
                  <button type="button" className="secondary" disabled={disabled} title={why}
                    aria-label={'Retry Callback ' + c.eventType} onClick={() => retry(c)}>
                    Retry Callback
                  </button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Errors({ operations, events, deadLetters }: { operations: OperationItem[]; events: JournalEvent[]; deadLetters: DeadLetter[] }) {
  const failing = operations.filter((o) => REPROCESSABLE_OPERATION_STATUSES.includes(o.status) || o.error);
  const failureEvents = events.filter((e) => FAILURE_EVENTS.includes(e.type));
  if (failing.length === 0 && deadLetters.length === 0 && failureEvents.length === 0) return <p className="empty">No errors recorded for this process.</p>;
  return (
    <>
      <h3>Operations with problems</h3>
      {failing.length === 0 ? <p className="empty">None.</p> : (
        <ul className="plain">
          {failing.map((o) => (
            <li key={o.operationId}>
              <strong>{o.type}</strong> <StatusBadge value={o.status} /> attempt {o.attempt}/{o.maxAttempts}
              <div className="sub wrap">{o.error ? (o.error.errorClass ?? '') + ' ' + (o.error.message ?? '') : 'No error details'}</div>
            </li>
          ))}
        </ul>
      )}
      <h3>Dead letters</h3>
      {deadLetters.length === 0 ? <p className="empty">None.</p> : (
        <ul className="plain">
          {deadLetters.map((d) => (
            <li key={d.deadLetterId}>
              <strong>{d.operationType}</strong> in <code>{d.queue}</code> · {d.resolvedAt ? 'resolved ' + formatDateTime(d.resolvedAt) : 'pending'}
              <div className="sub wrap">{d.errorClass}: {d.reason} ({d.attempts} attempts)</div>
            </li>
          ))}
        </ul>
      )}
      <h3>Failure events</h3>
      {failureEvents.length === 0 ? <p className="empty">None.</p> : (
        <ul className="plain">
          {failureEvents.map((e) => (
            <li key={e.eventId}>{formatDateTime(e.timestamp)} · {eventLabel(e.type)}</li>
          ))}
        </ul>
      )}
    </>
  );
}

const CONFIRMATION_LABEL: Record<string, string> = {
  PLANNED: 'Aguardando a vez', SENDING: 'Enviando código', SENT: 'Código enviado', EXPIRED: 'Código expirado', CONFIRMED: 'Confirmado', LOCKED: 'Bloqueado',
};

/** Planned steps of the process (fixed at creation) with the state of each, grouped per signer. */
function Steps({ p }: { p: Detail }) {
  const progress = p.progress;
  const names = new Map(p.signers.map((s) => [s.id, s]));
  return (
    <div>
      <p className="hint" aria-live="polite">
        {progress.completedSteps}/{progress.totalSteps} etapas concluídas. {currentStepText(progress)}.
      </p>
      <ProgressCell progress={progress} />
      {groupSteps(progress.steps).map((g) => {
        const signer = g.signerId ? names.get(g.signerId) : undefined;
        const title = g.signerId ? (signer?.name ?? g.signerId) : g.key === 'start' ? 'Processo' : 'Conclusão';
        return (
          <section key={g.key} aria-label={'Etapas de ' + title}>
            <h3>{title}</h3>
            <ul className="plain steps">
              {g.steps.map((s) => {
                const conf = s.kind === 'CONFIRMATION' ? signer?.confirmations?.find((c) => c.channel === s.channel) : undefined;
                return (
                  <li key={s.order} aria-current={progress.currentStep?.order === s.order ? 'step' : undefined}>
                    <span className="step-label">{s.label}</span> <StepBadge status={s.status} />
                    {conf && (
                      <span className="sub"> {CONFIRMATION_LABEL[conf.status] ?? conf.status}
                        {conf.attemptsRemaining !== null && conf.status === 'SENT' ? `, ${conf.attemptsRemaining} tentativas restantes` : ''}
                        {conf.sendCount > 1 ? `, ${conf.sendCount} envios` : ''}
                      </span>
                    )}
                  </li>
                );
              })}
            </ul>
          </section>
        );
      })}
    </div>
  );
}
