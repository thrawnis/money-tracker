import { useEffect, useState, useRef, useCallback } from 'react';
import { useSearchParams } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { getAccounts } from '../api/accounts';
import { searchTransactions, type SearchTransaction } from '../api/transactionSearch';
import type { Account } from '../types';
import TransactionDetailPanel from '../components/TransactionDetailPanel';
import styles from './AllTransactions.module.css';

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}
function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

const PAGE_SIZE = 100;

export default function AllTransactions() {
  usePageTitle('Transactions');
  const [searchParams, setSearchParams] = useSearchParams();

  const [selectedTx, setSelectedTx] = useState<SearchTransaction | null>(null);
  // Dirty state reported up from the detail panel's embedded edit form, so
  // clicking a different row can confirm before discarding unsaved edits.
  const [panelDirty, setPanelDirty] = useState(false);

  const [accounts, setAccounts] = useState<Account[]>([]);
  const [selectedIds, setSelectedIds] = useState<Set<number> | null>(null); // null = all
  const [acctPickerOpen, setAcctPickerOpen] = useState(false);
  const pickerRef = useRef<HTMLDivElement>(null);

  const [items, setItems] = useState<SearchTransaction[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  // Filter state
  const [filterFrom, setFilterFrom] = useState('');
  const [filterTo, setFilterTo] = useState('');
  const [filterPayee, setFilterPayee] = useState('');
  const [filterMemo, setFilterMemo] = useState('');
  // Seeded from ?uncategorized=true (the Dashboard's "Uncategorized
  // Transactions" heading links here) so the filter is already applied and
  // visibly checked on arrival, not just active behind the scenes.
  const [filterUncategorized, setFilterUncategorized] = useState(() => searchParams.get('uncategorized') === 'true');
  const [filterOpen, setFilterOpen] = useState(() => searchParams.get('uncategorized') === 'true');

  // Applied filters (only change when user clicks Apply)
  const [appliedFrom, setAppliedFrom] = useState('');
  const [appliedTo, setAppliedTo] = useState('');
  const [appliedPayee, setAppliedPayee] = useState('');
  const [appliedMemo, setAppliedMemo] = useState('');
  const [appliedUncategorized, setAppliedUncategorized] = useState(() => searchParams.get('uncategorized') === 'true');

  // One-shot: consume ?uncategorized= from the URL, then strip it so it
  // doesn't linger and reapply itself after the user clears the filter.
  useEffect(() => {
    if (!searchParams.has('uncategorized')) return;
    setSearchParams(prev => { const p = new URLSearchParams(prev); p.delete('uncategorized'); return p; }, { replace: true });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    getAccounts().then(accs => {
      setAccounts(accs);
    }).catch(() => setError('Failed to load accounts.'));
  }, []);

  // Close picker on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (pickerRef.current && !pickerRef.current.contains(e.target as Node)) {
        setAcctPickerOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  const effectiveIds = selectedIds ? Array.from(selectedIds) : accounts.map(a => a.id);
  // Stable primitive for the dep list below — a fresh array every render would
  // re-fire the loader constantly, and the React Compiler rejects a
  // JSON.stringify() call written inline in the dependency array.
  const effectiveIdsKey = effectiveIds.join(',');

  // Sequence guard instead of an `if (loading) return` early-out: the stale
  // `loading` closure used to silently skip reloads triggered mid-flight,
  // leaving the cleared list empty. Now the latest request always wins.
  const loadSeq = useRef(0);

  const loadPage = useCallback(async (pg: number, replace: boolean) => {
    const seq = ++loadSeq.current;
    // "Select none" means an empty result by definition — don't send the backend
    // an empty accountIds array and depend on how it happens to interpret it.
    if (effectiveIds.length === 0 && accounts.length > 0) {
      setItems([]);
      setTotal(0);
      setPage(1);
      setLoading(false);
      return;
    }
    setLoading(true);
    try {
      const result = await searchTransactions({
        accountIds: effectiveIds.length < accounts.length ? effectiveIds : undefined,
        from: appliedFrom || undefined,
        to: appliedTo || undefined,
        payeeName: appliedPayee || undefined,
        memo: appliedMemo || undefined,
        uncategorized: appliedUncategorized || undefined,
        page: pg,
        pageSize: PAGE_SIZE,
      });
      if (seq !== loadSeq.current) return;
      setItems(prev => replace ? result.items : [...prev, ...result.items]);
      setTotal(result.total);
      setPage(pg);
    } catch {
      if (seq === loadSeq.current) setError('Failed to load transactions.');
    } finally {
      if (seq === loadSeq.current) setLoading(false);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [effectiveIdsKey, appliedFrom, appliedTo, appliedPayee, appliedMemo, appliedUncategorized, accounts.length]);

  // Reload from page 1 when accounts selection or applied filters change
  useEffect(() => {
    if (accounts.length === 0) return;
    setItems([]);
    setPage(1);
    loadPage(1, true);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loadPage]);

  const applyFilters = () => {
    setAppliedFrom(filterFrom);
    setAppliedTo(filterTo);
    setAppliedPayee(filterPayee);
    setAppliedMemo(filterMemo);
    setAppliedUncategorized(filterUncategorized);
  };

  const clearFilters = () => {
    setFilterFrom(''); setFilterTo(''); setFilterPayee(''); setFilterMemo(''); setFilterUncategorized(false);
    setAppliedFrom(''); setAppliedTo(''); setAppliedPayee(''); setAppliedMemo(''); setAppliedUncategorized(false);
  };

  const toggleAccount = (id: number) => {
    setSelectedIds(prev => {
      const current = prev ?? new Set(accounts.map(a => a.id));
      const next = new Set(current);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      // If all are selected, revert to null (= all)
      if (next.size === accounts.length) return null;
      return next;
    });
  };

  const selectAll = () => setSelectedIds(null);
  const selectNone = () => setSelectedIds(new Set());

  const isAllSelected = selectedIds === null || selectedIds.size === accounts.length;
  const selectedCount = selectedIds ? selectedIds.size : accounts.length;

  const hasMore = items.length < total;
  const activeCount = accounts.filter(a => a.isActive).length;

  return (
    <div className={styles.page}>
      {/* ── Header ── */}
      <div className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>Transactions</h2>
          {total > 0 && (
            <div className={styles.meta}>
              {total.toLocaleString()} transaction{total !== 1 ? 's' : ''}
              {!isAllSelected && ` · ${selectedCount} account${selectedCount !== 1 ? 's' : ''}`}
            </div>
          )}
        </div>
        <div className={styles.headerActions}>
          {/* Account multiselect */}
          <div className={styles.acctPickerWrap} ref={pickerRef}>
            <button
              className={`${styles.btnSecondary} ${acctPickerOpen ? styles.btnSecondaryActive : ''} ${!isAllSelected ? styles.btnFiltered : ''}`}
              onClick={() => setAcctPickerOpen(o => !o)}
              title="Filter by account"
            >
              {isAllSelected ? 'All Accounts' : `${selectedCount} Account${selectedCount !== 1 ? 's' : ''}`}
              <span className={styles.chevron}>{acctPickerOpen ? '▲' : '▼'}</span>
            </button>
            {acctPickerOpen && (
              <div className={styles.acctPicker}>
                <div className={styles.acctPickerActions}>
                  <button className={styles.acctPickerLink} onClick={selectAll}>All</button>
                  <span className={styles.acctPickerSep}>·</span>
                  <button className={styles.acctPickerLink} onClick={selectNone}>None</button>
                  {activeCount < accounts.length && (
                    <>
                      <span className={styles.acctPickerSep}>·</span>
                      <button
                        className={styles.acctPickerLink}
                        onClick={() => setSelectedIds(new Set(accounts.filter(a => a.isActive).map(a => a.id)))}
                      >Active only</button>
                    </>
                  )}
                </div>
                <div className={styles.acctList}>
                  {accounts.map(a => {
                    const checked = selectedIds === null || selectedIds.has(a.id);
                    return (
                      <label
                        key={a.id}
                        className={`${styles.acctItem} ${!a.isActive ? styles.acctItemInactive : ''}`}
                      >
                        <input
                          type="checkbox"
                          checked={checked}
                          onChange={() => toggleAccount(a.id)}
                        />
                        <span className={styles.acctItemName}>{a.name}</span>
                        {!a.isActive && <span className={styles.acctItemBadge}>inactive</span>}
                      </label>
                    );
                  })}
                </div>
              </div>
            )}
          </div>

          <button
            className={`${styles.btnSecondary} ${filterOpen ? styles.btnSecondaryActive : ''}`}
            onClick={() => setFilterOpen(o => !o)}
          >
            {filterOpen ? 'Hide Filters' : 'Filters'}
          </button>
        </div>
      </div>

      {/* ── Filter panel ── */}
      {filterOpen && (
        <div className={styles.filterPanel}>
          <div className={styles.filterRow}>
            <div className={styles.filterField}>
              <label>From</label>
              <input type="date" value={filterFrom} onChange={e => setFilterFrom(e.target.value)} />
            </div>
            <div className={styles.filterField}>
              <label>To</label>
              <input type="date" value={filterTo} onChange={e => setFilterTo(e.target.value)} />
            </div>
            <div className={styles.filterField}>
              <label>Payee</label>
              <input type="text" value={filterPayee} onChange={e => setFilterPayee(e.target.value)} placeholder="* wildcard" />
            </div>
            <div className={styles.filterField}>
              <label>Memo</label>
              <input type="text" value={filterMemo} onChange={e => setFilterMemo(e.target.value)} placeholder="* wildcard" />
            </div>
            <div className={styles.filterField} style={{ justifyContent: 'flex-end' }}>
              <label>
                <input type="checkbox" checked={filterUncategorized} onChange={e => setFilterUncategorized(e.target.checked)} />
                {' '}Uncategorized only
              </label>
            </div>
          </div>
          <div className={styles.filterActions}>
            <button className={styles.btnPrimary} onClick={applyFilters}>Apply</button>
            <button className={styles.btnSecondary} onClick={clearFilters}>Clear</button>
          </div>
        </div>
      )}

      {error && <div className={styles.errorMsg}>{error}</div>}

      {/* ── Table ── */}
      <div className={styles.tableWrapper}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Date</th>
              <th>Account</th>
              <th>Payee</th>
              <th>Category</th>
              <th>Memo</th>
              <th className={styles.right}>Amount</th>
              <th className={styles.center}>Status</th>
            </tr>
          </thead>
          <tbody>
            {items.map(tx => (
              <tr
                key={tx.id}
                className={styles.txRow}
                onClick={() => {
                  if (selectedTx?.id === tx.id) return;
                  if (panelDirty && !confirm('You have unsaved changes to this transaction. Discard them?')) return;
                  setPanelDirty(false);
                  setSelectedTx(tx);
                }}
                title="View transaction details"
              >
                <td className={styles.noWrap}>{formatDate(tx.date)}</td>
                <td className={styles.accountCell}>{tx.accountName}</td>
                <td>{tx.payee ?? '—'}</td>
                <td>{tx.splitCount ? `Split (${tx.splitCount})` : tx.category ?? ''}</td>
                <td className={styles.memoCell}>{tx.memo ?? ''}</td>
                <td className={`${styles.right} ${styles.noWrap} ${tx.amount < 0 ? styles.debit : styles.credit}`}>
                  {formatCurrency(tx.amount)}
                </td>
                <td className={styles.center}>
                  <span className={styles[`status${tx.status as 'Uncleared' | 'Cleared' | 'Reconciled'}`]}>
                    {tx.status === 'Uncleared' ? '○' : tx.status === 'Cleared' ? '✓' : '✓✓'}
                  </span>
                </td>
              </tr>
            ))}
            {items.length === 0 && !loading && (
              <tr>
                <td colSpan={7} className={styles.empty}>
                  {selectedCount === 0 ? 'No accounts selected.' : 'No transactions found.'}
                </td>
              </tr>
            )}
            {loading && items.length === 0 && (
              <tr><td colSpan={7} className={styles.empty}>Loading…</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {hasMore && (
        <button
          className={styles.loadMore}
          onClick={() => loadPage(page + 1, false)}
          disabled={loading}
        >
          {loading ? 'Loading…' : `Load more (${(total - items.length).toLocaleString()} remaining)`}
        </button>
      )}

      {selectedTx && (
        <TransactionDetailPanel
          accountId={selectedTx.accountId}
          transactionId={selectedTx.id}
          accountName={selectedTx.accountName}
          accounts={accounts}
          onClose={() => { setSelectedTx(null); setPanelDirty(false); }}
          onChanged={() => loadPage(1, true)}
          onDirtyChange={setPanelDirty}
        />
      )}
    </div>
  );
}
