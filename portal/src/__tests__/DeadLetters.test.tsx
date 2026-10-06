import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { deadLetter, mockApi, renderApp } from './mockApi';

const page = (items: ReturnType<typeof deadLetter>[], total = items.length, pageNo = 1) => ({ items, page: pageNo, pageSize: 20, total });

describe('dead letters page', () => {
  it('lists the dead letters with domain, process, operation, error, reason, attempts and dates', async () => {
    mockApi([{ match: '/v1/dead-letters', handler: { body: page([
      deadLetter(),
      deadLetter({ deadLetterId: 'dlq_2', processId: 'sig_2', domain: 'callback', operationType: 'CALLBACK_SEND', operationId: 'op_9', queue: 'callback-dlq',
        errorClass: 'PERMANENT', reason: 'Destination responded HTTP 400', attempts: 1, resolvedAt: '2026-10-06T04:00:00Z' }),
    ]) } }]);
    renderApp('/dead-letters');

    const table = await screen.findByRole('table', { name: 'Dead letters' });
    for (const h of ['Domain', 'Process', 'Operation', 'Error class', 'Reason', 'Attempts', 'Created', 'Resolved'])
      expect(within(table).getByRole('columnheader', { name: h })).toBeInTheDocument();
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText('signature-provider')).toBeInTheDocument();
    expect(within(rows[0]).getByText('PROVIDER_CREATE_PROCESS')).toBeInTheDocument();
    expect(within(rows[0]).getByText('op_2')).toBeInTheDocument();
    expect(within(rows[0]).getByText('TRANSIENT')).toBeInTheDocument();
    expect(within(rows[0]).getByText('Simulated provider outage (HTTP 503)')).toBeInTheDocument();
    expect(within(rows[0]).getByText('4')).toBeInTheDocument();
    expect(within(rows[1]).getByText('callback')).toBeInTheDocument();
    expect(within(rows[1]).getByText('PERMANENT')).toBeInTheDocument();
  });

  it('links each row to the Errors tab of the process', async () => {
    mockApi([{ match: '/v1/dead-letters', handler: { body: page([deadLetter()]) } }]);
    renderApp('/dead-letters');
    expect(await screen.findByRole('link', { name: 'sig_1' })).toHaveAttribute('href', '/processes/sig_1?tab=errors');
  });

  it('filters by domain and by pending/resolved through the API', async () => {
    const api = mockApi([{ match: '/v1/dead-letters', handler: { body: page([deadLetter()]) } }]);
    renderApp('/dead-letters');
    await screen.findByRole('table');
    expect(new URL('http://x' + api.calls[0].path).searchParams.get('resolved')).toBe('false');

    await userEvent.selectOptions(screen.getByLabelText('Domain'), 'artifact');
    await waitFor(() => expect(new URL('http://x' + api.calls.at(-1)!.path).searchParams.get('domain')).toBe('artifact'));
    await userEvent.selectOptions(screen.getByLabelText('State'), 'resolved');
    await waitFor(() => {
      const sp = new URL('http://x' + api.calls.at(-1)!.path).searchParams;
      expect(sp.get('resolved')).toBe('true');
      expect(sp.get('domain')).toBe('artifact');
    });
  });

  it('paginates', async () => {
    const api = mockApi([{ match: '/v1/dead-letters', handler: (c) => {
      const n = Number(new URL('http://x' + c.path).searchParams.get('page') ?? '1');
      return { body: page([deadLetter({ deadLetterId: 'dlq_' + n, processId: 'sig_page' + n })], 41, n) };
    } }]);
    renderApp('/dead-letters');
    expect(await screen.findByText('Page 1 of 3 · 41 dead letters')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByRole('link', { name: 'sig_page2' })).toBeInTheDocument();
    expect(new URL('http://x' + api.calls.at(-1)!.path).searchParams.get('page')).toBe('2');
  });

  it('shows an empty state and an error state', async () => {
    mockApi([{ match: '/v1/dead-letters', handler: { body: page([], 0) } }]);
    const { unmount } = renderApp('/dead-letters');
    expect(await screen.findByText('No dead letters found.')).toBeInTheDocument();
    unmount();

    mockApi([{ match: '/v1/dead-letters', handler: { status: 500, body: { title: 'Internal error' } } }]);
    renderApp('/dead-letters');
    expect(await screen.findByRole('alert')).toHaveTextContent('Internal error');
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
  });

  it('is reachable from the main navigation', async () => {
    mockApi([{ match: '/v1/signature-processes', handler: { body: { items: [], page: 1, pageSize: 20, total: 0 } } },
      { match: '/v1/dead-letters', handler: { body: page([deadLetter()]) } }]);
    renderApp('/');
    await userEvent.click(await screen.findByRole('link', { name: 'Dead letters' }));
    expect(await screen.findByRole('heading', { name: 'Dead letters' })).toBeInTheDocument();
  });
});
