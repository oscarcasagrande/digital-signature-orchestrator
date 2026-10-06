import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { artifact, deadLetter, delivery, detail, ev, mockApi, op, processRoutes, providerInfo, renderApp } from './mockApi';

describe('process detail', () => {
  it('shows the header, the badges and a readable timeline', async () => {
    mockApi(processRoutes({
      events: [ev('PROCESS_CREATED', 0), ev('DOCUMENT_STORED', 1), ev('IDENTITY_VALIDATION_REQUESTED', 2), ev('IDENTITY_VALIDATED', 3),
        ev('SIGNATURE_STARTED', 4), ev('SIGNATURE_COMPLETED', 5), ev('FINAL_DOCUMENT_STORED', 6), ev('CALLBACK_DELIVERED', 7)],
    }));
    renderApp('/processes/sig_1');

    expect(await screen.findByRole('heading', { level: 1, name: 'sig_1' })).toBeInTheDocument();
    expect(screen.getByText(/CONTRACT-1 · contract\.pdf/)).toBeInTheDocument();
    expect(screen.getByText('SLA OK')).toBeInTheDocument();

    const timeline = await screen.findByRole('list', { name: 'Process timeline' });
    const items = within(timeline).getAllByRole('listitem').map((li) => li.textContent);
    expect(items).toHaveLength(8);
    expect(items[0]).toContain('Process created');
    expect(items[1]).toContain('Original document stored');
    expect(items[2]).toContain('Identity proofing started');
    expect(items[3]).toContain('Identity verified');
    expect(items[4]).toContain('Sent to provider for signature');
    expect(items[5]).toContain('Signed');
    expect(items[6]).toContain('Final document stored');
    expect(items[7]).toContain('Callback delivered');
    expect(items[0]).toContain('system:WORKER');
  });

  it('has the nine tabs of the PRD and starts on Overview', async () => {
    mockApi(processRoutes());
    renderApp('/processes/sig_1');
    await screen.findByRole('heading', { name: 'sig_1' });
    const tabs = screen.getAllByRole('tab').map((t) => t.textContent);
    expect(tabs).toEqual(['Overview', 'Signers', 'Etapas', 'Identity Proofing', 'Operations', 'Artifacts', 'Provider Metadata', 'Audit Trail', 'Callbacks', 'Errors']);
    expect(screen.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true');

    const panel = screen.getByRole('tabpanel');
    expect(within(panel).getByText('Process id')).toBeInTheDocument();
    expect(within(panel).getByText('ORIGINACAO_CALLBACK')).toBeInTheDocument();
    expect(within(panel).getByText('1/2')).toBeInTheDocument();
    expect(within(panel).getByText('corr-1')).toBeInTheDocument();
    expect(within(panel).getByText('ADVANCED')).toBeInTheDocument();
  });

  it('opens the tab named in the address and keeps it in the address', async () => {
    mockApi(processRoutes({ ops: [op(), op({ operationId: 'op_2', type: 'PROVIDER_SEND_DOCUMENT' })] }));
    renderApp('/processes/sig_1?tab=operations');
    expect(await screen.findByRole('table', { name: 'Operations' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Operations' })).toHaveAttribute('aria-selected', 'true');
    await userEvent.click(screen.getByRole('tab', { name: 'Signers' }));
    expect(await screen.findByRole('table', { name: 'Signers' })).toBeInTheDocument();
    expect(screen.getByRole('tab', { name: 'Signers' })).toHaveAttribute('aria-selected', 'true');
  });

  it('Signers tab lists the masked signers', async () => {
    mockApi(processRoutes());
    renderApp('/processes/sig_1?tab=signers');
    const table = await screen.findByRole('table', { name: 'Signers' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);
    expect(within(rows[0]).getByText('Maria Souza')).toBeInTheDocument();
    expect(within(rows[0]).getByText('*********09')).toBeInTheDocument();
    expect(within(rows[0]).getByText('Yes')).toBeInTheDocument();
    expect(within(rows[1]).getByText('No')).toBeInTheDocument();
    expect(table).not.toHaveTextContent(/12345678909/);
  });

  it('Identity Proofing tab shows the requested validations, or an empty message', async () => {
    mockApi(processRoutes());
    const { unmount } = renderApp('/processes/sig_1?tab=identity');
    expect(await screen.findByText('FACE_MATCH')).toBeInTheDocument();
    expect(screen.getByText('LIVENESS')).toBeInTheDocument();
    expect(screen.getByText('optional')).toBeInTheDocument();
    unmount();

    mockApi(processRoutes({ detail: detail({ identityValidations: null }) }));
    renderApp('/processes/sig_1?tab=identity');
    expect(await screen.findByText('No identity validations were requested.')).toBeInTheDocument();
  });

  it('Operations tab shows type, status, attempts, next retry and error', async () => {
    mockApi(processRoutes({ ops: [
      op({ operationId: 'op_a', type: 'DOCUMENT_DOWNLOAD' }),
      op({ operationId: 'op_b', type: 'PROVIDER_CREATE_PROCESS', status: 'RETRY_PENDING', attempt: 2, maxAttempts: 4, nextRetryAt: '2026-10-06T03:00:00Z',
        error: { errorClass: 'TRANSIENT', message: 'Simulated outage' } }),
    ] }));
    renderApp('/processes/sig_1?tab=operations');
    const table = await screen.findByRole('table', { name: 'Operations' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(within(rows[0]).getByText('COMPLETED')).toBeInTheDocument();
    expect(within(rows[1]).getByText('RETRY PENDING')).toBeInTheDocument();
    expect(within(rows[1]).getByText('2/4')).toBeInTheDocument();
    expect(within(rows[1]).getByText('TRANSIENT: Simulated outage')).toBeInTheDocument();
    expect(within(rows[0]).queryByRole('button')).not.toBeInTheDocument();
    expect(within(rows[1]).getByRole('button', { name: 'Retry Operation PROVIDER_CREATE_PROCESS' })).toBeInTheDocument();
  });

  it('Artifacts tab shows type, size and hash', async () => {
    mockApi(processRoutes({ artifacts: [artifact(), artifact({ artifactId: 'art_2', type: 'ORIGINAL_DOCUMENT', size: 5 * 1024 * 1024, fileName: 'contract.pdf' })] }));
    renderApp('/processes/sig_1?tab=artifacts');
    const table = await screen.findByRole('table', { name: 'Artifacts' });
    expect(within(table).getByText('SIGNED_DOCUMENT')).toBeInTheDocument();
    expect(within(table).getByText('2.0 KB')).toBeInTheDocument();
    expect(within(table).getByText('5.0 MB')).toBeInTheDocument();
    expect(within(table).getAllByText('aaaaaaaaaaaaaaaa…')).toHaveLength(2);
  });

  it('Provider Metadata tab asks before reading (the access is audited)', async () => {
    const api = mockApi([...processRoutes(), { match: '/v1/signature-processes/sig_1/provider', handler: { body: providerInfo() } }]);
    renderApp('/processes/sig_1?tab=provider', 'maria.ops');
    const panel = await screen.findByRole('tabpanel');
    expect(within(panel).getByRole('button', { name: 'Inspect Provider Metadata' })).toBeInTheDocument();
    expect(api.find('GET', /\/provider$/)).toHaveLength(0);
  });

  it('Audit Trail tab shows actor, correlation and causation ids', async () => {
    mockApi(processRoutes({ events: [ev('PROCESS_CREATED', 0, { actor: { type: 'OPERATOR', id: 'maria.ops' }, causationId: null }), ev('DOCUMENT_STORED', 1)] }));
    renderApp('/processes/sig_1?tab=audit');
    const table = await screen.findByRole('table', { name: 'Audit trail' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(within(rows[0]).getByText('operator:maria.ops')).toBeInTheDocument();
    expect(within(rows[0]).getAllByText('corr-1')).toHaveLength(1);
    expect(within(rows[0]).getByText('PROCESS_CREATED')).toBeInTheDocument();
    expect(within(rows[1]).getByText('evt_0')).toBeInTheDocument(); // causation id
    expect(within(rows[1]).getByText('system:WORKER')).toBeInTheDocument();
  });

  it('Callbacks tab shows deliveries with their status and last response', async () => {
    mockApi(processRoutes({ callbacks: [delivery(), delivery({ deliveryId: 'cbd_2', eventType: 'SIGNATURE_PROCESS.SIGNED', status: 'DELIVERED', lastStatusCode: 200, lastError: null })] }));
    renderApp('/processes/sig_1?tab=callbacks');
    const table = await screen.findByRole('table', { name: 'Callback deliveries' });
    expect(within(table).getByText('SIGNATURE_PROCESS.COMPLETED')).toBeInTheDocument();
    expect(within(table).getByText('503')).toBeInTheDocument();
    expect(within(table).getByText('Destination responded HTTP 503')).toBeInTheDocument();
    expect(within(table).getAllByText('ORIGINACAO_CALLBACK')).toHaveLength(2);
  });

  it('Errors tab gathers failing operations, dead letters and failure events', async () => {
    mockApi(processRoutes({
      ops: [op({ operationId: 'op_2', type: 'PROVIDER_CREATE_PROCESS', status: 'DLQ', attempt: 4, error: { errorClass: 'TRANSIENT', message: 'Simulated provider outage' } })],
      pendingDl: [deadLetter()],
      resolvedDl: [deadLetter({ deadLetterId: 'dlq_0', resolvedAt: '2026-10-06T03:10:00Z', operationType: 'DOCUMENT_DOWNLOAD', queue: 'artifact-dlq' })],
      events: [ev('PROCESS_CREATED', 0), ev('OPERATION_DEAD_LETTERED', 1)],
    }));
    renderApp('/processes/sig_1?tab=errors');
    expect(await screen.findByRole('heading', { name: 'Operations with problems' })).toBeInTheDocument();
    const panel = screen.getByRole('tabpanel');
    expect(within(panel).getByText(/attempt 4\/4/)).toBeInTheDocument();
    expect(await within(panel).findByText('signature-provider-dlq')).toBeInTheDocument();
    expect(within(panel).getByText('artifact-dlq')).toBeInTheDocument();
    expect(within(panel).getByText(/pending/)).toBeInTheDocument();
    expect(within(panel).getByText(/resolved/)).toBeInTheDocument();
    expect(within(panel).getAllByText(/Simulated provider outage \(HTTP 503\)/)).toHaveLength(2); // pending and resolved dead letters
    expect(within(panel).getByText(/Operation sent to the DLQ/)).toBeInTheDocument();
  });

  it('Errors tab says when there is nothing wrong', async () => {
    mockApi(processRoutes());
    renderApp('/processes/sig_1?tab=errors');
    expect(await screen.findByText('No errors recorded for this process.')).toBeInTheDocument();
  });

  it('shows "not found" for an unknown process', async () => {
    mockApi([{ match: '/v1/signature-processes/sig_nope', handler: { status: 404, body: { title: 'Not found', detail: 'Process not found' } } }]);
    renderApp('/processes/sig_nope');
    expect(await screen.findByRole('heading', { name: 'Process not found' })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Back to the list' })).toHaveAttribute('href', '/');
  });

  it('shows an error and retries when the API fails', async () => {
    let healthy = false;
    const routes = processRoutes();
    mockApi([{ match: '/v1/signature-processes/sig_1', handler: () => (healthy ? { body: detail() } : { status: 500, body: { title: 'Internal error' } }) }, ...routes.slice(1)]);
    renderApp('/processes/sig_1');
    const alert = await screen.findByRole('alert');
    healthy = true;
    await userEvent.click(within(alert).getByRole('button', { name: 'Try again' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'sig_1' })).toBeInTheDocument();
  });

  it('loads more events on demand (journal paging)', async () => {
    const many = Array.from({ length: 120 }, (_, i) => ev('SIGNER_SIGNED', i));
    const api = mockApi(processRoutes({ events: many }));
    renderApp('/processes/sig_1');
    const timeline = await screen.findByRole('list', { name: 'Process timeline' });
    expect(within(timeline).getAllByRole('listitem')).toHaveLength(50);
    await userEvent.click(screen.getAllByRole('button', { name: 'Load more events' })[0]);
    await waitFor(() => expect(within(screen.getByRole('list', { name: 'Process timeline' })).getAllByRole('listitem')).toHaveLength(100));
    expect(api.find('GET', /events\?page=2/)).toHaveLength(1);
  });

  it('tabs are keyboard accessible (arrow keys, roles and names)', async () => {
    mockApi(processRoutes());
    renderApp('/processes/sig_1');
    const overview = await screen.findByRole('tab', { name: 'Overview' });
    overview.focus();
    await userEvent.keyboard('{ArrowRight}');
    expect(screen.getByRole('tab', { name: 'Signers' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tab', { name: 'Signers' })).toHaveFocus();
    await userEvent.keyboard('{End}');
    expect(screen.getByRole('tab', { name: 'Errors' })).toHaveAttribute('aria-selected', 'true');
    await userEvent.keyboard('{ArrowRight}');
    expect(screen.getByRole('tab', { name: 'Overview' })).toHaveAttribute('aria-selected', 'true');
    expect(screen.getByRole('tablist', { name: 'Process sections' })).toBeInTheDocument();
    expect(screen.getByRole('tabpanel')).toHaveAttribute('aria-labelledby', 'tab-overview');
    expect(screen.getByRole('group', { name: 'Manual operations' })).toBeInTheDocument();
  });
});
