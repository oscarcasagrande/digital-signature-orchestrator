/** OIDC session of the portal (authorization code flow with PKCE against Keycloak; kept in sessionStorage). */
export interface Session {
  token: string;
  username: string;
  roles: string[];
  /** Access token expiry (epoch seconds). */
  expiresAt: number;
  /** Refresh token (kept in sessionStorage like the access token) and its expiry (epoch seconds), when the IdP issued one. */
  refreshToken?: string;
  refreshExpiresAt?: number;
}

export const SESSION_KEY = 'session';
export const TOKEN_URL = '/realms/orchestrator/protocol/openid-connect/token';
export const CLIENT_ID = 'portal';
export const CALLBACK_PATH = '/auth/callback';
export const PKCE_KEY = 'pkce';
export const OPERATE_ROLES = ['operator', 'admin'];
/** Roles allowed to upload a document and start a process (viewers can only read). */
export const CREATE_ROLES = ['client', 'operator', 'admin'];
/** The access token is renewed when it has less than this left. */
export const REFRESH_SKEW_SECONDS = 30;

let handler: (() => void) | null = null;

/** Called when the API answers 401 (missing or expired token). */
export function onUnauthorized(fn: (() => void) | null) {
  handler = fn;
}
export function notifyUnauthorized() {
  handler?.();
}

export function decodeToken(token: string): { username: string; roles: string[]; exp: number } | null {
  try {
    const part = token.split('.')[1];
    const json = decodeURIComponent(
      atob(part.replace(/-/g, '+').replace(/_/g, '/'))
        .split('')
        .map((c) => '%' + c.charCodeAt(0).toString(16).padStart(2, '0'))
        .join(''),
    );
    const c = JSON.parse(json);
    return { username: c.preferred_username ?? c.sub ?? 'unknown', roles: c.realm_access?.roles ?? [], exp: Number(c.exp) || 0 };
  } catch {
    return null;
  }
}

/**
 * The stored session, or null when there is none or nothing in it can still be used: an expired access token is kept while
 * a refresh token that has not expired can renew it.
 */
export function loadSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as Session;
    const accessValid = s.expiresAt * 1000 > Date.now();
    const refreshValid = !!s.refreshToken && (s.refreshExpiresAt === undefined || s.refreshExpiresAt * 1000 > Date.now());
    if (!s.token || (!accessValid && !refreshValid)) {
      sessionStorage.removeItem(SESSION_KEY);
      return null;
    }
    return s;
  } catch {
    return null;
  }
}

export function saveSession(s: Session | null) {
  try {
    if (s) sessionStorage.setItem(SESSION_KEY, JSON.stringify(s));
    else sessionStorage.removeItem(SESSION_KEY);
  } catch {
    /* storage unavailable */
  }
}

/** Current access token as stored (it may be about to expire; use <see cref="validToken"/> before calling the API). */
export function getToken(): string | null {
  return loadSession()?.token ?? null;
}

let sessionListener: ((s: Session | null) => void) | null = null;

/** Notified whenever the session is renewed or ended outside React (token refresh). */
export function onSessionChange(fn: ((s: Session | null) => void) | null) {
  sessionListener = fn;
}

function sessionFromTokenResponse(r: { access_token: string; refresh_token?: string; refresh_expires_in?: number }, previous?: Session): Session | null {
  const claims = decodeToken(r.access_token);
  if (!claims) return null;
  const refreshToken = r.refresh_token ?? previous?.refreshToken;
  return {
    token: r.access_token,
    username: claims.username,
    roles: claims.roles,
    expiresAt: claims.exp,
    refreshToken,
    refreshExpiresAt: r.refresh_token
      ? r.refresh_expires_in ? Math.floor(Date.now() / 1000) + r.refresh_expires_in : undefined
      : previous?.refreshExpiresAt,
  };
}

function endSession() {
  saveSession(null);
  sessionListener?.(null);
  notifyUnauthorized();
}

let refreshing: Promise<Session | null> | null = null;

/**
 * Renews the access token with the refresh token (one request at a time, shared by every caller). Resolves with the new session,
 * or null when the session cannot be renewed (no refresh token, refresh token expired or revoked): in that case the session is
 * ended and the user has to sign in again. A network or server failure rejects and keeps the session, so a later call can retry.
 */
export function refreshSession(): Promise<Session | null> {
  refreshing ??= doRefresh().finally(() => {
    refreshing = null;
  });
  return refreshing;
}

async function doRefresh(): Promise<Session | null> {
  const current = loadSession();
  if (!current?.refreshToken) {
    endSession();
    return null;
  }
  const body = new URLSearchParams({ grant_type: 'refresh_token', client_id: CLIENT_ID, refresh_token: current.refreshToken });
  const res = await fetch(TOKEN_URL, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body });
  if (res.status === 400 || res.status === 401) {
    endSession(); // invalid_grant: expired, revoked or reused refresh token
    return null;
  }
  if (!res.ok) throw new Error(`The identity provider could not renew the session (HTTP ${res.status}).`);
  const next = sessionFromTokenResponse(await res.json(), current);
  if (!next) {
    endSession();
    return null;
  }
  saveSession(next);
  sessionListener?.(next);
  return next;
}

/** An access token good for at least REFRESH_SKEW_SECONDS, renewing it first when needed; null when there is no session. */
export async function validToken(): Promise<string | null> {
  const s = loadSession();
  if (!s) return null;
  if (s.expiresAt - Date.now() / 1000 > REFRESH_SKEW_SECONDS) return s.token;
  if (!s.refreshToken) return s.token; // nothing to renew with: let the API decide (it answers 401 when expired)
  try {
    return (await refreshSession())?.token ?? null;
  } catch {
    return s.expiresAt * 1000 > Date.now() ? s.token : null; // IdP unreachable: use the old token while it still works
  }
}

/** Browser-facing authorization endpoint (the identity provider origin; overridable with VITE_AUTH_URL). */
export function authorizationUrl(): string {
  const env = (import.meta as unknown as { env?: Record<string, string | undefined> }).env;
  const base = env?.VITE_AUTH_URL ?? `${window.location.protocol}//${window.location.hostname}:8180/realms/orchestrator`;
  return `${base.replace(/\/$/, '')}/protocol/openid-connect/auth`;
}

export function base64Url(bytes: Uint8Array): string {
  let s = '';
  bytes.forEach((b) => (s += String.fromCharCode(b)));
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function randomString(byteLength = 48): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(byteLength)));
}

/** RFC 7636 S256: BASE64URL(SHA256(verifier)). */
export async function codeChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
  return base64Url(new Uint8Array(digest));
}

/** Builds the authorization request (authorization code + PKCE) and remembers verifier, state and the page to return to. */
export async function beginLogin(returnTo = '/'): Promise<string> {
  const verifier = randomString();
  const state = randomString(24);
  sessionStorage.setItem(PKCE_KEY, JSON.stringify({ verifier, state, returnTo }));
  const q = new URLSearchParams({
    client_id: CLIENT_ID,
    response_type: 'code',
    scope: 'openid',
    redirect_uri: window.location.origin + CALLBACK_PATH,
    state,
    code_challenge: await codeChallenge(verifier),
    code_challenge_method: 'S256',
  });
  return `${authorizationUrl()}?${q}`;
}

/** Completes the flow: validates state, exchanges the code (with the verifier) for a token and stores the session. */
export async function completeLogin(
  code: string | null,
  state: string | null,
  error?: string | null,
): Promise<{ session: Session; returnTo: string }> {
  let saved: { verifier: string; state: string; returnTo: string } | null = null;
  try {
    saved = JSON.parse(sessionStorage.getItem(PKCE_KEY) ?? 'null');
    sessionStorage.removeItem(PKCE_KEY); // single use
  } catch {
    saved = null;
  }
  if (error) throw new Error(`Sign-in was not completed (${error}).`);
  if (!code || !saved || !state || state !== saved.state) throw new Error('Invalid sign-in response. Please try again.');

  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    client_id: CLIENT_ID,
    code,
    code_verifier: saved.verifier,
    redirect_uri: window.location.origin + CALLBACK_PATH,
  });
  let res: Response;
  try {
    res = await fetch(TOKEN_URL, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body });
  } catch {
    throw new Error('The identity provider is unreachable.');
  }
  if (!res.ok) throw new Error(`Sign-in failed (HTTP ${res.status}).`);
  const tokens = (await res.json()) as { access_token: string; refresh_token?: string; refresh_expires_in?: number };
  const access_token = tokens.access_token;
  const claims = decodeToken(access_token);
  if (!claims) throw new Error('The identity provider returned an invalid token.');
  const session = sessionFromTokenResponse(tokens)!;
  saveSession(session);
  return { session, returnTo: saved.returnTo };
}
