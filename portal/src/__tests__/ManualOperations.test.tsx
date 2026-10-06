import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { openDownload } from '../util/navigation';
import { artifact, delivery, detail, mockApi, op, processRoutes, providerInfo, renderApp, type Route } from './mockApi';

vi.mock('../util/navigation', () => ({ openDownload: vi.fn() }));

const failedOp = op({ operationId: 'op_fail', type: 'PROVIDER_CREATE_PROCESS', status: 'DLQ', attempt: 4, error: { errorClass: 'TRANSIENT', message: 'outage' } });
const BASE = '/v1/signature-processes/sig_1';

const dialog = () => screen.findByRole('dialog');
const action = (name: string) => screen.getByRole('button', { name });

describe('manual operations', () => {
  it('require an operator id: buttons are disabled until one is saved, then every call carries it', async () => {
    const api = mockApi([...processRoutes(), { method: 'POST', match: BASE + '/cancel', handler: { body: {} } }]);
    renderApp('/processes/sig_1');
    await screen.findByRole('heading', { name: 'sig_1' });

    expect(action('Cancel Process')).toBeDisabled();
    expect(action('Reconcile Provider')).toBeDisabled();
    expect(action('Inspect Provider Metadata')).toBeDisabled();
    expect(action('Cancel Process')).toHaveAttribute('title', 'Set your operator id first');
    expect(screen.getByText(/Set your operator id \(top right\)/)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText('Operator id'), { target: { value: 'maria ops' } }); // space is not allowed: removed
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    expect(await screen.findByText('mariaops')).toBeInTheDocument();
    expect(action('Cancel Process')).toBeEnabled();

    await userEvent.click(action('Cancel Process'));
    await userEvent.click(within(await dialog()).getByRole('button', { name: 'Cancel process' }));
    await waitFor(() => expect(api.find('POST', /\/cancel$/)).toHaveLength(1));
    expect(api.find('POST', /\/cancel$/)[0].headers['X-Operator-Id']).toBe('mariaops');
  });

  it('Cancel Process: confirms, calls the API, shows the result and reloads the process', async () => {
    const api = mockApi([...processRoutes(), { method: 'POST', match: BASE + '/cancel', handler: { body: { processId: 'sig_1', businessStatus: 'CANCELLED' } } }]);
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });
    const before = api.find('GET', /sig_1$/).length;

    await userEvent.click(action('Cancel Process'));
    const d = await dialog();
    expect(within(d).getByRole('heading', { name: 'Cancel Process' })).toBeInTheDocument();
    expect(api.find('POST', /cancel/)).toHaveLength(0); // nothing happens before confirming
    await userEvent.click(within(d).getByRole('button', { name: 'Cancel process' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Process cancelled.');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await waitFor(() => expect(api.find('GET', /sig_1$/).length).toBeGreaterThan(before));
  });

  it('closing the dialog (Cancel button or Escape) does nothing', async () => {
    const api = mockApi(processRoutes());
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });
    await userEvent.click(action('Cancel Process'));
    await userEvent.click(within(await dialog()).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await userEvent.click(action('Reconcile Provider'));
    await dialog();
    await userEvent.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(api.calls.filter((c) => c.method === 'POST')).toHaveLength(0);
  });

  it.each([
    [{ outcome: 'CORRECTED', internalStatus: 'SIGNATURE_IN_PROGRESS', providerStatus: 'SIGNED', corrected: true, resultingStatus: 'SIGNED', error: null },
      'status', 'Corrected: SIGNATURE_IN_PROGRESS → SIGNED (provider status SIGNED).'],
    [{ outcome: 'CONSISTENT', internalStatus: 'SIGNATURE_IN_PROGRESS', providerStatus: 'PENDING', corrected: false, resultingStatus: null, error: null },
      'status', 'Consistent with the provider (PENDING). Nothing changed.'],
    [{ outcome: 'NOT_APPLICABLE', internalStatus: 'VALIDATING', providerStatus: null, corrected: false, resultingStatus: null, error: null },
      'status', 'Not applicable: the process has no provider registration yet.'],
    [{ outcome: 'PROVIDER_ERROR', internalStatus: 'SIGNATURE_IN_PROGRESS', providerStatus: null, corrected: false, resultingStatus: null, error: 'provider unavailable' },
      'alert', 'Provider error: provider unavailable'],
  ])('Reconcile Provider shows the outcome %#', async (result, role, text) => {
    const api = mockApi([...processRoutes(), { method: 'POST', match: BASE + '/reconcile', handler: { body: { processId: 'sig_1', ...result } } }]);
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });
    await userEvent.click(action('Reconcile Provider'));
    await userEvent.click(within(await dialog()).getByRole('button', { name: 'Reconcile' }));
    expect(await screen.findByRole(role)).toHaveTextContent(text);
    expect(api.find('POST', /\/reconcile$/)[0].headers['X-Operator-Id']).toBe('ana.ops');
  });

  it('Reprocess From Operation lets the operator pick the operation and sends the reason', async () => {
    const api = mockApi([
      ...processRoutes({ ops: [op(), failedOp, op({ operationId: 'op_fail2', type: 'DOCUMENT_DOWNLOAD', status: 'FAILED' })] }),
      { method: 'POST', match: BASE + '/retry', handler: { body: { processId: 'sig_1', operationId: 'op_fail', operationType: 'PROVIDER_CREATE_PROCESS', operationalStatus: 'READY' } } },
    ]);
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });

    await userEvent.click(action('Reprocess From Operation'));
    const d = await dialog();
    const select = within(d).getByLabelText('Operation');
    expect(within(select).getAllByRole('option')).toHaveLength(2);
    await userEvent.selectOptions(select, 'op_fail');
    await userEvent.type(within(d).getByLabelText('Reason (optional)'), 'provider is back');
    await userEvent.click(within(d).getByRole('button', { name: 'Reprocess' }));

    expect(await screen.findByRole('status')).toHaveTextContent('Operation PROVIDER_CREATE_PROCESS (op_fail) queued for reprocessing.');
    const call = api.find('POST', /\/retry$/)[0];
    expect(call.body).toEqual({ operationId: 'op_fail', reason: 'provider is back' });
    expect(call.headers['X-Operator-Id']).toBe('ana.ops');
  });

  it('Retry Operation retries exactly the chosen operation (omitting an empty reason)', async () => {
    const api = mockApi([
      ...processRoutes({ ops: [op(), failedOp] }),
      { method: 'POST', match: BASE + '/retry', handler: { body: { processId: 'sig_1', operationId: 'op_fail', operationType: 'PROVIDER_CREATE_PROCESS', operationalStatus: 'READY' } } },
    ]);
    renderApp('/processes/sig_1?tab=operations', 'ana.ops');
    await userEvent.click(await screen.findByRole('button', { name: 'Retry Operation PROVIDER_CREATE_PROCESS' }));
    const d = await dialog();
    expect(within(d).getByRole('heading', { name: 'Retry Operation' })).toBeInTheDocument();
    await userEvent.click(within(d).getByRole('button', { name: 'Reprocess' }));
    await waitFor(() => expect(api.find('POST', /\/retry$/)).toHaveLength(1));
    expect(api.find('POST', /\/retry$/)[0].body).toEqual({ operationId: 'op_fail' });
  });

  it('Reprocess is disabled (with the reason) when nothing failed', async () => {
    mockApi(processRoutes());
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });
    await waitFor(() => expect(action('Reprocess From Operation')).toBeDisabled());
    expect(action('Reprocess From Operation')).toHaveAttribute('title', 'No failed operation to reprocess');
  });

  it('Retry Callback resends a failed delivery, even when the process is already completed', async () => {
    const api = mockApi([
      ...processRoutes({ detail: detail({ businessStatus: 'COMPLETED', completedAt: '2026-10-06T02:50:00Z' }), callbacks: [delivery(), delivery({ deliveryId: 'cbd_ok', operationId: 'op_ok', status: 'DELIVERED' })] }),
      { method: 'POST', match: BASE + '/retry', handler: { body: { processId: 'sig_1', operationId: 'op_cb', operationType: 'CALLBACK_SEND', operationalStatus: 'READY' } } },
    ]);
    renderApp('/processes/sig_1?tab=callbacks', 'ana.ops');
    const buttons = await screen.findAllByRole('button', { name: /Retry Callback/ });
    expect(buttons).toHaveLength(1); // only the failed delivery offers it
    await userEvent.click(buttons[0]);
    const d = await dialog();
    await userEvent.type(within(d).getByLabelText('Reason (optional)'), 'receiver fixed');
    await userEvent.click(within(d).getByRole('button', { name: 'Send again' }));
    expect(await screen.findByRole('status')).toHaveTextContent('queued to be sent again');
    expect(api.find('POST', /\/retry$/)[0].body).toEqual({ operationId: 'op_cb', reason: 'receiver fixed' });
  });

  it('Download Artifact asks the API for a temporary link for that artifact and starts the download', async () => {
    const api = mockApi([
      ...processRoutes({ artifacts: [artifact(), artifact({ artifactId: 'art_2', type: 'EVIDENCE', fileName: 'evidence.json' })] }),
      { method: 'POST', match: BASE + '/download-link', handler: { body: { url: 'http://localhost:3000/v1/downloads/art_2?expires=1&sig=abc', expiresAt: '2026-10-06T03:00:00Z' } } },
    ]);
    renderApp('/processes/sig_1?tab=artifacts', 'ana.ops');
    await userEvent.click(await screen.findByRole('button', { name: 'Download Artifact EVIDENCE' }));
    await userEvent.click(within(await dialog()).getByRole('button', { name: 'Download' }));

    await waitFor(() => expect(openDownload).toHaveBeenCalledWith('http://localhost:3000/v1/downloads/art_2?expires=1&sig=abc'));
    expect(api.find('POST', /download-link$/)[0].body).toEqual({ artifactId: 'art_2' });
    expect(api.find('POST', /download-link$/)[0].headers['X-Operator-Id']).toBe('ana.ops');
    expect(await screen.findByRole('status')).toHaveTextContent('Download started.');
  });

  it('Inspect Provider Metadata shows the native metadata after confirming', async () => {
    const api = mockApi([...processRoutes(), { match: BASE + '/provider', handler: { body: providerInfo() } }]);
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });
    await userEvent.click(action('Inspect Provider Metadata'));
    expect(api.find('GET', /\/provider$/)).toHaveLength(0);
    await userEvent.click(within(await dialog()).getByRole('button', { name: 'Inspect' }));

    expect(await screen.findByRole('tab', { name: 'Provider Metadata', selected: true })).toBeInTheDocument();
    const json = await screen.findByLabelText('Native provider metadata');
    expect(json).toHaveTextContent('"Mode": "Auto"');
    expect(screen.getByText('sim_123')).toBeInTheDocument();
    expect(api.find('GET', /\/provider$/)[0].headers['X-Operator-Id']).toBe('ana.ops');
    expect(screen.getByRole('status')).toHaveTextContent('recorded in the audit trail');
  });

  it('Cancel and Reconcile are disabled for a process in a final state, with the reason', async () => {
    mockApi(processRoutes({ detail: detail({ businessStatus: 'COMPLETED' }) }));
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });
    for (const name of ['Cancel Process', 'Reconcile Provider']) {
      expect(action(name)).toBeDisabled();
      expect(action(name)).toHaveAttribute('title', 'The process is already in a final state');
    }
    expect(action('Inspect Provider Metadata')).toBeEnabled();
  });

  it('shows the API message when an operation is rejected and does not claim success', async () => {
    mockApi([...processRoutes(), { method: 'POST', match: BASE + '/cancel', handler: { status: 409, body: { title: 'Conflict', detail: 'Process is COMPLETED and cannot be cancelled' } } }]);
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });
    await userEvent.click(action('Cancel Process'));
    await userEvent.click(within(await dialog()).getByRole('button', { name: 'Cancel process' }));
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Conflict: Process is COMPLETED and cannot be cancelled');
    expect(screen.queryByText('Process cancelled.')).not.toBeInTheDocument();
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('every manual operation of the PRD is reachable from the detail page', async () => {
    const routes: Route[] = processRoutes({ ops: [failedOp], callbacks: [delivery()], artifacts: [artifact()] });
    mockApi(routes);
    renderApp('/processes/sig_1', 'ana.ops');
    await screen.findByRole('heading', { name: 'sig_1' });
    for (const name of ['Reconcile Provider', 'Reprocess From Operation', 'Cancel Process', 'Inspect Provider Metadata']) expect(action(name)).toBeEnabled();
    await userEvent.click(screen.getByRole('tab', { name: 'Operations' }));
    expect(await screen.findByRole('button', { name: /Retry Operation/ })).toBeEnabled();
    await userEvent.click(screen.getByRole('tab', { name: 'Callbacks' }));
    expect(await screen.findByRole('button', { name: /Retry Callback/ })).toBeEnabled();
    await userEvent.click(screen.getByRole('tab', { name: 'Artifacts' }));
    expect(await screen.findByRole('button', { name: /Download Artifact/ })).toBeEnabled();
  });
});
