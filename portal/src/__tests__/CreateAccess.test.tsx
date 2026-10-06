import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { SESSION_KEY } from '../api/auth';
import { fakeToken, listItem, mockApi, processRoutes, renderApp } from './mockApi';

const page = { items: [listItem()], page: 1, pageSize: 20, total: 1 };
const listRoute = { match: '/v1/signature-processes', handler: { body: page } };

function signIn(role: string) {
  const exp = Math.floor(Date.now() / 1000) + 600;
  sessionStorage.setItem(SESSION_KEY, JSON.stringify({ token: fakeToken('user-' + role, [role]), username: 'user-' + role, roles: [role], expiresAt: exp }));
}

describe('who can start a process from the portal', () => {
  it.each(['operator', 'admin', 'client'])('%s sees the "Novo processo" button and the form', async (role) => {
    signIn(role);
    mockApi([listRoute, ...processRoutes()]);
    renderApp('/');
    expect(await screen.findByRole('link', { name: 'Novo processo' })).toHaveAttribute('href', '/processes/new');
  });

  it.each(['operator', 'admin'])('%s can open the form and submit the fields', async (role) => {
    signIn(role);
    mockApi([listRoute]);
    renderApp('/processes/new');
    expect(await screen.findByRole('form', { name: 'Novo processo de assinatura' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Iniciar processo' })).toBeEnabled();
  });

  it('a viewer does not see the button on the list', async () => {
    signIn('viewer');
    mockApi([listRoute]);
    renderApp('/');
    await screen.findByRole('link', { name: 'sig_1' });
    expect(screen.queryByRole('link', { name: 'Novo processo' })).not.toBeInTheDocument();
  });

  it('a viewer who types the address gets an explanation instead of the form, and nothing is sent', async () => {
    signIn('viewer');
    const api = mockApi([listRoute]);
    renderApp('/processes/new');
    expect(await screen.findByRole('alert')).toHaveTextContent(/não permite iniciar processos/);
    expect(screen.queryByRole('form', { name: 'Novo processo de assinatura' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Iniciar processo' })).not.toBeInTheDocument();
    expect(api.find('POST', /.*/)).toHaveLength(0);
  });

  it('without sign-in (open API, operator id) the button stays available', async () => {
    mockApi([listRoute]);
    renderApp('/', 'olga.ops');
    expect(await screen.findByRole('link', { name: 'Novo processo' })).toBeInTheDocument();
  });
});
