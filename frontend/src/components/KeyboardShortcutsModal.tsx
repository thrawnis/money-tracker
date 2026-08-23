import { useEffect } from 'react';
import styles from './KeyboardShortcutsModal.module.css';

interface Props {
  onClose: () => void;
}

interface Shortcut {
  keys: string[];
  desc: string;
  scope?: string;
}

interface Group {
  title: string;
  shortcuts: Shortcut[];
}

const GROUPS: Group[] = [
  {
    title: 'Global',
    shortcuts: [
      { keys: ['?'], desc: 'Show this shortcuts list' },
      { keys: ['Esc'], desc: 'Close a dialog, menu, or dropdown' },
    ],
  },
  {
    title: 'Account Register',
    shortcuts: [
      { keys: ['N'], desc: 'Add a new transaction', scope: 'Register page' },
    ],
  },
  {
    title: 'Payee / Category fields',
    shortcuts: [
      { keys: ['↑', '↓'], desc: 'Move through suggestions' },
      { keys: ['Enter'], desc: 'Select the highlighted suggestion' },
      { keys: ['Esc'], desc: 'Close the suggestions list' },
    ],
  },
  {
    title: 'Amount field',
    shortcuts: [
      { keys: ['Enter'], desc: 'Evaluate a formula, e.g. (33.40*17)/14' },
      { keys: ['Enter'], desc: 'Submit the form and round to 2 decimals', scope: 'once the field already shows a plain number' },
    ],
  },
];

export default function KeyboardShortcutsModal({ onClose }: Props) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [onClose]);

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={styles.modal}>
        <div className={styles.modalHeader}>
          <h3 className={styles.modalTitle}>Keyboard Shortcuts</h3>
          <button className={styles.closeBtn} onClick={onClose} aria-label="Close">x</button>
        </div>
        <div className={styles.modalBody}>
          {GROUPS.map(group => (
            <div key={group.title}>
              <div className={styles.groupTitle}>{group.title}</div>
              {group.shortcuts.map((s, i) => (
                <div key={i} className={styles.row}>
                  <div className={styles.rowDesc}>
                    {s.desc}
                    {s.scope && <span className={styles.rowScope}>{s.scope}</span>}
                  </div>
                  <div className={styles.keys}>
                    {s.keys.map(k => <span key={k} className={styles.key}>{k}</span>)}
                  </div>
                </div>
              ))}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
