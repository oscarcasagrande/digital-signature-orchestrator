import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ApiError, api, getOperator, sanitizeOperatorId, setOperator } from '../api/client';
import { AUTO_REFRESH_MS } from '../pages/ProcessList';
import { listItem, mockApi, renderApp } from './mockApi';

const page = (items: ReturnType<typeof listItem>[], total = items.length, pageNo = 1) => ({ items, page: pageNo, pageSize: 20, total });

describe('process list', () => {
  it('shows one row per process with the columns of the PRD', async () => {
    mockApi([{ match: '/v1/signature-processes', handler: { body: page([
      listItem(),
      listItem({ processId: 'sig_2', externalId: 'TERM-2', documentFileName: 'term.pdf', provider: null, signersSigned: 0, signersTotal: 1,
        businessStatus: 'FAILED', operationalStatus: 'MANUAL_ACTION', sla: 'ALERT' }),
    ]) } }]);
    renderApp('/');

    const table = await screen.findByRole('table', { name: 'Signature processes' });
    for (const h of ['Process', 'Document', 'Provider', 'Signers', 'Status', 'Operational', 'Created', 'SLA'])
      expect(within(table).getByRole('columnheader', { name: h })).toBeInTheDocument();

    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    const first = within(rows[0]);
    expect(first.getByRole('link', { name: 'sig_1' })).toHaveAttribute('href', '/processes/sig_1');
    expect(first.getByText('CONTRACT-1')).toBeInTheDocument();
    expect(first.getByText('contract.pdf')).toBeInTheDocument();
    expect(first.getByText('SIMULATED')).toBeInTheDocument();
    expect(first.getByText('1/2')).toBeInTheDocument();
    expect(first.getByText('SIGNATURE IN PROGRESS')).toBeInTheDocument();
    expect(first.getByText('READY')).toBeInTheDocument();
    expect(first.getByText('OK')).toBeInTheDocument();

    const second = within(rows[1]);
    expect(second.getByText('0/1')).toBeInTheDocument();
    expect(second.getByText('FAILED')).toBeInTheDocument();
    expect(second.getByText('MANUAL ACTION')).toBeInTheDocument();
    expect(second.getByText('ALERT')).toBeInTheDocument();
    expect(second.getAllByText('—').length).toBeGreaterThan(0); // no provider yet
  });

  it('sends the filters and the search term to the API and resets the page', async () => {
    const api = mockApi([{ match: '/v1/signature-processes', handler: { body: page([listItem()], 1) } }]);
    renderApp('/');
    await screen.findByRole('table');

    await userEvent.selectOptions(screen.getByLabelText('Status'), 'COMPLETED');
    await waitFor(() => expect(api.calls.at(-1)!.path).toContain('status=COMPLETED'));
    await userEvent.selectOptions(screen.getByLabelText('Operational'), 'DLQ');
    await waitFor(() => expect(api.calls.at(-1)!.path).toContain('operationalStatus=DLQ'));

    await userEvent.type(screen.getByLabelText('Search'), 'CONTRACT-9');
    await userEvent.click(screen.getByRole('button', { name: 'Search' }));
    await waitFor(() => {
      const last = new URL('http://x' + api.calls.at(-1)!.path).searchParams;
      expect(last.get('q')).toBe('CONTRACT-9');
      expect(last.get('status')).toBe('COMPLETED');
      expect(last.get('operationalStatus')).toBe('DLQ');
      expect(last.get('page')).toBe('1'); // a new search goes back to the first page
    });
  });

  it('restores filters from the address and searches with Enter', async () => {
    const api = mockApi([{ match: '/v1/signature-processes', handler: { body: page([listItem()]) } }]);
    renderApp('/?status=FAILED&q=abc&page=2');
    await screen.findByRole('table');
    const first = new URL('http://x' + api.calls[0].path).searchParams;
    expect(first.get('status')).toBe('FAILED');
    expect(first.get('q')).toBe('abc');
    expect(first.get('page')).toBe('2');
    expect(screen.getByLabelText('Status')).toHaveValue('FAILED');
    expect(screen.getByLabelText('Search')).toHaveValue('abc');

    await userEvent.clear(screen.getByLabelText('Search'));
    await userEvent.type(screen.getByLabelText('Search'), 'xyz{Enter}');
    await waitFor(() => expect(new URL('http://x' + api.calls.at(-1)!.path).searchParams.get('q')).toBe('xyz'));
  });

  it('paginates without losing the filters', async () => {
    const api = mockApi([{ match: '/v1/signature-processes', handler: (c) => ({
      body: page([listItem({ processId: 'sig_p' + (new URL('http://x' + c.path).searchParams.get('page') ?? '1') })], 45,
        Number(new URL('http://x' + c.path).searchParams.get('page') ?? '1')),
    }) }]);
    renderApp('/?status=COMPLETED');
    expect(await screen.findByText('Page 1 of 3 · 45 processes')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();

    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByText('Page 2 of 3 · 45 processes')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'sig_p2' })).toBeInTheDocument();
    const last = new URL('http://x' + api.calls.at(-1)!.path).searchParams;
    expect(last.get('page')).toBe('2');
    expect(last.get('status')).toBe('COMPLETED');

    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByText('Page 3 of 3 · 45 processes')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('shows an empty state', async () => {
    mockApi([{ match: '/v1/signature-processes', handler: { body: page([], 0) } }]);
    renderApp('/');
    expect(await screen.findByText('No processes found.')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('shows the loading state first', async () => {
    let release: (v: { body: unknown }) => void = () => {};
    const gate = new Promise<{ body: unknown }>((r) => { release = r; });
    mockApi([{ match: '/v1/signature-processes', handler: () => gate }]);
    renderApp('/');
    expect(screen.getByText('Loading…')).toBeInTheDocument();
    release({ body: page([listItem()]) });
    expect(await screen.findByRole('table')).toBeInTheDocument();
    expect(screen.queryByText('Loading…')).not.toBeInTheDocument();
  });

  it('shows an error with a retry that works once the API is back', async () => {
    let healthy = false;
    mockApi([{ match: '/v1/signature-processes', handler: () => (healthy
      ? { body: page([listItem()]) } : { status: 503, body: { title: 'Service unavailable', detail: 'try later' } }) }]);
    renderApp('/');
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Service unavailable: try later');

    healthy = true;
    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('table')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('reports an unreachable API', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Failed to fetch'); }));
    renderApp('/');
    expect(await screen.findByRole('alert')).toHaveTextContent('The API is unreachable.');
  });

  it('refreshes automatically on demand', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const api = mockApi([{ match: '/v1/signature-processes', handler: { body: page([listItem()]) } }]);
    renderApp('/');
    await screen.findByRole('table');
    const before = api.calls.length;

    await userEvent.click(screen.getByLabelText('Auto-refresh'), { advanceTimers: vi.advanceTimersByTime });
    await act(async () => { await vi.advanceTimersByTimeAsync(AUTO_REFRESH_MS + 100); });
    await waitFor(() => expect(api.calls.length).toBeGreaterThan(before));

    await userEvent.click(screen.getByLabelText('Auto-refresh'), { advanceTimers: vi.advanceTimersByTime });
    const stopped = api.calls.length;
    await act(async () => { await vi.advanceTimersByTimeAsync(AUTO_REFRESH_MS * 2); });
    expect(api.calls.length).toBe(stopped);
  });

  it('is operable by keyboard: filters have labels and rows are links', async () => {
    mockApi([{ match: '/v1/signature-processes', handler: { body: page([listItem()]) } }]);
    renderApp('/');
    await screen.findByRole('table');
    expect(screen.getByRole('search')).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'sig_1' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Skip to content' })).toBeInTheDocument();
  });
});

describe('api client', () => {
  it('turns problem+json into ApiError with status, title and detail', async () => {
    mockApi([{ match: '/v1/signature-processes/sig_x', handler: { status: 409, body: { title: 'Conflict', detail: 'Process is COMPLETED', correlationId: 'c1' } } }]);
    await expect(api.getProcess('sig_x')).rejects.toMatchObject({ status: 409, title: 'Conflict', detail: 'Process is COMPLETED', correlationId: 'c1' });
    await expect(api.getProcess('sig_x')).rejects.toBeInstanceOf(ApiError);
  });

  it('sends X-Operator-Id only when an operator is set', async () => {
    const m = mockApi([{ match: '/v1/signature-processes/sig_1', handler: { body: {} } }]);
    await api.getProcess('sig_1');
    expect(m.calls[0].headers['X-Operator-Id']).toBeUndefined();
    setOperator('maria.ops');
    await api.getProcess('sig_1');
    expect(m.calls[1].headers['X-Operator-Id']).toBe('maria.ops');
  });

  it('sanitizes and limits the operator id', () => {
    expect(sanitizeOperatorId('ana ops<script>')).toBe('anaopsscript');
    expect(sanitizeOperatorId('ok.user_1@corp:x-y')).toBe('ok.user_1@corp:x-y');
    expect(sanitizeOperatorId('a'.repeat(150))).toHaveLength(100);
    expect(setOperator('   ')).toBeNull();
    expect(getOperator()).toBeNull();
    expect(setOperator('b'.repeat(130))).toHaveLength(100);
    expect(getOperator()).toHaveLength(100);
  });

  it('encodes ids and builds query strings without empty values', async () => {
    const m = mockApi([{ match: '/v1/signature-processes', handler: { body: { items: [], page: 1, pageSize: 20, total: 0 } } }]);
    await api.listProcesses({ status: '', q: 'a b', page: 2 });
    expect(m.calls[0].path).toBe('/v1/signature-processes?q=a+b&page=2');
  });

  it('input handling: change event with an over-long id is stored limited', async () => {
    mockApi([{ match: '/v1/signature-processes', handler: { body: page([listItem()]) } }]);
    renderApp('/');
    await screen.findByRole('table');
    const input = screen.getByLabelText('Operator id');
    fireEvent.change(input, { target: { value: 'x'.repeat(120) } });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(getOperator()).toHaveLength(100);
  });
});
