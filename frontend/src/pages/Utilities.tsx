import { useState, useEffect } from 'react';
import { Link } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { getDuplicates, ignoreDuplicateGroup, unignoreDuplicateGroup, type DuplicateGroup, type DuplicateTransaction } from '../api/duplicates';
import { deleteTransaction } from '../api/transactions';
import { getInstitutions, updateInstitution, deleteInstitution } from '../api/institutions';
import type { Institution } from '../types';
// Reuses Settings' styling for visual consistency between the two tabbed
// utility-page shells rather than duplicating the same CSS.
import styles from './Settings.module.css';

// ── Find Duplicates ──

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

function DuplicatesTab() {
  const [groups, setGroups] = useState<DuplicateGroup[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [deletingId, setDeletingId] = useState<number | null>(null);
  const [includeMemo, setIncludeMemo] = useState(false);
  const [includeCategory, setIncludeCategory] = useState(false);
  const [showIgnored, setShowIgnored] = useState(false);
  const [togglingKey, setTogglingKey] = useState('');

  const load = () => {
    setLoading(true);
    setError('');
    getDuplicates({ includeMemo, includeCategory, showIgnored })
      .then(setGroups)
      .catch(() => setError('Failed to load duplicate transactions.'))
      .finally(() => setLoading(false));
  };

  useEffect(load, [includeMemo, includeCategory, showIgnored]); // eslint-disable-line react-hooks/exhaustive-deps

  const handleDelete = async (accountId: number, id: number) => {
    if (!confirm('Delete this transaction? This cannot be undone.')) return;
    setDeletingId(id);
    try {
      await deleteTransaction(accountId, id);
      // Re-fetch rather than patch in place: deleting one row out of a group of
      // two collapses it below the ">1 transactions" threshold and should
      // disappear entirely, not linger as a single-row "duplicate".
      load();
    } catch {
      setError('Failed to delete transaction.');
    } finally {
      setDeletingId(null);
    }
  };

  const groupKeyOf = (g: DuplicateGroup) => `${g.accountId}-${g.date}-${g.amount}`;

  const handleIgnore = async (g: DuplicateGroup) => {
    const key = groupKeyOf(g);
    setTogglingKey(key);
    try {
      await ignoreDuplicateGroup({ accountId: g.accountId, date: g.date, amount: g.amount });
      load();
    } catch {
      setError('Failed to ignore this group.');
    } finally {
      setTogglingKey('');
    }
  };

  const handleUnignore = async (g: DuplicateGroup) => {
    const key = groupKeyOf(g);
    setTogglingKey(key);
    try {
      await unignoreDuplicateGroup({ accountId: g.accountId, date: g.date, amount: g.amount });
      load();
    } catch {
      setError('Failed to un-ignore this group.');
    } finally {
      setTogglingKey('');
    }
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Transactions in the same account, on the same date, for the same amount are grouped below — a common
        symptom of a file that got imported twice, or genuinely repeated transactions. Review each group and
        delete anything that shouldn't be there; transfers are excluded since linked transfer legs naturally
        share a date and amount. If a group isn't actually a duplicate, you can ignore it — it stays hidden as
        long as nothing about it changes, and reappears automatically if a member transaction is edited or
        another transaction joins the group.
      </p>

      <div style={{ display: 'flex', gap: 20, marginBottom: 14, flexWrap: 'wrap' }}>
        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          <input type="checkbox" checked={includeMemo} onChange={e => setIncludeMemo(e.target.checked)} />
          Also match memo
        </label>
        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          <input type="checkbox" checked={includeCategory} onChange={e => setIncludeCategory(e.target.checked)} />
          Also match category
        </label>
        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
          <input type="checkbox" checked={showIgnored} onChange={e => setShowIgnored(e.target.checked)} />
          Show ignored groups
        </label>
      </div>

      {loading && <p className={styles.hint}>Loading…</p>}
      {error && <div className={styles.error}>{error}</div>}

      {!loading && !error && groups.length === 0 && (
        <p className={styles.hint}>No likely duplicates found.</p>
      )}

      {groups.map((g, i) => {
        // A field only helps distinguish these rows if the transactions in
        // this group actually disagree on it — highlight just those cells.
        const fields: Array<keyof DuplicateTransaction> = ['payee', 'category', 'memo', 'checkNumber', 'status'];
        const varies = Object.fromEntries(
          fields.map(f => [f, new Set(g.transactions.map(t => t[f] ?? '')).size > 1])
        ) as Record<keyof DuplicateTransaction, boolean>;
        const key = groupKeyOf(g);

        return (
          <table
            className={`${styles.dupTable} ${g.ignored ? styles.dupTableIgnored : ''}`}
            key={`${key}-${i}`}
            style={{ marginBottom: 16 }}
          >
            <thead>
              <tr>
                <th colSpan={5}>
                  {g.accountName} — {formatDate(g.date)} — {formatCurrency(g.amount)}
                  {g.ignored && <span className={styles.dupIgnoredBadge}>Ignored</span>}
                </th>
                <th>
                  {g.ignored ? (
                    <button
                      className={styles.btnSecondary}
                      onClick={() => handleUnignore(g)}
                      disabled={togglingKey === key}
                    >
                      {togglingKey === key ? 'Un-ignoring…' : 'Un-ignore'}
                    </button>
                  ) : (
                    <button
                      className={styles.btnSecondary}
                      onClick={() => handleIgnore(g)}
                      disabled={togglingKey === key}
                    >
                      {togglingKey === key ? 'Ignoring…' : 'Ignore'}
                    </button>
                  )}
                </th>
              </tr>
              <tr>
                <th>Payee</th>
                <th>Category</th>
                <th>Memo</th>
                <th>Check #</th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {g.transactions.map(t => (
                <tr key={t.id}>
                  <td className={varies.payee ? styles.dupDiffCell : undefined}>
                    {t.payee ?? <span className={styles.hint}>—</span>}
                  </td>
                  <td className={varies.category ? styles.dupDiffCell : undefined}>
                    {t.category ?? <span className={styles.hint}>—</span>}
                  </td>
                  <td className={varies.memo ? styles.dupDiffCell : undefined}>{t.memo ?? ''}</td>
                  <td className={varies.checkNumber ? styles.dupDiffCell : undefined}>{t.checkNumber ?? ''}</td>
                  <td className={varies.status ? styles.dupDiffCell : undefined}>{t.status}</td>
                  <td style={{ display: 'flex', gap: 8 }}>
                    <Link className={styles.btnSecondary} to={`/accounts/${g.accountId}?tx=${t.id}`}>
                      View
                    </Link>
                    <button
                      className={styles.btnDanger}
                      onClick={() => handleDelete(g.accountId, t.id)}
                      disabled={deletingId === t.id}
                    >
                      {deletingId === t.id ? 'Deleting…' : 'Delete'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        );
      })}
    </div>
  );
}

// ── Institutions ──

function InstitutionsTab() {
  const [institutions, setInstitutions] = useState<Institution[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [renamingId, setRenamingId] = useState<number | null>(null);
  const [renameValue, setRenameValue] = useState('');

  const load = () => {
    setLoading(true);
    setError('');
    getInstitutions()
      .then(list => setInstitutions([...list].sort((a, b) => a.name.localeCompare(b.name))))
      .catch(() => setError('Failed to load institutions.'))
      .finally(() => setLoading(false));
  };

  useEffect(load, []);

  const handleRename = async (id: number) => {
    if (!renameValue.trim()) return;
    const inst = institutions.find(i => i.id === id);
    if (!confirm(`Rename "${inst?.name}" to "${renameValue.trim()}"?`)) return;
    try {
      await updateInstitution(id, renameValue.trim());
      setRenamingId(null);
      load();
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to rename institution.');
    }
  };

  const handleDelete = async (id: number, name: string) => {
    if (!confirm(`Delete "${name}"? This cannot be undone.`)) return;
    try {
      await deleteInstitution(id);
      setInstitutions(prev => prev.filter(i => i.id !== id));
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to delete institution. It may still be assigned to an account.');
    }
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Banks and institutions available when creating or editing an account. Rename or delete one here; deleting
        is blocked while it's still assigned to any account.
      </p>

      {loading && <p className={styles.hint}>Loading…</p>}
      {error && <div className={styles.error}>{error}</div>}

      {!loading && !error && institutions.length === 0 && (
        <p className={styles.hint}>No institutions yet — add one from the account creation form.</p>
      )}

      {institutions.length > 0 && (
        <table className={styles.dupTable}>
          <thead>
            <tr>
              <th>Name</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {institutions.map(inst => (
              <tr key={inst.id}>
                <td>
                  {renamingId === inst.id ? (
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <input
                        value={renameValue}
                        onChange={e => setRenameValue(e.target.value)}
                        onKeyDown={e => { if (e.key === 'Enter') handleRename(inst.id); if (e.key === 'Escape') setRenamingId(null); }}
                        autoFocus
                      />
                      <button className={styles.btnSecondary} onClick={() => handleRename(inst.id)}>Save</button>
                      <button className={styles.btnSecondary} onClick={() => setRenamingId(null)}>Cancel</button>
                    </div>
                  ) : (
                    inst.name
                  )}
                </td>
                <td>
                  {renamingId !== inst.id && (
                    <div style={{ display: 'flex', gap: 8 }}>
                      <button
                        className={styles.btnSecondary}
                        onClick={() => { setRenamingId(inst.id); setRenameValue(inst.name); }}
                      >
                        Rename
                      </button>
                      <button className={styles.btnDanger} onClick={() => handleDelete(inst.id, inst.name)}>Delete</button>
                    </div>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}

// ── Page shell ──

type Tab = 'duplicates' | 'institutions';

export default function Utilities() {
  usePageTitle('Utilities');
  const [tab, setTab] = useState<Tab>('duplicates');

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Utilities</h2>
      </div>
      <div className={styles.tabs}>
        <button
          className={`${styles.tab} ${tab === 'duplicates' ? styles.tabActive : ''}`}
          onClick={() => setTab('duplicates')}
        >
          Find Duplicates
        </button>
        <button
          className={`${styles.tab} ${tab === 'institutions' ? styles.tabActive : ''}`}
          onClick={() => setTab('institutions')}
        >
          Institutions
        </button>
      </div>
      <div className={styles.tabContent}>
        {tab === 'duplicates' && <DuplicatesTab />}
        {tab === 'institutions' && <InstitutionsTab />}
      </div>
    </div>
  );
}
