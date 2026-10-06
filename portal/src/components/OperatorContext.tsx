import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { getOperator, setOperator as persist } from '../api/client';
import { CREATE_ROLES, OPERATE_ROLES, REFRESH_SKEW_SECONDS, loadSession, onSessionChange, onUnauthorized, refreshSession, saveSession, type Session } from '../api/auth';

interface OperatorState {
  /** Identity of manual actions: the signed-in user (when allowed to operate) or the typed operator id. */
  operator: string | null;
  save: (raw: string) => string | null;
  session: Session | null;
  /** True when the API answered 401 and the user has to sign in. */
  loginRequired: boolean;
  adopt: (session: Session) => void;
  logout: () => void;
  /** True when the user may upload a document and start a process (any role but viewer; always true without sign-in, i.e. open API). */
  canCreateProcess: boolean;
  /** Why manual operations are disabled (undefined when enabled). */
  reason?: string;
}

const Ctx = createContext<OperatorState>({
  operator: null, save: () => null, session: null, loginRequired: false, adopt: () => {}, logout: () => {}, canCreateProcess: true,
});

export function OperatorProvider({ children }: { children: ReactNode }) {
  const [typed, setTyped] = useState<string | null>(() => getOperator());
  const [session, setSession] = useState<Session | null>(() => loadSession());
  const [loginRequired, setLoginRequired] = useState(false);

  useEffect(() => {
    onUnauthorized(() => {
      saveSession(null);
      setSession(null);
      setLoginRequired(true);
    });
    return () => onUnauthorized(null);
  }, []);

  // Token renewal: the session changes when a request renews it (or ends it), and a timer renews it shortly before it expires
  // so an open tab never falls back to the login page in the middle of a task.
  useEffect(() => {
    onSessionChange((s) => setSession(s));
    return () => onSessionChange(null);
  }, []);

  const expiresAt = session?.expiresAt;
  const renewable = !!session?.refreshToken;
  useEffect(() => {
    if (!expiresAt || !renewable) return;
    const wait = Math.min(Math.max(expiresAt * 1000 - Date.now() - REFRESH_SKEW_SECONDS * 2000, 0), 2 ** 31 - 1);
    const t = setTimeout(() => {
      if (loadSession()?.expiresAt !== expiresAt) return; // already renewed by a request in the meantime
      refreshSession().catch(() => {
        /* identity provider unreachable: the next request retries */
      });
    }, wait);
    return () => clearTimeout(t);
  }, [expiresAt, renewable]);

  const save = (raw: string) => {
    const v = persist(raw);
    setTyped(v);
    return v;
  };
  const adopt = (s: Session) => {
    setSession(s);
    setLoginRequired(false);
  };
  const logout = () => {
    saveSession(null);
    setSession(null);
    setLoginRequired(true);
  };

  const canOperate = !!session && session.roles.some((r) => OPERATE_ROLES.includes(r));
  const canCreateProcess = !session || session.roles.some((r) => CREATE_ROLES.includes(r));
  const operator = session ? (canOperate ? session.username : null) : typed;
  const reason = session
    ? canOperate ? undefined : `Your role (${session.roles.filter((r) => ['viewer', 'client', 'operator', 'admin'].includes(r)).join(', ') || 'none'}) cannot operate`
    : !typed ? 'Set your operator id first' : undefined;

  return <Ctx.Provider value={{ operator, save, session, loginRequired, adopt, logout, reason, canCreateProcess }}>{children}</Ctx.Provider>;
}

export const useOperator = () => useContext(Ctx);
