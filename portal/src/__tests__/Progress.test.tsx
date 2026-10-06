import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import type { Progress, Step } from '../api/types';
import { ProgressCell, currentStepText, groupSteps } from '../components/Progress';
import { detail, detailProgress, listItem, mockApi, processRoutes, renderApp } from './mockApi';

const page = (items: ReturnType<typeof listItem>[]) => ({ items, page: 1, pageSize: 20, total: items.length });

describe('progress column in the process list', () => {
  it('shows X/Y, a progress bar and the current step on hover', async () => {
    mockApi([{ match: '/v1/signature-processes', handler: { body: page([listItem()]) } }]);
    renderApp('/');
    const table = await screen.findByRole('table', { name: 'Signature processes' });
    expect(within(table).getByRole('columnheader', { name: 'Etapas' })).toBeInTheDocument();

    const row = within(within(table).getAllByRole('row')[1]);
    expect(row.getByText('3/7')).toBeInTheDocument();
    const bar = row.getByRole('progressbar', { name: 'Progresso das etapas' });
    expect(bar).toHaveAttribute('aria-valuenow', '3');
    expect(bar).toHaveAttribute('aria-valuemax', '7');
    expect(bar).toHaveAttribute('aria-valuetext', expect.stringContaining('Etapa atual: Assinatura - Joao Lima'));
    expect(bar.firstElementChild).toHaveStyle({ width: '43%' });
    // the tooltip (title) names the step in progress
    expect(row.getByText('3/7').parentElement).toHaveAttribute('title', 'Etapa atual: Assinatura - Joao Lima');
  });

  it('shows a finished process as complete and a cancelled one without a current step', async () => {
    const done: Progress = { completedSteps: 5, totalSteps: 5, currentStep: null };
    const cancelled: Progress = { completedSteps: 1, totalSteps: 5, currentStep: null };
    mockApi([{ match: '/v1/signature-processes', handler: { body: page([
      listItem({ processId: 'sig_done', businessStatus: 'COMPLETED', progress: done }),
      listItem({ processId: 'sig_off', businessStatus: 'CANCELLED', progress: cancelled }),
    ]) } }]);
    renderApp('/');
    const table = await screen.findByRole('table', { name: 'Signature processes' });
    const rows = within(table).getAllByRole('row').slice(1);
    expect(within(rows[0]).getByText('5/5').parentElement).toHaveAttribute('title', 'Todas as etapas concluídas');
    expect(within(rows[0]).getByRole('progressbar')).toHaveAttribute('aria-valuenow', '5');
    expect(within(rows[1]).getByText('1/5').parentElement).toHaveAttribute('title', 'Nenhuma etapa em andamento');
  });

  it('ProgressCell clamps an empty plan to zero width', () => {
    render(<ProgressCell progress={{ completedSteps: 0, totalSteps: 0, currentStep: null }} />);
    expect(screen.getByRole('progressbar').firstElementChild).toHaveStyle({ width: '0%' });
    expect(currentStepText({ completedSteps: 0, totalSteps: 0, currentStep: null })).toBe('Todas as etapas concluídas');
  });
});

describe('steps in the process detail', () => {
  it('has a Etapas tab with the steps grouped per signer and the state of each', async () => {
    mockApi(processRoutes({
      detail: detail({
        signers: [
          { id: 'sgn_1', externalId: 'c1', name: 'Maria Souza', document: '*********09', signed: true, signedAt: '2026-10-06T02:45:00Z',
            confirmations: [{ channel: 'EMAIL', status: 'CONFIRMED', sendCount: 1, lastSentAt: null, expiresAt: null, attemptsRemaining: null, confirmedAt: '2026-10-06T02:44:00Z' }] },
          { id: 'sgn_2', externalId: 'c2', name: 'Joao Lima', document: '*********09', signed: false, signedAt: null,
            confirmations: [{ channel: 'EMAIL', status: 'SENT', sendCount: 2, lastSentAt: null, expiresAt: null, attemptsRemaining: 3, confirmedAt: null }] },
        ],
      }),
    }));
    renderApp('/processes/sig_1');
    await userEvent.click(await screen.findByRole('tab', { name: 'Etapas' }));

    expect(await screen.findByText(/3\/7 etapas concluídas\. Etapa atual: Confirmação por e-mail - Joao Lima/)).toBeInTheDocument();
    const maria = screen.getByRole('region', { name: 'Etapas de Maria Souza' });
    expect(within(maria).getAllByRole('listitem')).toHaveLength(2);
    expect(within(maria).getAllByText('Concluída')).toHaveLength(2);
    expect(within(maria).getByText(/Confirmado/)).toBeInTheDocument();

    const joao = screen.getByRole('region', { name: 'Etapas de Joao Lima' });
    const items = within(joao).getAllByRole('listitem');
    expect(items[0]).toHaveAttribute('aria-current', 'step');
    expect(within(items[0]).getByText('Em andamento')).toBeInTheDocument();
    expect(within(items[0]).getByText(/Código enviado, 3 tentativas restantes, 2 envios/)).toBeInTheDocument();
    expect(within(items[1]).getByText('Pendente')).toBeInTheDocument();

    expect(screen.getByRole('region', { name: 'Etapas de Processo' })).toBeInTheDocument();
    const closing = screen.getByRole('region', { name: 'Etapas de Conclusão' });
    expect(within(closing).getByText('Documento final')).toBeInTheDocument();
    expect(within(closing).getByText('Callback')).toBeInTheDocument();
  });

  it('shows failed and cancelled steps with their own state', async () => {
    const steps: Step[] = detailProgress().steps.map((s) => (s.order === 4 ? { ...s, status: 'FAILED' } : s.order > 4 ? { ...s, status: 'CANCELLED' } : s));
    mockApi(processRoutes({ detail: detail({ businessStatus: 'FAILED', progress: { completedSteps: 3, totalSteps: 7, currentStep: null, steps } }) }));
    renderApp('/processes/sig_1?tab=steps');
    expect(await screen.findByText('Falhou')).toBeInTheDocument();
    expect(screen.getAllByText('Cancelada')).toHaveLength(3);
    expect(screen.getByText(/Nenhuma etapa em andamento/)).toBeInTheDocument();
  });

  it('groups document, signers and closing steps in plan order', () => {
    const groups = groupSteps(detailProgress().steps);
    expect(groups.map((g) => g.key)).toEqual(['start', 'sgn_1', 'sgn_2', 'end']);
  });
});
