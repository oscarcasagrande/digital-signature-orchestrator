import { useState } from 'react';
import { beginLogin } from '../api/auth';
import { redirectTo } from '../util/navigation';

export function Login() {
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function start() {
    setBusy(true);
    setError(null);
    try {
      redirectTo(await beginLogin(window.location.pathname + window.location.search));
    } catch {
      setError('Could not start the sign-in.');
      setBusy(false);
    }
  }

  return (
    <section className="login" aria-labelledby="login-title">
      <h1 id="login-title">Sign in</h1>
      <p className="hint">
        The API requires authentication. You will be redirected to the identity provider. Demo users: viewer, operator, admin
        (password equals the user name).
      </p>
      {error && <p role="alert" className="error">{error}</p>}
      <button type="button" onClick={start} disabled={busy}>{busy ? 'Redirecting…' : 'Sign in'}</button>
    </section>
  );
}
