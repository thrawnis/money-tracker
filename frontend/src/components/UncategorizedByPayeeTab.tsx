import { useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { getUncategorizedByPayee, assignUncategorized, type UncategorizedGroup } from '../api/uncategorized';
import { getCategories } from '../api/categories';
import CategoryAutocomplete from './CategoryAutocomplete';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { flattenCategories } from '../utils/categories';
import type { Category } from '../types';
import styles from '../pages/Settings.module.css';

type ApiError = { response?: { data?: { message?: string } } };

const money = (n: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
const day = (d: string) => new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
type AssignPayload = Omit<Parameters<typeof assignUncategorized>[0], 'categoryId'>;

const groupKey = (g: UncategorizedGroup) => (g.payeeId === null ? 'none' : String(g.payeeId));
const groupName = (g: UncategorizedGroup) => g.payeeName ?? '(No payee)';

/**
 * Utilities → Uncategorized by Payee: every uncategorized transaction, grouped
 * by payee (A→Z by payee name, no payee last). Assign a category to one transaction,
 * to all of a payee's, or to several payees at once (tick them). Assigning a
 * whole payee can also make the category its default for future imports.
 */
export default function UncategorizedByPayeeTab() {
  const [groups, setGroups] = useState<UncategorizedGroup[] | null>(null);
  const [categories, setCategories] = useState<Category[]>([]);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [filter, setFilter] = useState('');
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [selected, setSelected] = useState<Set<string>>(new Set());
  // Category text per input: 'bulk', 'g:<payee key>' or 't:<transaction id>'.
  const [text, setText] = useState<Record<string, string>>({});
  // "Also make it the payee's default", per group and for the bulk bar; on unless unticked.
  const [noDefault, setNoDefault] = useState<Set<string>>(new Set());
  // The action in progress (same keys as text); held until the list has reloaded.
  const [busy, setBusy] = useState<string | null>(null);

  const load = async () => {
    const d = await getUncategorizedByPayee();
    setGroups(d.groups);
    // Drop selections/expansions for payees that are no longer listed.
    const keys = new Set(d.groups.map(groupKey));
    setSelected(prev => new Set([...prev].filter(k => keys.has(k))));
  };

  useEffect(() => {
    getCategories().then(setCategories).catch(() => setError('Failed to load categories.'));
    getUncategorizedByPayee()
      .then(d => setGroups(d.groups))
      .catch(() => setError('Failed to load uncategorized transactions.'));
  }, []);

  const typed = Object.values(text).some(v => v.trim() !== '');
  useUnsavedChanges(typed && busy === null);

  const flat = useMemo(() => flattenCategories(categories), [categories]);
  const resolve = (label: string) => flat.find(c => c.label.toLowerCase() === label.trim().toLowerCase());

  // A→Z by payee name, ignoring case, numbers in natural order; "(No payee)" last.
  const visible = (groups ?? [])
    .filter(g => groupName(g).toLowerCase().includes(filter.trim().toLowerCase()))
    .sort((a, b) => Number(a.payeeName === null) - Number(b.payeeName === null)
      || (a.payeeName ?? '').localeCompare(b.payeeName ?? '', undefined, { sensitivity: 'base', numeric: true }));
  const selectedGroups = (groups ?? []).filter(g => selected.has(groupKey(g)));
  const selectedCount = selectedGroups.reduce((n, g) => n + g.count, 0);

  const assign = async (key: string, payload: AssignPayload, what: string) => {
    const match = resolve(text[key] ?? '');
    if (!match) {
      setError((text[key] ?? '').trim() ? `No category called "${text[key].trim()}". Pick one from the suggestions.` : 'Choose a category first.');
      return;
    }
    setBusy(key); setError(''); setNotice('');
    try {
      const r = await assignUncategorized({ ...payload, categoryId: match.id });
      await load();
      setText(prev => ({ ...prev, [key]: '' }));
      setNotice(`Set "${match.label}" on ${r.updated} transaction${r.updated === 1 ? '' : 's'}${what}`
        + (r.defaultsSet ? ` and made it the default for ${r.defaultsSet} payee${r.defaultsSet === 1 ? '' : 's'}.` : '.'));
    } catch (err) {
      setError((err as ApiError)?.response?.data?.message ?? 'Failed to set the category.');
    } finally {
      setBusy(null);
    }
  };

  const assignGroups = (key: string, gs: UncategorizedGroup[]) => {
    const payeeIds = gs.filter(g => g.payeeId !== null).map(g => g.payeeId!);
    return assign(key, {
      payeeIds,
      includeNoPayee: gs.some(g => g.payeeId === null),
      setPayeeDefault: payeeIds.length > 0 && !noDefault.has(key),
    }, gs.length > 1 ? ` across ${gs.length} payees` : gs[0].payeeId === null ? ' with no payee' : ` from ${groupName(gs[0])}`);
  };

  const applyBulk = () => {
    if (selectedGroups.length > 1 && !confirm(
      `Set "${resolve(text.bulk ?? '')?.label ?? text.bulk}" on all ${selectedCount} uncategorized transactions from `
      + `${selectedGroups.length} payees (${selectedGroups.map(groupName).join(', ')})?`)) return;
    void assignGroups('bulk', selectedGroups);
  };

  const toggle = (set: Set<string>, k: string, setter: (s: Set<string>) => void) => {
    const next = new Set(set);
    if (next.has(k)) next.delete(k); else next.add(k);
    setter(next);
  };

  // Fixed width so the Apply buttons line up from one payee to the next.
  const categoryBox = (key: string, placeholder = 'Category or Category: Subcategory') => (
    <div style={{ width: 280, maxWidth: '100%' }}>
      <CategoryAutocomplete
        value={text[key] ?? ''}
        onChange={v => { setText(prev => ({ ...prev, [key]: v })); setError(''); }}
        categories={categories}
        placeholder={placeholder}
        className={styles.input}
      />
    </div>
  );

  const defaultBox = (key: string, label: string) => (
    <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
      <input type="checkbox" checked={!noDefault.has(key)} onChange={() => toggle(noDefault, key, setNoDefault)} />
      {label}
    </label>
  );

  if (!groups) {
    return <div className={styles.tabSection}>{error ? <div className={styles.error}>{error}</div> : <p className={styles.hint}>Loading…</p>}</div>;
  }

  const total = groups.reduce((n, g) => n + g.count, 0);

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Every uncategorized transaction (not counting transfers, split or voided transactions), grouped by payee —
        A→Z by payee name. Set a category on one transaction, on all of a payee's, or tick several payees to
        do them together. Assigning a whole payee can also make the category that payee's default, so its future
        imports and bank syncs arrive categorized.
      </p>
      {error && <div className={styles.error}>{error}</div>}
      {notice && <p className={styles.hint} style={{ color: '#1a7a40' }}>{notice}</p>}

      {total === 0 ? (
        <p className={styles.hint}>Nothing to categorize — every transaction has a category. 🎉</p>
      ) : (
        <>
          <div style={{ display: 'flex', gap: 12, alignItems: 'center', flexWrap: 'wrap', margin: '4px 0 12px' }}>
            <strong>{total} uncategorized transaction{total === 1 ? '' : 's'} from {groups.length} payee{groups.length === 1 ? '' : 's'}</strong>
            <input className={styles.input} placeholder="Filter payees…" value={filter} onChange={e => setFilter(e.target.value)}
              aria-label="Filter payees" style={{ width: 200 }} />
          </div>

          {selectedGroups.length > 0 && (
            <div className={styles.prefGroup} style={{ position: 'sticky', top: 0, zIndex: 2, marginBottom: 12 }}>
              <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
                <strong>{selectedGroups.length} payee{selectedGroups.length === 1 ? '' : 's'} selected ({selectedCount} transaction{selectedCount === 1 ? '' : 's'})</strong>
                {categoryBox('bulk')}
                <button className={styles.btnPrimary} onClick={applyBulk} disabled={busy !== null}>
                  {busy === 'bulk' ? 'Applying…' : `Apply to ${selectedCount}`}
                </button>
                <button className={styles.btnSecondary} onClick={() => setSelected(new Set())} disabled={busy !== null}>Clear selection</button>
              </div>
              {selectedGroups.some(g => g.payeeId !== null) && (
                <div style={{ marginTop: 6 }}>{defaultBox('bulk', "Also make it these payees' default category")}</div>
              )}
            </div>
          )}

          {visible.length === 0 && <p className={styles.hint}>No payees match “{filter}”.</p>}
          {visible.map(g => {
            const k = groupKey(g);
            const open = expanded.has(k);
            return (
              <div key={k} className={styles.prefGroup} style={{ marginBottom: 8 }}>
                <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
                  <input type="checkbox" checked={selected.has(k)} onChange={() => toggle(selected, k, setSelected)}
                    aria-label={`Select ${groupName(g)}`} disabled={busy !== null} />
                  <button type="button" className={styles.btnLink} onClick={() => toggle(expanded, k, setExpanded)}
                    aria-expanded={open} style={{ fontWeight: 600, fontSize: 14 }}>
                    {open ? '▾' : '▸'} {groupName(g)}
                  </button>
                  <span className={styles.hint} style={{ margin: 0 }}>
                    {g.count} transaction{g.count === 1 ? '' : 's'} · {money(g.total)}
                  </span>
                  <span style={{ flex: 1 }} />
                  {categoryBox(`g:${k}`)}
                  <button className={styles.btnPrimary} disabled={busy !== null} style={{ minWidth: 130 }}
                    onClick={() => void assignGroups(`g:${k}`, [g])}>
                    {busy === `g:${k}` ? 'Applying…' : g.count === 1 ? 'Apply' : `Apply to all ${g.count}`}
                  </button>
                </div>
                {g.payeeId !== null && (
                  <div style={{ marginTop: 4, marginLeft: 26 }}>
                    {defaultBox(`g:${k}`, g.hasDefaultCategory ? "Also replace this payee's default category" : "Also make it this payee's default category")}
                  </div>
                )}
                {open && (
                  <table className={styles.dupTable} style={{ marginTop: 8 }}>
                    <thead>
                      <tr><th>Date</th><th>Account</th><th>Memo</th><th style={{ textAlign: 'right' }}>Amount</th><th>Category</th><th></th></tr>
                    </thead>
                    <tbody>
                      {g.transactions.map(t => (
                        <tr key={t.id}>
                          <td style={{ whiteSpace: 'nowrap' }}>{day(t.date)}</td>
                          <td><Link to={`/accounts/${t.accountId}`}>{t.accountName}</Link></td>
                          <td>{t.memo || '—'}</td>
                          <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{money(t.amount)}</td>
                          <td style={{ minWidth: 200 }}>{categoryBox(`t:${t.id}`, 'Category')}</td>
                          <td>
                            <button className={styles.btnSecondary} disabled={busy !== null}
                              onClick={() => void assign(`t:${t.id}`, { transactionIds: [t.id] }, '')}>
                              {busy === `t:${t.id}` ? 'Saving…' : 'Save'}
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            );
          })}
        </>
      )}
    </div>
  );
}
