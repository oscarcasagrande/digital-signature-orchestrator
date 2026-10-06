import { useRef, type KeyboardEvent, type ReactNode } from 'react';

export interface TabDef {
  key: string;
  label: string;
}

interface Props {
  tabs: TabDef[];
  active: string;
  onChange: (key: string) => void;
  children: ReactNode;
}

/** Accessible tab strip (roles, aria-selected, arrow-key navigation). */
export function Tabs({ tabs, active, onChange, children }: Props) {
  const refs = useRef<Record<string, HTMLButtonElement | null>>({});

  const onKey = (e: KeyboardEvent, index: number) => {
    let next = -1;
    if (e.key === 'ArrowRight') next = (index + 1) % tabs.length;
    else if (e.key === 'ArrowLeft') next = (index - 1 + tabs.length) % tabs.length;
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = tabs.length - 1;
    if (next >= 0) {
      e.preventDefault();
      onChange(tabs[next].key);
      refs.current[tabs[next].key]?.focus();
    }
  };

  return (
    <div className="tabs">
      <div role="tablist" aria-label="Process sections" className="tablist">
        {tabs.map((t, i) => (
          <button
            key={t.key}
            ref={(el) => { refs.current[t.key] = el; }}
            role="tab"
            id={'tab-' + t.key}
            aria-selected={active === t.key}
            aria-controls={'panel-' + t.key}
            tabIndex={active === t.key ? 0 : -1}
            className={active === t.key ? 'tab tab-active' : 'tab'}
            onClick={() => onChange(t.key)}
            onKeyDown={(e) => onKey(e, i)}
            type="button"
          >
            {t.label}
          </button>
        ))}
      </div>
      <div role="tabpanel" id={'panel-' + active} aria-labelledby={'tab-' + active} className="tabpanel">
        {children}
      </div>
    </div>
  );
}
