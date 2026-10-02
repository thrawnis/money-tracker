import { useMemo, useRef, useState } from 'react';
import type { Payee } from '../types';
import styles from './TransactionForm.module.css';

/** How one raw payee name from an import file is resolved. */
export type PayeeChoice =
  | { mode: 'file'; remember: boolean }                       // use the file's name as-is
  | { mode: 'existing'; payeeId: number; remember: boolean }  // map to an existing payee
  | { mode: 'new'; name: string; remember: boolean };         // create a payee with a custom name

interface Props {
  rawText: string;
  payees: Payee[];
  /** Likely matches for the raw text, shown before anything is typed. */
  suggestions: { id: number; name: string }[];
  value: PayeeChoice;
  onChange: (choice: PayeeChoice) => void;
}

type Option =
  | { kind: 'file' }
  | { kind: 'existing'; payee: { id: number; name: string } }
  | { kind: 'new'; name: string };

/**
 * Payee picker for the import review: type to search every payee (like the
 * transaction form's payee box), or create a new payee with a custom name,
 * or keep the name from the file.
 */
export default function PayeeResolveInput({ rawText, payees, suggestions, value, onChange }: Props) {
  const shown = value.mode === 'existing'
    ? payees.find(p => p.id === value.payeeId)?.name ?? ''
    : value.mode === 'new' ? value.name : '';
  // null: not editing — the box shows the current choice.
  const [draft, setDraft] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [highlight, setHighlight] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const text = draft ?? shown;

  const options = useMemo<Option[]>(() => {
    const q = (draft ?? '').trim();
    const opts: Option[] = [{ kind: 'file' }];
    if (!q) {
      for (const s of suggestions) opts.push({ kind: 'existing', payee: s });
      return opts;
    }
    const matches = payees.filter(p => p.name.toLowerCase().includes(q.toLowerCase())).slice(0, 8);
    for (const p of matches) opts.push({ kind: 'existing', payee: p });
    if (!payees.some(p => p.name.toLowerCase() === q.toLowerCase())) opts.push({ kind: 'new', name: q });
    return opts;
  }, [draft, payees, suggestions]);

  const choose = (o: Option) => {
    const remember = value.remember;
    if (o.kind === 'file') onChange({ mode: 'file', remember });
    else if (o.kind === 'existing') onChange({ mode: 'existing', payeeId: o.payee.id, remember });
    else onChange({ mode: 'new', name: o.name, remember });
    setDraft(null);
    setOpen(false);
  };

  // Leaving the box commits what was typed: nothing -> keep the file's name,
  // an existing payee's exact name -> that payee, anything else -> new payee.
  const commitTyped = () => {
    if (draft == null) return;
    const q = draft.trim();
    const exact = payees.find(p => p.name.toLowerCase() === q.toLowerCase());
    if (!q) choose({ kind: 'file' });
    else if (exact) choose({ kind: 'existing', payee: exact });
    else choose({ kind: 'new', name: q });
  };

  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setHighlight(h => (h + 1) % options.length); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); setOpen(true); setHighlight(h => (h - 1 + options.length) % options.length); }
    else if (e.key === 'Enter') { e.preventDefault(); if (open && options[highlight]) choose(options[highlight]); else commitTyped(); }
    else if (e.key === 'Escape') { setDraft(null); setOpen(false); }
  };

  const optionLabel = (o: Option) =>
    o.kind === 'file' ? <>Don't map — use “{rawText}” from the file</>
      : o.kind === 'existing' ? o.payee.name
        : <>Create new payee “{o.name}”</>;

  return (
    <div className={styles.fieldRelative} style={{ minWidth: 260 }}>
      <input
        ref={inputRef}
        type="text"
        className={styles.input}
        value={text}
        placeholder={`Use “${rawText}” from the file — or type to search`}
        aria-label={`Payee for ${rawText}`}
        onChange={e => {
          setDraft(e.target.value);
          setOpen(true);
          // While typing, Enter picks the first match (or "create new"), like
          // the transaction form; with the box cleared it's "use the file's name".
          setHighlight(e.target.value.trim() ? 1 : 0);
        }}
        onFocus={() => { setOpen(true); setHighlight(0); }}
        onBlur={() => setTimeout(() => { commitTyped(); setOpen(false); }, 150)}
        onKeyDown={onKeyDown}
        autoComplete="off"
      />
      {open && (
        <ul className={styles.suggestions} style={{ zIndex: 5 }}>
          {options.map((o, i) => (
            <li
              key={o.kind === 'existing' ? `p${o.payee.id}` : o.kind}
              onMouseDown={() => choose(o)}
              className={i === highlight ? `${styles.suggestion} ${styles.suggestionActive}` : styles.suggestion}
              style={o.kind !== 'existing' ? { fontStyle: 'italic' } : undefined}
            >
              {optionLabel(o)}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
