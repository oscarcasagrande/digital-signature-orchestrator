import { useState } from 'react';
import { useOperator } from './OperatorContext';

/** Operator identity (sent as X-Operator-Id so every manual action is audited). Replaced by sign-in once OIDC exists. */
export function OperatorBar() {
  const { operator, save, session, logout } = useOperator();
  const [editing, setEditing] = useState(!operator);
  const [draft, setDraft] = useState(operator ?? '');

  if (session) {
    const roles = session.roles.filter((r) => ['viewer', 'operator', 'client', 'admin'].includes(r));
    return (
      <div className="operator">
        <span>
          Signed in as <strong>{session.username}</strong> ({roles.join(', ') || 'no role'})
        </span>
        <button type="button" className="link" onClick={logout}>
          Sign out
        </button>
      </div>
    );
  }
  if (!editing && operator) {
    return (
      <div className="operator">
        <span>
          Operator: <strong>{operator}</strong>
        </span>
        <button type="button" className="link" onClick={() => { setDraft(operator); setEditing(true); }}>
          Change
        </button>
      </div>
    );
  }
  return (
    <form
      className="operator"
      onSubmit={(e) => {
        e.preventDefault();
        if (save(draft)) setEditing(false);
      }}
    >
      <label htmlFor="operator-id">Operator id</label>
      <input
        id="operator-id"
        value={draft}
        maxLength={100}
        placeholder="e.g. maria.ops"
        onChange={(e) => setDraft(e.target.value)}
        aria-describedby="operator-hint"
      />
      <button type="submit" disabled={!draft.trim()}>
        Save
      </button>
      <span id="operator-hint" className="hint">
        Required for manual actions; recorded in the audit trail.
      </span>
    </form>
  );
}
