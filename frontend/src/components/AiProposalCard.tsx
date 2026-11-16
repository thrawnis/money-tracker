import { useState } from 'react';
import { applyAiProposal, dismissAiProposal, type AiProposal } from '../api/ai';
import { confirmDialog } from '../utils/dialogs';
import styles from './AiProposalCard.module.css';

type ApiError = { response?: { data?: { message?: string } } };
const ICON: Record<AiProposal['kind'], string> = {
  set_category: '🏷️', link_transfers: '🔗', rename_payee: '✏️', merge_payees: '🔀', mapping_rule: '🧭', create_bill: '📅',
};
const SHOWN = 8;

/**
 * A change the AI assistant proposed, under its answer. Nothing happens until
 * Apply (rows can be unticked first); the server re-checks everything then.
 */
export default function AiProposalCard({ proposal, onChange }: { proposal: AiProposal; onChange: (p: AiProposal) => void }) {
  const [excluded, setExcluded] = useState<Set<string>>(new Set());
  const [showAll, setShowAll] = useState(false);
  const [busy, setBusy] = useState<'apply' | 'dismiss' | null>(null);
  const [error, setError] = useState('');
  const p = proposal;
  const pending = p.status === 'pending';
  const items = p.items ?? [];
  const chosen = items.length - excluded.size;

  const toggle = (key: string) => setExcluded(s => { const n = new Set(s); if (n.has(key)) n.delete(key); else n.add(key); return n; });

  const apply = async () => {
    if (p.kind === 'merge_payees' && !(await confirmDialog(`${p.title}?\n\n${p.lines.join('\n')}`, { title: 'Merge payees', confirmLabel: 'Merge', danger: true }))) return;
    setBusy('apply'); setError('');
    try { onChange(await applyAiProposal(p.id, [...excluded])); }
    catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Couldn\'t apply this change.'); }
    finally { setBusy(null); }
  };
  const dismiss = async () => {
    setBusy('dismiss'); setError('');
    try { onChange(await dismissAiProposal(p.id)); }
    catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Couldn\'t dismiss it.'); }
    finally { setBusy(null); }
  };

  return (
    <div className={`${styles.card} ${!pending ? styles.resolved : ''}`} role="group" aria-label={`Suggested change: ${p.title}`}>
      <div className={styles.head}>
        <span className={styles.icon} aria-hidden="true">{ICON[p.kind] ?? '•'}</span>
        <div className={styles.titleBox}>
          <span className={styles.kicker}>Suggested change</span>
          <span className={styles.title}>{p.title}</span>
        </div>
        {p.status === 'applied' && <span className={`${styles.badge} ${styles.applied}`}>✓ Applied</span>}
        {p.status === 'dismissed' && <span className={`${styles.badge} ${styles.dismissed}`}>Dismissed</span>}
      </div>
      {p.lines.map(l => <p key={l} className={styles.line}>{l}</p>)}
      {items.length > 0 && (
        <ul className={styles.items}>
          {(showAll ? items : items.slice(0, SHOWN)).map(i => (
            <li key={i.key}>
              <label className={styles.item}>
                {pending && <input type="checkbox" checked={!excluded.has(i.key)} onChange={() => toggle(i.key)} />}
                <span>{i.label}</span>
              </label>
            </li>
          ))}
          {items.length > SHOWN && (
            <li><button className={styles.more} onClick={() => setShowAll(s => !s)}>
              {showAll ? 'Show fewer' : `Show all ${items.length}`}
            </button></li>
          )}
        </ul>
      )}
      {p.result && <p className={styles.result}>{p.result}</p>}
      {error && <p className={styles.error} role="alert">{error}</p>}
      {pending && (
        <div className={styles.actions}>
          <button className={styles.apply} onClick={apply} disabled={busy !== null || (items.length > 0 && chosen === 0)}>
            {busy === 'apply' ? 'Applying…' : items.length > 0 ? `Apply to ${chosen}` : 'Apply'}
          </button>
          <button className={styles.dismiss} onClick={dismiss} disabled={busy !== null}>
            {busy === 'dismiss' ? 'Dismissing…' : 'Dismiss'}
          </button>
        </div>
      )}
    </div>
  );
}
