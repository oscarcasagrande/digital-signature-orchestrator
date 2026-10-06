import { useEffect, useState, type FormEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { LoadError } from '../components/Notice';
import { Pagination } from '../components/Pagination';
import { ProgressCell } from '../components/Progress';
import { StatusBadge } from '../components/StatusBadge';
import { useApi } from '../hooks/useApi';
import { useOperator } from '../components/OperatorContext';
import { BUSINESS_STATUSES, OPERATIONAL_STATUSES, formatDateTime } from '../util/format';

export const PAGE_SIZE = 20;
export const AUTO_REFRESH_MS = 10_000;

export function ProcessList() {
  const [params, setParams] = useSearchParams();
  const { canCreateProcess } = useOperator();
  const status = params.get('status') ?? '';
  const operationalStatus = params.get('operationalStatus') ?? '';
  const q = params.get('q') ?? '';
  const page = Math.max(1, Number(params.get('page') ?? '1') || 1);
  const [draft, setDraft] = useState(q);
  const [auto, setAuto] = useState(false);

  const list = useApi(() => api.listProcesses({ status, operationalStatus, q, page, pageSize: PAGE_SIZE }), [status, operationalStatus, q, page]);
  const { reload } = list;

  useEffect(() => {
    if (!auto) return;
    const t = setInterval(reload, AUTO_REFRESH_MS);
    return () => clearInterval(t);
  }, [auto, reload]);

  const update = (changes: Record<string, string>, resetPage = true) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(changes)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    if (resetPage) next.delete('page');
    setParams(next);
  };

  const onSearch = (e: FormEvent) => {
    e.preventDefault();
    update({ q: draft.trim() });
  };

  const data = list.data;
  return (
    <section aria-labelledby="processes-title">
      <div className="page-head">
        <h1 id="processes-title">Signature processes</h1>
        {canCreateProcess && <Link to="/processes/new" className="button">Novo processo</Link>}
      </div>

      <form className="filters" role="search" onSubmit={onSearch}>
        <label>
          Status
          <select value={status} onChange={(e) => update({ status: e.target.value })}>
            <option value="">All</option>
            {BUSINESS_STATUSES.map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </label>
        <label>
          Operational
          <select value={operationalStatus} onChange={(e) => update({ operationalStatus: e.target.value })}>
            <option value="">All</option>
            {OPERATIONAL_STATUSES.map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </label>
        <label>
          Search
          <input type="search" value={draft} placeholder="Process or external id" onChange={(e) => setDraft(e.target.value)} />
        </label>
        <button type="submit">Search</button>
        <label className="check">
          <input type="checkbox" checked={auto} onChange={(e) => setAuto(e.target.checked)} />
          Auto-refresh
        </label>
        <button type="button" className="secondary" onClick={reload}>
          Refresh
        </button>
      </form>

      {list.error && <LoadError error={list.error} onRetry={reload} />}
      {!data && list.loading && <p role="status">Loading…</p>}

      {data && data.items.length === 0 && <p className="empty">No processes found.</p>}
      {data && data.items.length > 0 && (
        <div className="table-wrap">
          <table>
            <caption className="sr-only">Signature processes</caption>
            <thead>
              <tr>
                <th scope="col">Process</th>
                <th scope="col">Document</th>
                <th scope="col">Provider</th>
                <th scope="col">Signers</th>
                <th scope="col">Etapas</th>
                <th scope="col">Status</th>
                <th scope="col">Operational</th>
                <th scope="col">Created</th>
                <th scope="col">SLA</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((p) => (
                <tr key={p.processId}>
                  <td>
                    <Link to={'/processes/' + p.processId}>{p.processId}</Link>
                    <div className="sub">{p.externalId}</div>
                  </td>
                  <td>{p.documentFileName ?? '—'}</td>
                  <td>{p.provider ?? '—'}</td>
                  <td>{p.signersSigned}/{p.signersTotal}</td>
                  <td><ProgressCell progress={p.progress} /></td>
                  <td><StatusBadge value={p.businessStatus} /></td>
                  <td><StatusBadge value={p.operationalStatus} /></td>
                  <td>{formatDateTime(p.createdAt)}</td>
                  <td><StatusBadge value={p.sla} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {data && data.total > 0 && (
        <Pagination page={data.page} pageSize={data.pageSize} total={data.total} noun="processes"
          onChange={(n) => update({ page: String(n) }, false)} />
      )}
    </section>
  );
}
