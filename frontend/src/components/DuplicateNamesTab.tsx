import { useEffect, useState } from 'react';
import {
  findNameDuplicates, mergePayees, mergeCategories, dismissNameGroup, undismissNameGroup,
  type NameDuplicates, type DuplicatePayee, type DuplicateCategory,
} from '../api/nameDuplicates';
import styles from '../pages/Settings.module.css';

type ApiError = { response?: { data?: { message?: string } } };
type Item = { id: number; name: string; transactionCount: number; detail?: string };

/**
 * One suggested group: pick the name to keep and which others to merge into it.
 * Defaults to keeping the most-used name and merging all the rest.
 */
function MergeGroup({ items, kind, dismissed, onMerge, onDismiss, onRestore }: {
  items: Item[];
  kind: 'payee' | 'category';
  dismissed: boolean;
  onMerge: (keep: Item, merge: Item[]) => Promise<void>;
  onDismiss: () => Promise<void>;
  onRestore: () => Promise<void>;
}) {
  const [keepId, setKeepId] = useState(items[0].id);
  const [selected, setSelected] = useState<Set<number>>(new Set(items.map(i => i.id)));
  // Which action is running; held until the refreshed list has loaded, so the
  // group doesn't reappear clickable in between.
  const [busy, setBusy] = useState<'merge' | 'dismiss' | 'restore' | null>(null);
  const keep = items.find(i => i.id === keepId)!;
  const merge = items.filter(i => i.id !== keepId && selected.has(i.id));

  const toggle = (id: number) => {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id); else next.add(id);
    setSelected(next);
  };

  const run = async () => {
    const what = kind === 'payee' ? 'payee' : 'category';
    if (!confirm(
      `Merge ${merge.map(m => `"${m.name}"`).join(', ')} into "${keep.name}"?\n\n`
      + `Their transactions${kind === 'category' ? ', splits, subcategories' : ''} and bills move to "${keep.name}", `
      + `and the merged ${what}${merge.length === 1 ? ' is' : 's are'} deleted. This can't be undone.`
      + (kind === 'payee' ? '\n\nThe old names become payee mapping rules, so future imports use the kept name.' : ''),
    )) return;
    setBusy('merge');
    try { await onMerge(keep, merge); } finally { setBusy(null); }
  };

  return (
    <div className={styles.prefGroup} style={{ marginBottom: 10, opacity: dismissed ? 0.7 : 1 }}>
      {dismissed && <p className={styles.hint} style={{ margin: '0 0 6px' }}>Marked “Not duplicates”.</p>}
      <table className={styles.dupTable} style={{ marginBottom: 8 }}>
        <thead>
          <tr><th style={{ width: 60 }}>Keep</th><th style={{ width: 60 }}>Merge</th><th>Name</th><th style={{ textAlign: 'right' }}>Used by</th></tr>
        </thead>
        <tbody>
          {items.map(i => (
            <tr key={i.id}>
              <td>
                <input type="radio" name={`keep-${kind}-${items[0].id}`} checked={keepId === i.id}
                  onChange={() => setKeepId(i.id)} aria-label={`Keep ${i.name}`} />
              </td>
              <td>
                <input type="checkbox" checked={i.id !== keepId && selected.has(i.id)} disabled={i.id === keepId}
                  onChange={() => toggle(i.id)} aria-label={`Merge ${i.name}`} />
              </td>
              <td>{i.name}{i.detail && <span className={styles.hint} style={{ margin: '0 0 0 6px' }}>{i.detail}</span>}</td>
              <td style={{ textAlign: 'right' }}>{i.transactionCount} transaction{i.transactionCount === 1 ? '' : 's'}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <div style={{ display: 'flex', gap: 8 }}>
        <button className={styles.btnPrimary} onClick={run} disabled={busy !== null || merge.length === 0}>
          {busy === 'merge' ? 'Merging…' : merge.length === 0 ? 'Select names to merge' : `Merge ${merge.length} into "${keep.name}"`}
        </button>
        {dismissed ? (
          <button className={styles.btnSecondary} disabled={busy !== null} aria-busy={busy === 'restore'}
            onClick={async () => { setBusy('restore'); try { await onRestore(); } finally { setBusy(null); } }}>
            {busy === 'restore' ? 'Restoring…' : 'Suggest again'}
          </button>
        ) : (
          <button className={styles.btnSecondary} disabled={busy !== null} aria-busy={busy === 'dismiss'}
            onClick={async () => { setBusy('dismiss'); try { await onDismiss(); } finally { setBusy(null); } }}>
            {busy === 'dismiss' ? 'Hiding…' : 'Not duplicates'}
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * Utilities → Duplicate Names: likely-duplicate payees and categories (store
 * numbers, punctuation, plurals, typos — exact duplicates are merged
 * automatically at sign-in), merged on the user's say-so.
 */
export default function DuplicateNamesTab() {
  const [data, setData] = useState<NameDuplicates | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [showDismissed, setShowDismissed] = useState(false);

  // Re-fetches the list; actions await it so their busy state lasts until the
  // updated list is on screen.
  const refresh = async () => {
    try { setData(await findNameDuplicates(showDismissed)); }
    catch { setError('Failed to look for duplicate names.'); }
  };

  useEffect(() => {
    let cancelled = false;
    findNameDuplicates(showDismissed)
      .then(d => { if (!cancelled) setData(d); })
      .catch(() => { if (!cancelled) setError('Failed to look for duplicate names.'); });
    return () => { cancelled = true; };
  }, [showDismissed]);

  const key = (kind: string, ids: number[]) => `${kind}:${[...ids].sort((a, b) => a - b).join(',')}`;

  const setDismissed = (kind: 'payee' | 'category', ids: number[], dismiss: boolean) => async () => {
    setError(''); setNotice('');
    try {
      if (dismiss) await dismissNameGroup(kind, ids); else await undismissNameGroup(kind, ids);
      await refresh();
    } catch {
      setError('Failed to update the group.');
    }
  };

  const doMerge = (kind: 'payee' | 'category') => async (keep: Item, merge: Item[]) => {
    setError(''); setNotice('');
    try {
      const ids = merge.map(m => m.id);
      if (kind === 'payee') await mergePayees(keep.id, ids); else await mergeCategories(keep.id, ids);
      setNotice(`Merged ${merge.map(m => `"${m.name}"`).join(', ')} into "${keep.name}".`);
      await refresh();
    } catch (err) {
      setError((err as ApiError)?.response?.data?.message ?? 'Merge failed.');
    }
  };

  const payeeItems = (g: DuplicatePayee[]): Item[] =>
    g.map(p => ({ id: p.id, name: p.name, transactionCount: p.transactionCount, detail: p.hasDefaultCategory ? 'has a default category' : undefined }));
  const categoryItems = (g: DuplicateCategory[]): Item[] =>
    g.map(c => ({
      id: c.id,
      name: c.parentName ? `${c.parentName}: ${c.name}` : c.name,
      transactionCount: c.transactionCount,
      detail: c.subcategoryCount > 0 ? `${c.subcategoryCount} subcategor${c.subcategoryCount === 1 ? 'y' : 'ies'}` : undefined,
    }));

  const payeeGroups = data?.payees ?? [];
  const categoryGroups = data?.categories ?? [];

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Payees and categories whose names are probably the same thing — differing by store numbers, punctuation,
        "Inc"/".com", plurals, spacing or a typo (e.g. "Starbucks #1234" and "Starbucks"). Names that match exactly
        apart from capitalization are already merged automatically. Choose the name to keep and which to merge
        into it; categories are only compared with others under the same parent. A group marked “Not duplicates”
        stays hidden unless its names change (a new similar name appears, or one is merged or deleted).
      </p>
      <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
        <input type="checkbox" checked={showDismissed} onChange={e => setShowDismissed(e.target.checked)} />
        Show groups marked “Not duplicates”
      </label>
      {error && <div className={styles.error}>{error}</div>}
      {notice && <p className={styles.hint} style={{ color: '#1a7a40' }}>{notice}</p>}
      {!data && !error && <p className={styles.hint}>Looking for duplicates…</p>}

      {data && (
        <>
          <h3 style={{ fontSize: 15, margin: '16px 0 8px' }}>Payees ({payeeGroups.length})</h3>
          {payeeGroups.length === 0 && <p className={styles.hint}>No likely duplicate payees.</p>}
          {payeeGroups.map(g => {
            const ids = g.items.map(i => i.id);
            return (
              <MergeGroup key={key('p', ids)} items={payeeItems(g.items)} kind="payee" dismissed={g.dismissed}
                onMerge={doMerge('payee')} onDismiss={setDismissed('payee', ids, true)} onRestore={setDismissed('payee', ids, false)} />
            );
          })}

          <h3 style={{ fontSize: 15, margin: '20px 0 8px' }}>Categories ({categoryGroups.length})</h3>
          {categoryGroups.length === 0 && <p className={styles.hint}>No likely duplicate categories.</p>}
          {categoryGroups.map(g => {
            const ids = g.items.map(i => i.id);
            return (
              <MergeGroup key={key('c', ids)} items={categoryItems(g.items)} kind="category" dismissed={g.dismissed}
                onMerge={doMerge('category')} onDismiss={setDismissed('category', ids, true)} onRestore={setDismissed('category', ids, false)} />
            );
          })}
        </>
      )}
    </div>
  );
}
