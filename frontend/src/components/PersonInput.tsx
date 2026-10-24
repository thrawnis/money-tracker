import { useMemo, useState } from 'react';
import styles from './TransactionForm.module.css';

export type PersonValue = { personId?: number; newName?: string };

interface Props {
  people: { id: number; name: string }[];
  value: PersonValue;
  onChange: (v: PersonValue) => void;
  placeholder?: string;
  autoFocus?: boolean;
  ariaLabel?: string;
}

/**
 * Person picker for IOUs: type to search everyone you've recorded, or keep
 * a new name to add that person when the IOU is saved. Keyboard: arrows and
 * Enter pick, Escape closes.
 */
export default function PersonInput({ people, value, onChange, placeholder, autoFocus, ariaLabel }: Props) {
  const shown = value.personId != null ? people.find(p => p.id === value.personId)?.name ?? '' : value.newName ?? '';
  const [text, setText] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [highlight, setHighlight] = useState(0);
  const current = text ?? shown;

  const options = useMemo(() => {
    const q = (text ?? '').trim().toLowerCase();
    const matches = people.filter(p => !q || p.name.toLowerCase().includes(q)).slice(0, 8);
    const exact = people.some(p => p.name.toLowerCase() === q);
    return { matches, offerNew: q.length > 0 && !exact };
  }, [people, text]);
  const count = options.matches.length + (options.offerNew ? 1 : 0);

  const pick = (i: number) => {
    if (i < options.matches.length) onChange({ personId: options.matches[i].id });
    else if (options.offerNew) onChange({ newName: (text ?? '').trim() });
    setText(null);
    setOpen(false);
  };

  // Leaving the box keeps what was typed: an existing name selects that
  // person, anything else becomes a new person.
  const commit = () => {
    if (text == null) return;
    const t = text.trim();
    const exact = people.find(p => p.name.toLowerCase() === t.toLowerCase());
    onChange(!t ? {} : exact ? { personId: exact.id } : { newName: t });
    setText(null);
  };

  return (
    <div className={styles.fieldRelative}>
      <input
        type="text"
        className={styles.input}
        value={current}
        placeholder={placeholder ?? 'Name'}
        aria-label={ariaLabel ?? 'Person'}
        autoFocus={autoFocus}
        autoComplete="off"
        onChange={e => { setText(e.target.value); setOpen(true); setHighlight(0); }}
        onFocus={() => { setOpen(true); setHighlight(0); }}
        onBlur={() => setTimeout(() => { commit(); setOpen(false); }, 150)}
        onKeyDown={e => {
          if (e.key === 'ArrowDown') { e.preventDefault(); setOpen(true); setHighlight(h => (h + 1) % Math.max(count, 1)); }
          else if (e.key === 'ArrowUp') { e.preventDefault(); setHighlight(h => (h - 1 + count) % Math.max(count, 1)); }
          else if (e.key === 'Enter' && open && count > 0) { e.preventDefault(); pick(highlight); }
          else if (e.key === 'Escape') { setText(null); setOpen(false); }
        }}
      />
      {open && count > 0 && (
        <ul className={styles.suggestions} style={{ zIndex: 5 }}>
          {options.matches.map((p, i) => (
            <li key={p.id} onMouseDown={() => pick(i)}
              className={i === highlight ? `${styles.suggestion} ${styles.suggestionActive}` : styles.suggestion}>
              {p.name}
            </li>
          ))}
          {options.offerNew && (
            <li onMouseDown={() => pick(options.matches.length)} style={{ fontStyle: 'italic' }}
              className={highlight === options.matches.length ? `${styles.suggestion} ${styles.suggestionActive}` : styles.suggestion}>
              Add new person “{(text ?? '').trim()}”
            </li>
          )}
        </ul>
      )}
    </div>
  );
}
