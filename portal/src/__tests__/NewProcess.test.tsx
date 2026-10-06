import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { detail, listItem, mockApi, processRoutes, renderApp } from './mockApi';

const pdf = () => new File(['%PDF-1.4 test'], 'contrato.pdf', { type: 'application/pdf' });

const upload = { status: 201, body: { uploadId: 'upl_1', fileName: 'contrato.pdf', size: 13, sha256: 'a'.repeat(64) } };

function routes(createReply: (n: number) => { status?: number; body?: unknown } = () => ({ status: 202, body: { processId: 'sig_new' } })) {
  let creates = 0;
  return [
    { method: 'POST', match: '/v1/document-uploads', handler: upload },
    { method: 'POST', match: '/v1/signature-processes', handler: () => createReply(++creates) },
    ...processRoutes({ id: 'sig_new', detail: detail({ processId: 'sig_new' }) }),
  ];
}

async function fillFirstSigner(name = 'Maria Souza', cpf = '123.456.789-09') {
  await userEvent.type(screen.getByLabelText('Nome do signatário 1'), name);
  await userEvent.type(screen.getByLabelText('CPF do signatário 1'), cpf);
}

async function fillBase() {
  await userEvent.type(screen.getByLabelText('Identificador externo'), 'CONTRATO-9');
  await userEvent.upload(screen.getByLabelText('Documento (PDF)'), pdf());
}

describe('new process form', () => {
  it('is reachable from the process list', async () => {
    mockApi([{ match: '/v1/signature-processes', handler: { body: { items: [listItem()], page: 1, pageSize: 20, total: 1 } } }]);
    renderApp('/');
    expect(await screen.findByRole('link', { name: 'Novo processo' })).toHaveAttribute('href', '/processes/new');
  });

  it('blocks submission and explains what is missing, without calling the API', async () => {
    const api = mockApi(routes());
    renderApp('/processes/new');
    await userEvent.click(screen.getByRole('button', { name: 'Iniciar processo' }));
    expect(await screen.findByText('Informe o identificador externo.')).toBeInTheDocument();
    expect(screen.getByText('Selecione o documento a ser assinado.')).toBeInTheDocument();
    expect(screen.getByText('Informe o nome.')).toBeInTheDocument();
    expect(screen.getByText('O CPF deve ter 11 dígitos.')).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
  });

  it('requires the contact of every channel that was chosen', async () => {
    const api = mockApi(routes());
    renderApp('/processes/new');
    await fillBase();
    await fillFirstSigner();
    await userEvent.click(screen.getByLabelText('E-mail', { selector: 'input[type=checkbox]' }));
    await userEvent.click(screen.getByLabelText('SMS'));
    await userEvent.click(screen.getByRole('button', { name: 'Iniciar processo' }));
    expect(await screen.findByText('Obrigatório para confirmação por e-mail.')).toBeInTheDocument();
    expect(screen.getByText('Obrigatório para confirmação por SMS ou WhatsApp.')).toBeInTheDocument();
    expect(api.calls).toHaveLength(0);
  });

  it('adds and removes signers in the table', async () => {
    mockApi(routes());
    renderApp('/processes/new');
    const table = screen.getByRole('table', { name: 'Signatários' });
    expect(within(table).getAllByRole('row')).toHaveLength(2); // header + one signer
    expect(screen.getByRole('button', { name: 'Remover signatário 1' })).toBeDisabled(); // there is always one
    await userEvent.click(screen.getByRole('button', { name: 'Adicionar signatário' }));
    await userEvent.click(screen.getByRole('button', { name: 'Adicionar signatário' }));
    expect(within(table).getAllByRole('row')).toHaveLength(4);
    await userEvent.type(screen.getByLabelText('Nome do signatário 2'), 'Segundo');
    await userEvent.click(screen.getByRole('button', { name: 'Remover signatário 1' }));
    expect(within(table).getAllByRole('row')).toHaveLength(3);
    expect(screen.getByLabelText('Nome do signatário 1')).toHaveValue('Segundo');
  });

  it('uploads the document, creates the process with defaults and opens its detail', async () => {
    const api = mockApi(routes());
    renderApp('/processes/new');
    await fillBase();
    await fillFirstSigner();
    await userEvent.type(screen.getByLabelText('E-mail do signatário 1'), 'maria@example.com');
    await userEvent.click(screen.getByRole('button', { name: 'Adicionar signatário' }));
    await userEvent.type(screen.getByLabelText('Nome do signatário 2'), 'Joao Lima');
    await userEvent.type(screen.getByLabelText('CPF do signatário 2'), '12345678909');
    await userEvent.type(screen.getByLabelText('E-mail do signatário 2'), 'joao@example.com');
    await userEvent.type(screen.getByLabelText('Ordem do signatário 1'), '1');
    await userEvent.type(screen.getByLabelText('Ordem do signatário 2'), '2');
    await userEvent.selectOptions(screen.getByLabelText(/Tipo de assinatura/), 'QUALIFIED');
    await userEvent.click(screen.getByLabelText('E-mail', { selector: 'input[type=checkbox]' }));
    await userEvent.click(screen.getByRole('button', { name: 'Iniciar processo' }));

    await waitFor(() => expect(api.find('GET', /^\/v1\/signature-processes\/sig_new$/).length).toBeGreaterThan(0));
    const [uploadCall] = api.find('POST', /document-uploads/);
    expect(uploadCall.body.file).toMatchObject({ fileName: 'contrato.pdf' });
    expect(uploadCall.headers['Content-Type']).toBeUndefined(); // the browser sets the multipart boundary

    const [create] = api.find('POST', /^\/v1\/signature-processes$/);
    expect(create.headers['Idempotency-Key']).toMatch(/.{8,}/);
    expect(create.body).toEqual({
      externalId: 'CONTRATO-9',
      document: { fileName: 'contrato.pdf', source: { type: 'UPLOAD', uploadId: 'upl_1' } },
      signers: [
        { name: 'Maria Souza', document: '12345678909', email: 'maria@example.com', order: 1 },
        { name: 'Joao Lima', document: '12345678909', email: 'joao@example.com', order: 2 },
      ],
      defaults: { signatureType: 'QUALIFIED', confirmation: ['EMAIL'] },
    });
    expect(await screen.findByRole('heading', { name: /sig_new/ })).toBeInTheDocument();
  });

  it('shows API validation errors on the signer and field, and retries without duplicating the upload or the process', async () => {
    const api = mockApi(routes((n) => n === 1
      ? { status: 400, body: { title: 'Validation failed', detail: 'Validation failed', errors: {
        'signers[1].phone': ['Required when the confirmation channel SMS or WHATSAPP is used'],
        'signers[0].order': ['Order has a gap: no signer has order 1'],
        'document.source.uploadId': ['Unknown or expired upload'],
      } } }
      : { status: 202, body: { processId: 'sig_new' } }));
    renderApp('/processes/new');
    await fillBase();
    await fillFirstSigner();
    await userEvent.click(screen.getByRole('button', { name: 'Adicionar signatário' }));
    await userEvent.type(screen.getByLabelText('Nome do signatário 2'), 'Joao');
    await userEvent.type(screen.getByLabelText('CPF do signatário 2'), '12345678909');
    await userEvent.click(screen.getByRole('button', { name: 'Iniciar processo' }));

    const second = (await screen.findByLabelText('Telefone do signatário 2')).closest('td')!;
    expect(await within(second).findByText('Required when the confirmation channel SMS or WHATSAPP is used')).toBeInTheDocument();
    const firstOrder = screen.getByLabelText('Ordem do signatário 1').closest('td')!;
    expect(within(firstOrder).getByText('Order has a gap: no signer has order 1')).toBeInTheDocument();
    expect(screen.getByText(/document.source.uploadId: Unknown or expired upload/)).toBeInTheDocument();

    // same payload again (e.g. after a transient failure): same file upload, same idempotency key
    await userEvent.click(screen.getByRole('button', { name: 'Iniciar processo' }));
    await waitFor(() => expect(api.find('POST', /^\/v1\/signature-processes$/)).toHaveLength(2));
    expect(api.find('POST', /document-uploads/)).toHaveLength(1);
    const [first, retry] = api.find('POST', /^\/v1\/signature-processes$/);
    expect(retry.headers['Idempotency-Key']).toBe(first.headers['Idempotency-Key']);
  });

  it('uses a new idempotency key when the payload was edited after an error', async () => {
    const api = mockApi(routes((n) => n === 1 ? { status: 400, body: { title: 'Validation failed', errors: { 'signers[0].document': ['Invalid CPF'] } } } : { status: 202, body: { processId: 'sig_new' } }));
    renderApp('/processes/new');
    await fillBase();
    await fillFirstSigner('Maria', '11111111111');
    await userEvent.click(screen.getByRole('button', { name: 'Iniciar processo' }));
    await screen.findByText('Invalid CPF');
    await userEvent.clear(screen.getByLabelText('CPF do signatário 1'));
    await userEvent.type(screen.getByLabelText('CPF do signatário 1'), '12345678909');
    await userEvent.click(screen.getByRole('button', { name: 'Iniciar processo' }));
    await waitFor(() => expect(api.find('POST', /^\/v1\/signature-processes$/)).toHaveLength(2));
    const [a, b] = api.find('POST', /^\/v1\/signature-processes$/);
    expect(b.headers['Idempotency-Key']).not.toBe(a.headers['Idempotency-Key']);
  });

  it('reports an upload failure without creating the process', async () => {
    const api = mockApi([
      { method: 'POST', match: '/v1/document-uploads', handler: { status: 400, body: { title: 'Validation failed', detail: 'The file is empty' } } },
      { method: 'POST', match: '/v1/signature-processes', handler: { status: 202, body: { processId: 'x' } } },
    ]);
    renderApp('/processes/new');
    await fillBase();
    await fillFirstSigner();
    await userEvent.click(screen.getByRole('button', { name: 'Iniciar processo' }));
    expect(await screen.findByText(/The file is empty/)).toBeInTheDocument();
    expect(api.find('POST', /^\/v1\/signature-processes$/)).toHaveLength(0);
  });
});
