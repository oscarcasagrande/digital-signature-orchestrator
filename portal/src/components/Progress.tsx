import type { Progress, Step, StepStatus } from '../api/types';

export const STEP_STATUS_LABEL: Record<StepStatus, string> = {
  PENDING: 'Pendente', IN_PROGRESS: 'Em andamento', COMPLETED: 'Concluída', FAILED: 'Falhou', CANCELLED: 'Cancelada',
};

const STEP_TONE: Record<StepStatus, string> = {
  PENDING: 'neutral', IN_PROGRESS: 'info', COMPLETED: 'good', FAILED: 'bad', CANCELLED: 'muted',
};

export function StepBadge({ status }: { status: StepStatus }) {
  return <span className={'badge badge-' + STEP_TONE[status]}>{STEP_STATUS_LABEL[status]}</span>;
}

/** Text shown when hovering the progress of a process: the step currently in progress, or why there is none. */
export function currentStepText(p: Progress): string {
  if (p.currentStep) return `Etapa atual: ${p.currentStep.label}`;
  if (p.completedSteps >= p.totalSteps) return 'Todas as etapas concluídas';
  return 'Nenhuma etapa em andamento';
}

/** "X/Y" with a progress bar; the current step is the tooltip (title) and the accessible description. */
export function ProgressCell({ progress }: { progress: Progress }) {
  const { completedSteps: done, totalSteps: total } = progress;
  const pct = total > 0 ? Math.round((done / total) * 100) : 0;
  const text = currentStepText(progress);
  return (
    <div className="progress-cell" title={text}>
      <span className="progress-count">{done}/{total}</span>
      <div className="progress-bar" role="progressbar" aria-label="Progresso das etapas" aria-valuemin={0} aria-valuemax={total}
        aria-valuenow={done} aria-valuetext={`${done} de ${total} etapas. ${text}`}>
        <div className="progress-fill" style={{ width: pct + '%' }} />
      </div>
    </div>
  );
}

/** Steps grouped for the detail page: process-level steps first and last, signer steps in between. */
export function groupSteps(steps: Step[]): Array<{ key: string; signerId: string | null; steps: Step[] }> {
  const groups: Array<{ key: string; signerId: string | null; steps: Step[] }> = [];
  const document = steps.filter((s) => s.kind === 'DOCUMENT_RECEIVED');
  if (document.length) groups.push({ key: 'start', signerId: null, steps: document });
  const order: string[] = [];
  for (const s of steps) if (s.signerId && !order.includes(s.signerId)) order.push(s.signerId);
  for (const id of order) groups.push({ key: id, signerId: id, steps: steps.filter((s) => s.signerId === id) });
  const closing = steps.filter((s) => s.kind === 'FINAL_DOCUMENT' || s.kind === 'CALLBACK');
  if (closing.length) groups.push({ key: 'end', signerId: null, steps: closing });
  return groups;
}
