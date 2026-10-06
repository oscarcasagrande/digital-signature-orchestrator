import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { PKCE_KEY, SESSION_KEY, base64Url, beginLogin, codeChallenge } from '../api/auth';
import { redirectTo } from '../util/navigation';
import { fakeToken, listItem, mockApi, processRoutes, renderApp, type Route } from './mockApi';

vi.mock('../util/navigation', () => ({ openDownload: vi.fn(), redirectTo: vi.fn() }));

const TOKEN_PATH = '/realms/orchestrator/protocol/openid-connect/token';
const page = { items: [listItem()], page: 1, pageSize: 20, total: 1 };

/** Token endpoint of the authorization code flow: accepts only the verifier that matches the stored challenge. */
const tokenRoute = (roles: string[], user = 'olga.ops', expectCode = 'good-code'): Route => ({
  method: 'POST', match: TOKEN_PATH, handler: (c) => {
    const form = new URLSearchParams(String(c.body));
    return form.get('grant_type') === 'authorization_code' && form.get('code') === expectCode && form.get('code_verifier')
      ? { body: { access_token: fakeToken(user, roles) } }
      : { status: 400, body: { error: 'invalid_grant' } };
  },
});

/** API that requires a Bearer token on every call. */
const securedList = (): Route => ({
  match: '/v1/signature-processes', handler: (c) =>
    c.headers.Authorization ? { body: page } : { status: 401, body: { title: 'Authentication required' } },
});

describe('PKCE', () => {
  it('derives the S256 challenge exactly as RFC 7636 appendix B', async () => {
    expect(await codeChallenge('dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk')).toBe('E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
  });

  it('encodes base64url without padding or unsafe characters', () => {
    expect(base64Url(new Uint8Array([251, 255, 254]))).toBe('-__-');
  });

  it('builds an authorization code request with S256, state and a verifier that matches the challenge', async () => {
    const url = new URL(await beginLogin('/dead-letters'));
    expect(url.pathname).toBe('/realms/orchestrator/protocol/openid-connect/auth');
    const p = url.searchParams;
    expect(p.get('response_type')).toBe('code');
    expect(p.get('client_id')).toBe('portal');
    expect(p.get('code_challenge_method')).toBe('S256');
    expect(p.get('redirect_uri')).toBe(window.location.origin + '/auth/callback');
    expect(p.get('scope')).toBe('openid');
    const saved = JSON.parse(sessionStorage.getItem(PKCE_KEY)!);
    expect(saved.state).toBe(p.get('state'));
    expect(saved.returnTo).toBe('/dead-letters');
    expect(saved.verifier.length).toBeGreaterThanOrEqual(43);
    expect(await codeChallenge(saved.verifier)).toBe(p.get('code_challenge'));
    expect(url.search).not.toContain(saved.verifier); // the verifier never travels in the authorization request
  });

  it('uses a fresh verifier and state on every attempt', async () => {
    await beginLogin();
    const first = JSON.parse(sessionStorage.getItem(PKCE_KEY)!);
    await beginLogin();
    const second = JSON.parse(sessionStorage.getItem(PKCE_KEY)!);
    expect(second.verifier).not.toBe(first.verifier);
    expect(second.state).not.toBe(first.state);
  });
});

describe('sign-in with authorization code and PKCE', () => {
  it('redirects to the identity provider when the API answers 401', async () => {
    mockApi([securedList()]);
    renderApp('/');
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument(); // no password form in the portal any more
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    await waitFor(() => expect(redirectTo).toHaveBeenCalledTimes(1));
    const target = new URL(vi.mocked(redirectTo).mock.calls[0][0]);
    expect(target.searchParams.get('code_challenge_method')).toBe('S256');
    expect(target.searchParams.get('response_type')).toBe('code');
  });

  it('exchanges the code with the verifier on the callback, stores the session and returns to the page', async () => {
    const api = mockApi([securedList(), tokenRoute(['operator'])]);
    const url = new URL(await beginLogin('/dead-letters'));
    const state = url.searchParams.get('state')!;
    const verifier = JSON.parse(sessionStorage.getItem(PKCE_KEY)!).verifier;
    mockApi([securedList(), tokenRoute(['operator']), { match: '/v1/dead-letters', handler: { body: { items: [], page: 1, pageSize: 20, total: 0 } } }]);

    renderApp(`/auth/callback?code=good-code&state=${state}`);
    expect(await screen.findByRole('heading', { name: 'Dead letters' })).toBeInTheDocument();
    const calls = vi.mocked(fetch).mock.calls.filter(([u]) => String(u).includes('openid-connect/token'));
    expect(calls).toHaveLength(1);
    const form = new URLSearchParams(String((calls[0][1] as RequestInit).body));
    expect(form.get('grant_type')).toBe('authorization_code');
    expect(form.get('code')).toBe('good-code');
    expect(form.get('code_verifier')).toBe(verifier);
    expect(form.get('client_id')).toBe('portal');
    expect(form.get('redirect_uri')).toBe(window.location.origin + '/auth/callback');
    expect(form.get('grant_type')).not.toBe('password');
    expect(sessionStorage.getItem(PKCE_KEY)).toBeNull(); // verifier and state are single use
    expect(JSON.parse(sessionStorage.getItem(SESSION_KEY)!).username).toBe('olga.ops');
    expect(screen.getByText('olga.ops')).toBeInTheDocument();
    expect(api.calls.length).toBe(0);
  });

  it('rejects a callback whose state does not match and never calls the token endpoint', async () => {
    await beginLogin();
    const api = mockApi([tokenRoute(['operator'])]);
    renderApp('/auth/callback?code=good-code&state=forged');
    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid sign-in response');
    expect(api.calls).toHaveLength(0);
    expect(sessionStorage.getItem(SESSION_KEY)).toBeNull();
  });

  it('rejects a callback without a pending login (replay or direct access)', async () => {
    const api = mockApi([tokenRoute(['operator'])]);
    renderApp('/auth/callback?code=good-code&state=whatever');
    expect(await screen.findByRole('alert')).toHaveTextContent('Invalid sign-in response');
    expect(api.calls).toHaveLength(0);
  });

  it('shows the error returned by the identity provider', async () => {
    await beginLogin();
    mockApi([]);
    renderApp('/auth/callback?error=access_denied');
    expect(await screen.findByRole('alert')).toHaveTextContent('access_denied');
  });

  it('reports a failed token exchange', async () => {
    const state = new URL(await beginLogin()).searchParams.get('state')!;
    mockApi([tokenRoute(['operator'], 'olga.ops', 'another-code')]);
    renderApp(`/auth/callback?code=good-code&state=${state}`);
    expect(await screen.findByRole('alert')).toHaveTextContent('Sign-in failed (HTTP 400)');
    expect(sessionStorage.getItem(SESSION_KEY)).toBeNull();
  });

  it('an expired or rejected token sends the user back to sign-in', async () => {
    sessionStorage.setItem(SESSION_KEY, JSON.stringify({ token: fakeToken('olga.ops', ['operator']), username: 'olga.ops', roles: ['operator'], expiresAt: Math.floor(Date.now() / 1000) + 600 }));
    mockApi([{ match: '/v1/signature-processes', handler: { status: 401, body: { title: 'Authentication required' } } }]);
    renderApp('/');
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(sessionStorage.getItem(SESSION_KEY)).toBeNull();
  });

  it('sign out clears the session and asks for sign-in again', async () => {
    sessionStorage.setItem(SESSION_KEY, JSON.stringify({ token: fakeToken('vera', ['viewer']), username: 'vera', roles: ['viewer'], expiresAt: Math.floor(Date.now() / 1000) + 600 }));
    mockApi([securedList()]);
    renderApp('/');
    await screen.findByRole('table');
    await userEvent.click(screen.getByRole('button', { name: 'Sign out' }));
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
    expect(sessionStorage.getItem(SESSION_KEY)).toBeNull();
  });
});

describe('roles in the detail page', () => {
  const session = (roles: string[], user: string) =>
    sessionStorage.setItem(SESSION_KEY, JSON.stringify({ token: fakeToken(user, roles), username: user, roles, expiresAt: Math.floor(Date.now() / 1000) + 600 }));

  it('a viewer can read but every manual action is disabled with the reason', async () => {
    session(['viewer'], 'vera');
    mockApi(processRoutes());
    renderApp('/processes/sig_1');
    await screen.findByRole('heading', { name: 'sig_1' });
    for (const name of ['Cancel Process', 'Reconcile Provider', 'Inspect Provider Metadata']) {
      expect(screen.getByRole('button', { name })).toBeDisabled();
      expect(screen.getByRole('button', { name })).toHaveAttribute('title', expect.stringContaining('viewer'));
    }
    expect(screen.getByText('vera')).toBeInTheDocument();
  });

  it('an operator signed in needs no operator id: actions are enabled and calls carry the token', async () => {
    session(['operator'], 'olga.ops');
    const api = mockApi([...processRoutes(), { method: 'POST', match: '/v1/signature-processes/sig_1/cancel', handler: { body: {} } }]);
    renderApp('/processes/sig_1');
    await screen.findByRole('heading', { name: 'sig_1' });
    const cancel = screen.getByRole('button', { name: 'Cancel Process' });
    expect(cancel).toBeEnabled();
    expect(screen.queryByLabelText('Operator id')).not.toBeInTheDocument();
    await userEvent.click(cancel);
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Cancel process' }));
    await waitFor(() => expect(api.find('POST', /cancel$/)).toHaveLength(1));
    const call = api.find('POST', /cancel$/)[0];
    expect(call.headers.Authorization).toMatch(/^Bearer /);
    expect(call.headers['X-Operator-Id']).toBeUndefined();
  });
});
