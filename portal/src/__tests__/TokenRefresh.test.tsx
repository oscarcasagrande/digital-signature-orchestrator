import { act, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SESSION_KEY, type Session } from '../api/auth';
import { api } from '../api/client';
import { redirectTo } from '../util/navigation';
import { fakeToken, listItem, mockApi, renderApp, type Route } from './mockApi';

vi.mock('../util/navigation', () => ({ openDownload: vi.fn(), redirectTo: vi.fn() }));

const TOKEN_PATH = '/realms/orchestrator/protocol/openid-connect/token';
const page = { items: [listItem()], page: 1, pageSize: 20, total: 1 };
const now = () => Math.floor(Date.now() / 1000);

/** A stored session whose access token expires in `accessIn` seconds (negative: already expired). */
function storeSession(accessIn: number, extra: Partial<Session> = {}): Session {
  const token = fakeToken('olga.ops', ['operator'], accessIn);
  const s: Session = {
    token, username: 'olga.ops', roles: ['operator'], expiresAt: now() + accessIn, refreshToken: 'refresh-1', refreshExpiresAt: now() + 1800, ...extra,
  };
  sessionStorage.setItem(SESSION_KEY, JSON.stringify(s));
  return s;
}

const stored = (): Session | null => JSON.parse(sessionStorage.getItem(SESSION_KEY) ?? 'null');

/** Token endpoint that renews on `refresh_token`, rotating the refresh token; `fail` answers an error instead. */
function refreshRoute(opts: { accessIn?: number; fail?: { status: number; body?: unknown }; network?: boolean } = {}) {
  const calls: URLSearchParams[] = [];
  let n = 0;
  const route: Route = {
    method: 'POST', match: TOKEN_PATH, handler: (c) => {
      const form = new URLSearchParams(String(c.body));
      calls.push(form);
      if (opts.fail) return { status: opts.fail.status, body: opts.fail.body ?? { error: 'invalid_grant' } };
      if (form.get('grant_type') !== 'refresh_token' || form.get('client_id') !== 'portal') return { status: 400, body: { error: 'invalid_request' } };
      n++;
      return { body: { access_token: fakeToken('olga.ops', ['operator'], opts.accessIn ?? 900), refresh_token: 'refresh-' + (n + 1), refresh_expires_in: 1800 } };
    },
  };
  return { route, calls };
}

/** API that requires the Bearer token and records which one each call carried. */
function securedApi(accept: (auth: string) => boolean = () => true) {
  const seen: string[] = [];
  const route: Route = {
    match: '/v1/signature-processes', handler: (c) => {
      const auth = c.headers.Authorization ?? '';
      seen.push(auth);
      return auth && accept(auth) ? { body: page } : { status: 401, body: { title: 'Authentication required' } };
    },
  };
  return { route, seen };
}

afterEach(() => {
  vi.useRealTimers();
  vi.mocked(redirectTo).mockClear();
});

describe('access token renewal', () => {
  it('renews an expired access token with the refresh token before calling the API, without sending the user to sign in', async () => {
    storeSession(-60);
    const refresh = refreshRoute();
    const secured = securedApi();
    mockApi([refresh.route, secured.route]);
    renderApp('/');

    expect(await screen.findByRole('link', { name: 'sig_1' })).toBeInTheDocument();
    expect(refresh.calls).toHaveLength(1);
    expect(refresh.calls[0].get('refresh_token')).toBe('refresh-1');
    expect(refresh.calls[0].get('client_id')).toBe('portal');
    expect(secured.seen).toHaveLength(1);
    expect(secured.seen[0]).toBe('Bearer ' + stored()!.token); // the request carried the NEW token
    expect(redirectTo).not.toHaveBeenCalled();
  });

  it('renews a token that is about to expire (inside the safety margin), not only an expired one', async () => {
    const old = storeSession(10);
    const refresh = refreshRoute();
    const secured = securedApi();
    mockApi([refresh.route, secured.route]);
    renderApp('/');
    await screen.findByRole('link', { name: 'sig_1' });
    expect(refresh.calls).toHaveLength(1);
    expect(secured.seen[0]).not.toBe('Bearer ' + old.token);
  });

  it('does not touch the token endpoint while the access token is still comfortably valid', async () => {
    const s = storeSession(600);
    const refresh = refreshRoute();
    const secured = securedApi();
    mockApi([refresh.route, secured.route]);
    renderApp('/');
    await screen.findByRole('link', { name: 'sig_1' });
    expect(refresh.calls).toHaveLength(0);
    expect(secured.seen[0]).toBe('Bearer ' + s.token);
  });

  it('shares ONE refresh among concurrent requests (several pages load at once)', async () => {
    storeSession(-5);
    const refresh = refreshRoute();
    const secured = securedApi();
    mockApi([refresh.route, secured.route]);
    await Promise.all([api.listProcesses({}), api.listProcesses({}), api.listProcesses({}), api.listProcesses({})]);
    expect(refresh.calls).toHaveLength(1);
    expect(secured.seen).toHaveLength(4);
    expect(new Set(secured.seen).size).toBe(1);
  });

  it('stores the rotated refresh token and uses it for the next renewal', async () => {
    storeSession(-5);
    const refresh = refreshRoute({ accessIn: 1 }); // the renewed token is itself already inside the margin
    const secured = securedApi();
    mockApi([refresh.route, secured.route]);
    await api.listProcesses({});
    expect(stored()!.refreshToken).toBe('refresh-2');
    await api.listProcesses({});
    expect(refresh.calls.map((c) => c.get('refresh_token'))).toEqual(['refresh-1', 'refresh-2']);
    expect(stored()!.refreshToken).toBe('refresh-3');
    expect(stored()!.refreshExpiresAt).toBeGreaterThan(now());
  });

  it('keeps the roles and user of the renewed token', async () => {
    storeSession(-5, { roles: ['viewer'] });
    const refresh = refreshRoute();
    mockApi([refresh.route, securedApi().route]);
    await api.listProcesses({});
    expect(stored()!.roles).toEqual(['operator']);
    expect(stored()!.username).toBe('olga.ops');
  });

  it('a refresh token the identity provider rejects ends the session and asks for sign-in again', async () => {
    storeSession(-5);
    const refresh = refreshRoute({ fail: { status: 400 } });
    mockApi([refresh.route, securedApi().route]);
    renderApp('/');
    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(stored()).toBeNull();
  });

  it('an expired refresh token is not even tried', async () => {
    storeSession(-5, { refreshExpiresAt: now() - 5 });
    const refresh = refreshRoute();
    mockApi([refresh.route, securedApi().route]);
    renderApp('/');
    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(refresh.calls).toHaveLength(0);
    expect(stored()).toBeNull();
  });

  it('when the identity provider is down the session survives and the still-valid token keeps working', async () => {
    const s = storeSession(20); // inside the margin, but not expired yet
    const refresh = refreshRoute({ fail: { status: 503, body: {} } });
    const secured = securedApi();
    mockApi([refresh.route, secured.route]);
    renderApp('/');
    expect(await screen.findByRole('link', { name: 'sig_1' })).toBeInTheDocument();
    expect(refresh.calls.length).toBeGreaterThanOrEqual(1); // the request and the renewal timer may both try; neither ends the session
    expect(secured.seen[0]).toBe('Bearer ' + s.token);
    expect(stored()).not.toBeNull();
    expect(redirectTo).not.toHaveBeenCalled();
  });

  it('a 401 from the API (token refused early) renews once and replays the request', async () => {
    const s = storeSession(600);
    const refresh = refreshRoute();
    const secured = securedApi((auth) => auth !== 'Bearer ' + s.token); // the API no longer accepts the old token
    mockApi([refresh.route, secured.route]);
    renderApp('/');
    expect(await screen.findByRole('link', { name: 'sig_1' })).toBeInTheDocument();
    expect(refresh.calls).toHaveLength(1);
    expect(secured.seen).toHaveLength(2);
    expect(secured.seen[1]).not.toBe(secured.seen[0]);
  });

  it('gives up after one replay: a token that keeps being refused sends the user to sign in', async () => {
    storeSession(600);
    const refresh = refreshRoute();
    const secured = securedApi(() => false);
    mockApi([refresh.route, secured.route]);
    renderApp('/');
    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(refresh.calls).toHaveLength(1); // not a refresh loop
    expect(secured.seen).toHaveLength(2);
  });

  it('a session without a refresh token behaves as before: expired means sign in again', async () => {
    storeSession(-5, { refreshToken: undefined, refreshExpiresAt: undefined });
    const refresh = refreshRoute();
    mockApi([refresh.route, securedApi().route]);
    renderApp('/');
    expect(await screen.findByRole("heading", { name: "Sign in" })).toBeInTheDocument();
    expect(refresh.calls).toHaveLength(0);
  });

  it('renews on its own shortly before expiry while the page stays open', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'], shouldAdvanceTime: true });
    storeSession(120); // margin is 2 x 30 s, so the timer fires at about 60 s
    const refresh = refreshRoute();
    mockApi([refresh.route, securedApi().route]);
    renderApp('/');
    await screen.findByRole('link', { name: 'sig_1' });
    expect(refresh.calls).toHaveLength(0);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(65_000);
    });
    await waitFor(() => expect(refresh.calls).toHaveLength(1));
    expect(stored()!.refreshToken).toBe('refresh-2');
    expect(redirectTo).not.toHaveBeenCalled();
  });
});
