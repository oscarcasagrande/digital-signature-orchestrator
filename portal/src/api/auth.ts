/** OIDC session of the portal (authorization code flow with PKCE against Keycloak; kept in sessionStorage). */
export interface Session {
  token: string;
  username: string;
  roles: string[];
  expiresAt: number;
}

export const SESSION_KEY = 'session';
export const TOKEN_URL = '/realms/orchestrator/protocol/openid-connect/token';
export const CLIENT_ID = 'portal';
export const CALLBACK_PATH = '/auth/callback';
export const PKCE_KEY = 'pkce';
export const OPERATE_ROLES = ['operator', 'admin'];

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

export function loadSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(SESSION_KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as Session;
    if (!s.token || s.expiresAt * 1000 < Date.now()) {
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

export function getToken(): string | null {
  return loadSession()?.token ?? null;
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
  const { access_token } = (await res.json()) as { access_token: string };
  const claims = decodeToken(access_token);
  if (!claims) throw new Error('The identity provider returned an invalid token.');
  const session: Session = { token: access_token, username: claims.username, roles: claims.roles, expiresAt: claims.exp };
  saveSession(session);
  return { session, returnTo: saved.returnTo };
}
