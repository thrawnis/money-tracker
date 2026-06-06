import { useEffect, useState, useCallback, useRef } from 'react';
import { useParams } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { getAccount } from '../api/accounts';
import { getTransactions, createTransaction, updateTransaction, deleteTransaction } from '../api/transactions';
import { getUpcoming } from '../api/scheduledTransactions';
import { getCategories } from '../api/categories';
import type { Account, Transaction, Category, ScheduledTransaction } from '../types';
import TransactionForm from '../components/TransactionForm';
import styles from './AccountRegister.module.css';

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

const PAST_PAGE_SIZE = 50;
const FUTURE_BATCH = 5;
// How far ahead (in days) to look for upcoming bills
const FUTURE_DAYS = 365;

export default function AccountRegister() {
  const { id } = useParams<{ id: string }>();
  const accountId = Number(id);

  const [account, setAccount] = useState<Account | null>(null);
  usePageTitle('Account Register');
  const [categories, setCategories] = useState<Category[]>([]);
  const [error, setError] = useState('');
  const [initialLoading, setInitialLoading] = useState(true);

  // Past transactions — loaded in pages, accumulated
  const [pastTxs, setPastTxs] = useState<Transaction[]>([]);
  const [pastTotal, setPastTotal] = useState(0);
  const [pastPage, setPastPage] = useState(1);
  const [loadingPast, setLoadingPast] = useState(false);

  // Future scheduled transactions
  const [showFuture, setShowFuture] = useState(false);
  const [futureBills, setFutureBills] = useState<ScheduledTransaction[]>([]);
  const [futureSkip, setFutureSkip] = useState(0);
  const [futureTotal, setFutureTotal] = useState(0);
  const [loadingFuture, setLoadingFuture] = useState(false);

  // Filters
  const [filterOpen, setFilterOpen] = useState(false);
  const [filterFrom, setFilterFrom] = useState('');
  const [filterTo, setFilterTo] = useState('');
  const [filterMinAmount, setFilterMinAmount] = useState('');
  const [filterMaxAmount, setFilterMaxAmount] = useState('');
  const [filterPayee, setFilterPayee] = useState('');
  const [filterCategoryId, setFilterCategoryId] = useState('');
  const [filterMemo, setFilterMemo] = useState('');
  const [filterUncategorized, setFilterUncategorized] = useState(false);

  // Form
  const [showForm, setShowForm] = useState(false);
  const [editingTx, setEditingTx] = useState<Transaction | null>(null);

  // Infinite scroll sentinels
  const topSentinelRef = useRef<HTMLDivElement>(null);
  const bottomSentinelRef = useRef<HTMLDivElement>(null);

  // Keyboard shortcut: N = new transaction
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement).tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      if (e.key === 'n' || e.key === 'N') {
        e.preventDefault();
        setEditingTx(null);
        setShowForm(true);
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, []);

  const hasMorePast = pastTxs.length < pastTotal;
  const hasMoreFuture = futureSkip + FUTURE_BATCH < futureTotal;

  // ── Initial load ───────────────────────────────────────────────────────────

  const loadInitial = useCallback(async () => {
    if (!accountId) return;
    setInitialLoading(true);
    setError('');
    try {
      const [acc, txResult, cats] = await Promise.all([
        getAccount(accountId),
        getTransactions(accountId, { page: 1, pageSize: PAST_PAGE_SIZE }),
        getCategories(),
      ]);
      setAccount(acc);
      setPastTxs(txResult.items);
      setPastTotal(txResult.total);
      setPastPage(1);
      setCategories(cats);
    } catch {
      setError('Failed to load account data.');
    } finally {
      setInitialLoading(false);
    }
  }, [accountId]);

  useEffect(() => { loadInitial(); }, [loadInitial]);

  // ── Load more past (scroll up) ─────────────────────────────────────────────

  const loadMorePast = useCallback(async () => {
    if (loadingPast || !hasMorePast) return;
    setLoadingPast(true);
    try {
      const nextPage = pastPage + 1;
      const result = await getTransactions(accountId, {
        from: filterFrom || undefined,
        to: filterTo || undefined,
        minAmount: filterMinAmount ? Number(filterMinAmount) : undefined,
        maxAmount: filterMaxAmount ? Number(filterMaxAmount) : undefined,
        payeeName: filterPayee || undefined,
        categoryId: filterCategoryId ? Number(filterCategoryId) : undefined,
        memo: filterMemo || undefined,
        uncategorizedOnly: filterUncategorized || undefined,
        page: nextPage,
        pageSize: PAST_PAGE_SIZE,
      });
      setPastTxs(prev => [...prev, ...result.items]);
      setPastPage(nextPage);
    } catch {
      // silently ignore pagination errors
    } finally {
      setLoadingPast(false);
    }
  }, [loadingPast, hasMorePast, pastPage, accountId, filterFrom, filterTo, filterMinAmount, filterMaxAmount, filterPayee, filterCategoryId, filterMemo, filterUncategorized]);

  // ── Load future bills ──────────────────────────────────────────────────────

  const loadFutureBills = useCallback(async (skip = 0, replace = false) => {
    if (loadingFuture) return;
    setLoadingFuture(true);
    try {
      // Fetch one extra to know if there are more
      const batch = await getUpcoming({
        accountId,
        days: FUTURE_DAYS,
        limit: FUTURE_BATCH + 1,
        skip,
      });
      const hasMore = batch.length > FUTURE_BATCH;
      const items = hasMore ? batch.slice(0, FUTURE_BATCH) : batch;
      setFutureBills(prev => replace ? items : [...prev, ...items]);
      setFutureSkip(skip + items.length);
      // Update total estimate: if we got a full batch + more indicator, mark as having more
      setFutureTotal(skip + items.length + (hasMore ? 1 : 0));
    } catch {
      // silently ignore
    } finally {
      setLoadingFuture(false);
    }
  }, [loadingFuture, accountId]);

  useEffect(() => {
    if (showFuture && futureBills.length === 0) {
      loadFutureBills(0, true);
    }
  }, [showFuture]); // eslint-disable-line react-hooks/exhaustive-deps

  // ── Infinite scroll via IntersectionObserver ───────────────────────────────

  useEffect(() => {
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        if (entry.target === topSentinelRef.current && hasMorePast) {
          loadMorePast();
        }
        if (entry.target === bottomSentinelRef.current && showFuture && hasMoreFuture) {
          loadFutureBills(futureSkip);
        }
      }
    }, { threshold: 0.1 });

    if (topSentinelRef.current) observer.observe(topSentinelRef.current);
    if (bottomSentinelRef.current) observer.observe(bottomSentinelRef.current);
    return () => observer.disconnect();
  }, [hasMorePast, loadMorePast, showFuture, hasMoreFuture, loadFutureBills, futureSkip]);

  // ── Filters ────────────────────────────────────────────────────────────────

  const applyFilters = useCallback(async () => {
    setInitialLoading(true);
    try {
      const result = await getTransactions(accountId, {
        from: filterFrom || undefined,
        to: filterTo || undefined,
        minAmount: filterMinAmount ? Number(filterMinAmount) : undefined,
        maxAmount: filterMaxAmount ? Number(filterMaxAmount) : undefined,
        payeeName: filterPayee || undefined,
        categoryId: filterCategoryId ? Number(filterCategoryId) : undefined,
        memo: filterMemo || undefined,
        uncategorizedOnly: filterUncategorized || undefined,
        page: 1,
        pageSize: PAST_PAGE_SIZE,
      });
      setPastTxs(result.items);
      setPastTotal(result.total);
      setPastPage(1);
    } catch {
      setError('Failed to apply filters.');
    } finally {
      setInitialLoading(false);
    }
  }, [accountId, filterFrom, filterTo, filterMinAmount, filterMaxAmount, filterPayee, filterCategoryId, filterMemo, filterUncategorized]);

  const clearFilters = () => {
    setFilterFrom(''); setFilterTo(''); setFilterMinAmount(''); setFilterMaxAmount('');
    setFilterPayee(''); setFilterCategoryId(''); setFilterMemo(''); setFilterUncategorized(false);
  };

  // ── CRUD ───────────────────────────────────────────────────────────────────

  const handleSaveTx = async (data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt'>) => {
    if (editingTx) {
      await updateTransaction(accountId, editingTx.id, data);
    } else {
      await createTransaction(accountId, data);
    }
    setShowForm(false);
    setEditingTx(null);
    loadInitial();
  };

  const handleDelete = async (txId: number) => {
    if (!confirm('Delete this transaction?')) return;
    await deleteTransaction(accountId, txId);
    loadInitial();
  };

  const handleEdit = (tx: Transaction) => {
    setEditingTx(tx);
    setShowForm(true);
  };

  // ── Running balance ────────────────────────────────────────────────────────

  const openingBalance = account?.openingBalance ?? 0;
  // Transactions come back newest-first; compute balance from the full set
  const balanceMap = new Map<number, number>();
  {
    // Oldest first for running balance computation
    const sorted = [...pastTxs].reverse();
    let running = openingBalance;
    for (const tx of sorted) {
      running += tx.amount;
      balanceMap.set(tx.id, running);
    }
  }
  const currentBalance = balanceMap.get(pastTxs[0]?.id) ?? openingBalance;

  const allCategories: { id: number; label: string }[] = [];
  for (const cat of categories) {
    allCategories.push({ id: cat.id, label: cat.name });
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        allCategories.push({ id: sub.id, label: `${cat.name} : ${sub.name}` });
      }
    }
  }

  const today = new Date().toISOString().slice(0, 10);

  if (initialLoading && pastTxs.length === 0) return (
    <div className={styles.page}><p className={styles.loadingMsg}>Loading…</p></div>
  );
  if (error) return <div className={styles.page}><p className={styles.errorMsg}>{error}</p></div>;

  return (
    <div className={styles.page}>
      {/* ── Header ──────────────────────────────────────────────────────────── */}
      <div className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{account?.name ?? 'Account Register'}</h2>
          {account && (
            <div className={styles.accountMeta}>
              {account.type} &bull; Balance: <strong>{formatCurrency(currentBalance)}</strong>
            </div>
          )}
        </div>
        <div className={styles.headerActions}>
          <button
            className={`${styles.btnToggleFuture} ${showFuture ? styles.btnToggleFutureActive : ''}`}
            onClick={() => setShowFuture(s => !s)}
            title="Show upcoming scheduled transactions"
          >
            {showFuture ? 'Hide Upcoming' : 'Show Upcoming'}
          </button>
          <button className={styles.btnPrimary} onClick={() => { setEditingTx(null); setShowForm(s => !s); }}>
            {showForm && !editingTx ? 'Cancel' : '+ New Transaction'}
          </button>
          <button className={styles.btnSecondary} onClick={() => setFilterOpen(o => !o)}>
            {filterOpen ? 'Hide Filters' : 'Filters'}
          </button>
        </div>
      </div>

      {/* ── Filter panel ────────────────────────────────────────────────────── */}
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
              <label>Min Amount</label>
              <input type="number" step="0.01" value={filterMinAmount} onChange={e => setFilterMinAmount(e.target.value)} />
            </div>
            <div className={styles.filterField}>
              <label>Max Amount</label>
              <input type="number" step="0.01" value={filterMaxAmount} onChange={e => setFilterMaxAmount(e.target.value)} />
            </div>
            <div className={styles.filterField}>
              <label>Payee</label>
              <input type="text" value={filterPayee} onChange={e => setFilterPayee(e.target.value)} placeholder="* wildcard" />
            </div>
            <div className={styles.filterField}>
              <label>Category</label>
              <select value={filterCategoryId} onChange={e => setFilterCategoryId(e.target.value)}>
                <option value="">All</option>
                {allCategories.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
              </select>
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
            <button className={styles.btnSecondary} onClick={() => { clearFilters(); applyFilters(); }}>Clear</button>
          </div>
        </div>
      )}

      {/* ── Transaction entry form ───────────────────────────────────────────── */}
      {showForm && (
        <TransactionForm
          accountId={accountId}
          initial={editingTx ?? undefined}
          onSave={handleSaveTx}
          onCancel={() => { setShowForm(false); setEditingTx(null); }}
        />
      )}

      {/* ── Register table ──────────────────────────────────────────────────── */}
      <div className={styles.tableWrapper}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Date</th>
              <th>Payee</th>
              <th>Category</th>
              <th>Memo</th>
              <th className={styles.right}>Amount</th>
              <th className={styles.right}>Balance</th>
              <th>Status</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {/* ── Load-more-past sentinel (top) ────────────────────────────── */}
            {hasMorePast && (
              <tr>
                <td colSpan={8} className={styles.sentinelCell}>
                  <div ref={topSentinelRef} className={styles.sentinel} />
                  {loadingPast && <span className={styles.loadingMore}>Loading older transactions…</span>}
                </td>
              </tr>
            )}

            {/* ── Past / current transactions (newest first) ───────────────── */}
            {pastTxs.length === 0 && !initialLoading ? (
              <tr>
                <td colSpan={8} className={styles.emptyMsg}>No transactions found.</td>
              </tr>
            ) : (
              pastTxs.map(tx => (
                <tr key={tx.id} className={styles.txRow}>
                  <td>{formatDate(tx.date)}</td>
                  <td>{tx.payee?.name ?? '—'}</td>
                  <td>{getCategoryLabel(tx, categories)}</td>
                  <td>{tx.memo ?? ''}</td>
                  <td className={`${styles.right} ${tx.amount < 0 ? styles.debit : styles.credit}`}>
                    {formatCurrency(tx.amount)}
                  </td>
                  <td className={`${styles.right} ${(balanceMap.get(tx.id) ?? 0) < 0 ? styles.debit : ''}`}>
                    {formatCurrency(balanceMap.get(tx.id) ?? 0)}
                  </td>
                  <td><span className={styles[`status${tx.status}`]}>{tx.status}</span></td>
                  <td className={styles.actions}>
                    <button className={styles.btnEdit} onClick={() => handleEdit(tx)}>Edit</button>
                    <button className={styles.btnDelete} onClick={() => handleDelete(tx.id)}>Del</button>
                  </td>
                </tr>
              ))
            )}

            {/* ── Today divider ────────────────────────────────────────────── */}
            <tr className={styles.todayRow}>
              <td colSpan={8}>
                <div className={styles.todayDivider}>
                  <span className={styles.todayLabel}>Today — {formatDate(today)}</span>
                </div>
              </td>
            </tr>

            {/* ── Future scheduled transactions ────────────────────────────── */}
            {showFuture && (
              <>
                {loadingFuture && futureBills.length === 0 ? (
                  <tr>
                    <td colSpan={8} className={styles.loadingMore}>Loading upcoming…</td>
                  </tr>
                ) : futureBills.length === 0 ? (
                  <tr>
                    <td colSpan={8} className={styles.noUpcoming}>No upcoming scheduled transactions.</td>
                  </tr>
                ) : (
                  futureBills.map(bill => (
                    <tr key={`sched-${bill.id}`} className={styles.futureTxRow}>
                      <td>{formatDate(bill.nextDueDate)}</td>
                      <td>{bill.payee?.name ?? bill.name}</td>
                      <td>{bill.category?.name ?? ''}</td>
                      <td>{bill.memo ?? ''}</td>
                      <td className={`${styles.right} ${bill.amount < 0 ? styles.debit : styles.credit}`}>
                        {formatCurrency(bill.amount)}
                      </td>
                      <td className={styles.right}>—</td>
                      <td><span className={styles.statusScheduled}>Scheduled</span></td>
                      <td />
                    </tr>
                  ))
                )}

                {/* Load-more-future sentinel (bottom) */}
                {hasMoreFuture && (
                  <tr>
                    <td colSpan={8} className={styles.sentinelCell}>
                      <div ref={bottomSentinelRef} className={styles.sentinel} />
                      {loadingFuture && <span className={styles.loadingMore}>Loading more upcoming…</span>}
                    </td>
                  </tr>
                )}
              </>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function getCategoryLabel(tx: Transaction, categories: Category[]): string {
  if (!tx.categoryId) return '';
  for (const cat of categories) {
    if (cat.id === tx.categoryId) return cat.name;
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        if (sub.id === tx.categoryId) return `${cat.name}:${sub.name}`;
      }
    }
  }
  return tx.category?.name ?? '';
}
