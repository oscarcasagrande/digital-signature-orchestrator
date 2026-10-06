import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { LoadError } from '../components/Notice';
import { Pagination } from '../components/Pagination';
import { StatusBadge } from '../components/StatusBadge';
import { useApi } from '../hooks/useApi';
import { DOMAINS, formatDateTime } from '../util/format';

const PAGE_SIZE = 20;

export function DeadLetters() {
  const [params, setParams] = useSearchParams();
  const domain = params.get('domain') ?? '';
  const state = params.get('state') === 'resolved' ? 'resolved' : 'pending';
  const page = Math.max(1, Number(params.get('page') ?? '1') || 1);

  const list = useApi(() => api.listDeadLetters({ domain, resolved: state === 'resolved', page, pageSize: PAGE_SIZE }), [domain, state, page]);

  const update = (changes: Record<string, string>, resetPage = true) => {
    const next = new URLSearchParams(params);
    for (const [k, v] of Object.entries(changes)) {
      if (v) next.set(k, v);
      else next.delete(k);
    }
    if (resetPage) next.delete('page');
    setParams(next);
  };

  const data = list.data;
  return (
    <section aria-labelledby="dlq-title">
      <h1 id="dlq-title">Dead letters</h1>
      <div className="filters">
        <label>
          Domain
          <select value={domain} onChange={(e) => update({ domain: e.target.value })}>
            <option value="">All domains</option>
            {DOMAINS.map((d) => (
              <option key={d} value={d}>{d}</option>
            ))}
          </select>
        </label>
        <label>
          State
          <select value={state} onChange={(e) => update({ state: e.target.value })}>
            <option value="pending">Pending</option>
            <option value="resolved">Resolved</option>
          </select>
        </label>
        <button type="button" className="secondary" onClick={list.reload}>
          Refresh
        </button>
      </div>

      {list.error && <LoadError error={list.error} onRetry={list.reload} />}
      {!data && list.loading && <p role="status">Loading…</p>}
      {data && data.items.length === 0 && <p className="empty">No dead letters found.</p>}
      {data && data.items.length > 0 && (
        <div className="table-wrap">
          <table>
            <caption className="sr-only">Dead letters</caption>
            <thead>
              <tr>
                <th scope="col">Domain</th>
                <th scope="col">Process</th>
                <th scope="col">Operation</th>
                <th scope="col">Error class</th>
                <th scope="col">Reason</th>
                <th scope="col">Attempts</th>
                <th scope="col">Created</th>
                <th scope="col">Resolved</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((d) => (
                <tr key={d.deadLetterId}>
                  <td>{d.domain}</td>
                  <td><Link to={'/processes/' + d.processId + '?tab=errors'}>{d.processId}</Link></td>
                  <td>
                    {d.operationType}
                    <div className="sub">{d.operationId}</div>
                  </td>
                  <td><StatusBadge value={d.errorClass} /></td>
                  <td className="wrap">{d.reason}</td>
                  <td>{d.attempts}</td>
                  <td>{formatDateTime(d.createdAt)}</td>
                  <td>{formatDateTime(d.resolvedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {data && data.total > 0 && (
        <Pagination page={data.page} pageSize={data.pageSize} total={data.total} noun="dead letters"
          onChange={(n) => update({ page: String(n) }, false)} />
      )}
    </section>
  );
}
