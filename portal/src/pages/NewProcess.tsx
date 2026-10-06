import { useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ApiError, api } from '../api/client';
import type { NewProcessRequest, NewSigner } from '../api/types';
import { errorNotice, Notice, type NoticeState } from '../components/Notice';
import { useOperator } from '../components/OperatorContext';

interface Row {
  key: number;
  name: string;
  document: string;
  email: string;
  phone: string;
  order: string;
}

const CHANNELS = [
  { value: 'EMAIL', label: 'E-mail' },
  { value: 'SMS', label: 'SMS' },
  { value: 'WHATSAPP', label: 'WhatsApp' },
];
const SIGNATURE_TYPES = ['SIMPLE', 'ADVANCED', 'QUALIFIED'];

let nextKey = 1;
const emptyRow = (): Row => ({ key: nextKey++, name: '', document: '', email: '', phone: '', order: '' });

const newId = () =>
  typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(16).slice(2)}`;

/** Errors from the API are keyed "signers[i].field"; they are shown next to the signer row and field. */
export function fieldErrors(errors: Record<string, string[]> | undefined, index: number, field: string): string[] {
  return errors?.[`signers[${index}].${field}`] ?? [];
}

export function NewProcess() {
  const navigate = useNavigate();
  const { canCreateProcess, session } = useOperator();
  const [externalId, setExternalId] = useState('');
  const [file, setFile] = useState<File | null>(null);
  const [signatureType, setSignatureType] = useState('ADVANCED');
  const [channels, setChannels] = useState<string[]>([]);
  const [rows, setRows] = useState<Row[]>([emptyRow()]);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<NoticeState | null>(null);
  const [errors, setErrors] = useState<Record<string, string[]> | undefined>();
  const [local, setLocal] = useState<Record<string, string>>({});
  // One key per form submission attempt, so a retry after a network failure cannot create a second process.
  const attempt = useRef<{ body: string; key: string } | null>(null);
  const uploaded = useRef<{ file: File; id: string } | null>(null);

  const update = (key: number, patch: Partial<Row>) => setRows((rs) => rs.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const toggleChannel = (c: string) => setChannels((cs) => (cs.includes(c) ? cs.filter((x) => x !== c) : [...cs, c]));

  const validate = (): Record<string, string> => {
    const e: Record<string, string> = {};
    if (!externalId.trim()) e.externalId = 'Informe o identificador externo.';
    if (!file) e.file = 'Selecione o documento a ser assinado.';
    rows.forEach((r, i) => {
      if (!r.name.trim()) e[`signers[${i}].name`] = 'Informe o nome.';
      if (r.document.replace(/\D/g, '').length !== 11) e[`signers[${i}].document`] = 'O CPF deve ter 11 dígitos.';
      if (channels.includes('EMAIL') && !r.email.trim()) e[`signers[${i}].email`] = 'Obrigatório para confirmação por e-mail.';
      if ((channels.includes('SMS') || channels.includes('WHATSAPP')) && !r.phone.trim()) e[`signers[${i}].phone`] = 'Obrigatório para confirmação por SMS ou WhatsApp.';
    });
    return e;
  };

  const onSubmit = async (ev: FormEvent) => {
    ev.preventDefault();
    setNotice(null);
    setErrors(undefined);
    const found = validate();
    setLocal(found);
    if (Object.keys(found).length > 0) return;

    setBusy(true);
    try {
      // The upload is reused while the same file stays selected, so a retried submission carries an identical payload.
      if (uploaded.current?.file !== file) uploaded.current = { file: file!, id: (await api.uploadDocument(file!)).uploadId };
      const upload = { uploadId: uploaded.current.id };
      const signers: NewSigner[] = rows.map((r) => ({
        name: r.name.trim(),
        document: r.document.replace(/\D/g, ''),
        ...(r.email.trim() ? { email: r.email.trim() } : {}),
        ...(r.phone.trim() ? { phone: r.phone.trim() } : {}),
        ...(r.order.trim() ? { order: Number(r.order) } : {}),
      }));
      const request: NewProcessRequest = {
        externalId: externalId.trim(),
        document: { fileName: file!.name, source: { type: 'UPLOAD', uploadId: upload.uploadId } },
        signers,
        defaults: { signatureType, confirmation: channels },
      };
      // Same payload keeps the key (a retry after a network failure replays instead of duplicating); an edited payload gets a new one.
      const body = JSON.stringify(request);
      if (attempt.current?.body !== body) attempt.current = { body, key: newId() };
      const created = await api.createProcess(request, attempt.current.key);
      navigate('/processes/' + created.processId);
    } catch (e) {
      setNotice(errorNotice(e));
      if (e instanceof ApiError) setErrors(e.errors);
    } finally {
      setBusy(false);
    }
  };

  const cell = (i: number, field: string) => {
    const msgs = [...fieldErrors(errors, i, field), ...(local[`signers[${i}].${field}`] ? [local[`signers[${i}].${field}`]] : [])];
    return msgs.map((m) => <div key={m} className="field-error" role="alert">{m}</div>);
  };

  const general = errors ? Object.entries(errors).filter(([k]) => !k.startsWith('signers[')) : [];

  if (!canCreateProcess) {
    return (
      <section aria-labelledby="new-title">
        <p><Link to="/">← Processos</Link></p>
        <h1 id="new-title">Novo processo</h1>
        <p role="alert" className="notice notice-error">
          Seu perfil ({session?.roles.filter((r) => ['viewer', 'client', 'operator', 'admin'].includes(r)).join(', ') || 'sem papel'}) não permite iniciar processos.
          Peça a um operador ou administrador.
        </p>
      </section>
    );
  }

  return (
    <section aria-labelledby="new-title">
      <p><Link to="/">← Processos</Link></p>
      <h1 id="new-title">Novo processo</h1>
      {notice && <Notice notice={notice} onDismiss={() => setNotice(null)} />}
      {general.length > 0 && (
        <ul className="plain" role="alert">
          {general.map(([k, m]) => <li key={k} className="field-error">{k}: {m.join(', ')}</li>)}
        </ul>
      )}

      <form onSubmit={onSubmit} noValidate className="form-grid" aria-label="Novo processo de assinatura">
        <label>
          Identificador externo
          <input value={externalId} onChange={(e) => setExternalId(e.target.value)} maxLength={200} aria-invalid={!!local.externalId} />
          {local.externalId && <span className="field-error" role="alert">{local.externalId}</span>}
        </label>

        <label>
          Documento (PDF)
          <input type="file" accept="application/pdf,.pdf" onChange={(e) => setFile(e.target.files?.[0] ?? null)} aria-invalid={!!local.file} />
          {local.file && <span className="field-error" role="alert">{local.file}</span>}
        </label>

        <label>
          Tipo de assinatura (padrão de todos os signatários)
          <select value={signatureType} onChange={(e) => setSignatureType(e.target.value)}>
            {SIGNATURE_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
        </label>

        <fieldset>
          <legend>Confirmação por código antes de assinar</legend>
          {CHANNELS.map((c) => (
            <label key={c.value} className="check">
              <input type="checkbox" checked={channels.includes(c.value)} onChange={() => toggleChannel(c.value)} />
              {c.label}
            </label>
          ))}
        </fieldset>

        <div>
          <h2>Signatários</h2>
          <div className="table-wrap">
            <table>
              <caption className="sr-only">Signatários</caption>
              <thead>
                <tr>
                  <th scope="col">Nome</th><th scope="col">CPF</th><th scope="col">E-mail</th><th scope="col">Telefone</th>
                  <th scope="col">Ordem</th><th scope="col"><span className="sr-only">Ações</span></th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r, i) => (
                  <tr key={r.key}>
                    <td><input aria-label={`Nome do signatário ${i + 1}`} value={r.name} onChange={(e) => update(r.key, { name: e.target.value })} />{cell(i, 'name')}</td>
                    <td><input aria-label={`CPF do signatário ${i + 1}`} inputMode="numeric" value={r.document} onChange={(e) => update(r.key, { document: e.target.value })} />{cell(i, 'document')}</td>
                    <td><input aria-label={`E-mail do signatário ${i + 1}`} type="email" value={r.email} onChange={(e) => update(r.key, { email: e.target.value })} />{cell(i, 'email')}</td>
                    <td><input aria-label={`Telefone do signatário ${i + 1}`} type="tel" value={r.phone} placeholder="+5511999990000" onChange={(e) => update(r.key, { phone: e.target.value })} />{cell(i, 'phone')}</td>
                    <td><input aria-label={`Ordem do signatário ${i + 1}`} className="narrow" inputMode="numeric" value={r.order} onChange={(e) => update(r.key, { order: e.target.value.replace(/\D/g, '') })} />{cell(i, 'order')}</td>
                    <td>
                      <button type="button" className="secondary" disabled={rows.length === 1} onClick={() => setRows((rs) => rs.filter((x) => x.key !== r.key))}
                        aria-label={`Remover signatário ${i + 1}`}>Remover</button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <p className="hint">Sem ordem, todos assinam em paralelo. Com ordem (1, 2, 3…), cada grupo assina depois do anterior; todos precisam ter ordem.</p>
          <button type="button" className="secondary" onClick={() => setRows((rs) => [...rs, emptyRow()])}>Adicionar signatário</button>
        </div>

        <div>
          <button type="submit" disabled={busy}>{busy ? 'Enviando…' : 'Iniciar processo'}</button>
        </div>
      </form>
    </section>
  );
}
