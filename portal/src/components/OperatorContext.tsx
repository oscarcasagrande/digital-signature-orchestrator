import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
import { getOperator, setOperator as persist } from '../api/client';
import { OPERATE_ROLES, loadSession, onUnauthorized, saveSession, type Session } from '../api/auth';

interface OperatorState {
  /** Identity of manual actions: the signed-in user (when allowed to operate) or the typed operator id. */
  operator: string | null;
  save: (raw: string) => string | null;
  session: Session | null;
  /** True when the API answered 401 and the user has to sign in. */
  loginRequired: boolean;
  adopt: (session: Session) => void;
  logout: () => void;
  /** Why manual operations are disabled (undefined when enabled). */
  reason?: string;
}

const Ctx = createContext<OperatorState>({
  operator: null, save: () => null, session: null, loginRequired: false, adopt: () => {}, logout: () => {},
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
  const operator = session ? (canOperate ? session.username : null) : typed;
  const reason = session
    ? canOperate ? undefined : `Your role (${session.roles.filter((r) => ['viewer', 'client', 'operator', 'admin'].includes(r)).join(', ') || 'none'}) cannot operate`
    : !typed ? 'Set your operator id first' : undefined;

  return <Ctx.Provider value={{ operator, save, session, loginRequired, adopt, logout, reason }}>{children}</Ctx.Provider>;
}

export const useOperator = () => useContext(Ctx);
