import { useEffect, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { completeLogin } from '../api/auth';
import { useOperator } from '../components/OperatorContext';

/** Receives the authorization code from the identity provider and finishes the PKCE exchange. */
export function AuthCallback() {
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const { adopt } = useOperator();
  const [error, setError] = useState<string | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return; // the code is single use (effects may run twice in development)
    started.current = true;
    completeLogin(params.get('code'), params.get('state'), params.get('error'))
      .then(({ session, returnTo }) => {
        adopt(session);
        navigate(returnTo.startsWith('/') && !returnTo.startsWith('//') ? returnTo : '/', { replace: true });
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Sign-in failed.'));
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  if (error) {
    return (
      <section className="login">
        <p role="alert" className="error">{error}</p>
        <a href="/">Back to the portal</a>
      </section>
    );
  }
  return <p className="empty">Signing you in…</p>;
}
