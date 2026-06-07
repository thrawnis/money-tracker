import { useEffect, useState, useCallback, useRef } from 'react';
import { useParams } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { getAccount, getAccounts } from '../api/accounts';
import { getTransactions, createTransaction, updateTransaction, deleteTransaction, createTransfer } from '../api/transactions';
import { getUpcoming } from '../api/scheduledTransactions';
import { getCategories } from '../api/categories';
import type { Account, Transaction, Category, ScheduledTransaction } from '../types';
import TransactionForm from '../components/TransactionForm';
import ReceiptScanner from '../components/ReceiptScanner';
import type { ExtractedReceipt } from '../api/receipts';
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
  const [allAccounts, setAllAccounts] = useState<Account[]>([]);
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

  // Sort
  const [sortBy, setSortBy] = useState('date');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');

  // Form
  const [showForm, setShowForm] = useState(false);
  const [editingTx, setEditingTx] = useState<Transaction | null>(null);
  const [receiptPrefill, setReceiptPrefill] = useState<Partial<Transaction> | null>(null);
  const [receiptPayeeName, setReceiptPayeeName] = useState<string | undefined>(undefined);
  const [receiptCategoryLabel, setReceiptCategoryLabel] = useState<string | undefined>(undefined);
  const [showScanner, setShowScanner] = useState(false);
  const lastUsedDate = useRef<string>(new Date().toISOString().slice(0, 10));

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

  const loadInitial = useCallback(async (overrideSortBy = sortBy, overrideSortDir = sortDir) => {
    if (!accountId) return;
    setInitialLoading(true);
    setError('');
    try {
      const [acc, txResult, cats, accs] = await Promise.all([
        getAccount(accountId),
        getTransactions(accountId, { page: 1, pageSize: PAST_PAGE_SIZE, sortBy: overrideSortBy, sortDir: overrideSortDir }),
        getCategories(),
        getAccounts(),
      ]);
      setAccount(acc);
      setAllAccounts(accs);
      setPastTxs(txResult.items);
      setPastTotal(txResult.total);
      setPastPage(1);
      setCategories(cats);
    } catch {
      setError('Failed to load account data.');
    } finally {
      setInitialLoading(false);
    }
  }, [accountId, sortBy, sortDir]);

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
        sortBy, sortDir,
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
  }, [loadingPast, hasMorePast, pastPage, accountId, filterFrom, filterTo, filterMinAmount, filterMaxAmount, filterPayee, filterCategoryId, filterMemo, filterUncategorized, sortBy, sortDir]);

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
        sortBy, sortDir,
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
  }, [accountId, filterFrom, filterTo, filterMinAmount, filterMaxAmount, filterPayee, filterCategoryId, filterMemo, filterUncategorized, sortBy, sortDir]);

  const clearFilters = () => {
    setFilterFrom(''); setFilterTo(''); setFilterMinAmount(''); setFilterMaxAmount('');
    setFilterPayee(''); setFilterCategoryId(''); setFilterMemo(''); setFilterUncategorized(false);
  };

  const handleSort = (field: string) => {
    const newDir = sortBy === field && sortDir === 'desc' ? 'asc' : 'desc';
    setSortBy(field);
    setSortDir(newDir);
    loadInitial(field, newDir);
  };

  // ── CRUD ───────────────────────────────────────────────────────────────────

  const insertSorted = (prev: Transaction[], tx: Transaction) => {
    const effDate = (t: Transaction) => t.postDate ?? t.date;
    const idx = prev.findIndex(t =>
      effDate(t) < effDate(tx) ||
      (effDate(t) === effDate(tx) && t.createdAt <= tx.createdAt)
    );
    const next = [...prev];
    next.splice(idx === -1 ? next.length : idx, 0, tx);
    return next;
  };

  const handleSaveTx = async (data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt'> & { targetAccountId?: number; transferDestAccountId?: number }) => {
    if (data.transferDestAccountId) {
      // Create a transfer
      const result = await createTransfer({
        sourceAccountId:      accountId,
        destinationAccountId: data.transferDestAccountId,
        date:                 data.date,
        postDate:             data.postDate,
        amount:               Math.abs(data.amount),
        memo:                 data.memo,
      });
      lastUsedDate.current = data.date;
      // Only add the debit side to this account's register
      setPastTxs(prev => insertSorted(prev, result.debit));
      setPastTotal(prev => prev + 1);
    } else if (editingTx) {
      const updated = await updateTransaction(accountId, editingTx.id, data);
      if (data.targetAccountId && data.targetAccountId !== accountId) {
        setPastTxs(prev => prev.filter(t => t.id !== editingTx.id));
        setPastTotal(prev => prev - 1);
      } else {
        setPastTxs(prev => prev.map(t => t.id === editingTx.id ? updated : t));
      }
    } else {
      const created = await createTransaction(accountId, data);
      lastUsedDate.current = data.date;
      setPastTxs(prev => insertSorted(prev, created));
      setPastTotal(prev => prev + 1);
    }
    setShowForm(false);
    setEditingTx(null);
    setReceiptPrefill(null);
    setReceiptPayeeName(undefined);
    setReceiptCategoryLabel(undefined);
  };

  const handleDelete = async (txId: number) => {
    if (!confirm('Delete this transaction?')) return;
    await deleteTransaction(accountId, txId);
    setPastTxs(prev => prev.filter(t => t.id !== txId));
    setPastTotal(prev => prev - 1);
  };

  const handleEdit = (tx: Transaction) => {
    setEditingTx(tx);
    setShowForm(true);
  };

  const handleToggleStatus = async (tx: Transaction) => {
    const next: Transaction['status'] =
      tx.status === 'Uncleared' ? 'Cleared' :
      tx.status === 'Cleared'   ? 'Reconciled' : 'Uncleared';
    setPastTxs(prev => prev.map(t => t.id === tx.id ? { ...t, status: next } : t));
    await updateTransaction(accountId, tx.id, { status: next });
  };

  const handleReceiptConfirm = (data: ExtractedReceipt) => {
    setShowScanner(false);
    setEditingTx(null);
    setReceiptPrefill({
      date: data.date ?? lastUsedDate.current,
      amount: data.amount ?? undefined,
      memo: data.memo ?? undefined,
    });
    setReceiptPayeeName(data.payee ?? undefined);
    setReceiptCategoryLabel(data.suggestedCategory ?? undefined);
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
          <button className={styles.btnPrimary} onClick={() => { setEditingTx(null); setReceiptPrefill(null); setReceiptPayeeName(undefined); setReceiptCategoryLabel(undefined); setShowForm(s => !s); }}>
            {showForm && !editingTx ? 'Cancel' : '+ New Transaction'}
          </button>
          <button className={styles.btnSecondary} onClick={() => setShowScanner(true)}>
            📷 Scan Receipt
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
          accounts={allAccounts}
          initial={editingTx ?? receiptPrefill ?? { date: lastUsedDate.current }}
          initialPayeeName={editingTx ? undefined : receiptPayeeName}
          initialCategoryLabel={editingTx ? undefined : receiptCategoryLabel}
          onSave={handleSaveTx}
          onCancel={() => { setShowForm(false); setEditingTx(null); setReceiptPrefill(null); setReceiptPayeeName(undefined); setReceiptCategoryLabel(undefined); }}
        />
      )}

      {showScanner && (
        <ReceiptScanner
          onConfirm={handleReceiptConfirm}
          onCancel={() => setShowScanner(false)}
        />
      )}

      {/* ── Register table ──────────────────────────────────────────────────── */}
      <div className={styles.tableWrapper}>
        <table className={styles.table}>
          <thead>
            <tr>
              {(['date','payee','category','memo'] as const).map(col => (
                <th key={col} className={styles.sortable} onClick={() => handleSort(col)}>
                  {col.charAt(0).toUpperCase() + col.slice(1)}
                  {sortBy === col
                    ? <span className={styles.sortActive}>{sortDir === 'desc' ? ' ▼' : ' ▲'}</span>
                    : <span className={styles.sortIdle}> ⇅</span>}
                </th>
              ))}
              <th className={`${styles.right} ${styles.sortable}`} onClick={() => handleSort('amount')}>
                Amount{sortBy === 'amount'
                  ? <span className={styles.sortActive}>{sortDir === 'desc' ? ' ▼' : ' ▲'}</span>
                  : <span className={styles.sortIdle}> ⇅</span>}
              </th>
              <th className={styles.right}>Balance</th>
              <th className={styles.sortable} onClick={() => handleSort('status')}>
                Status{sortBy === 'status'
                  ? <span className={styles.sortActive}>{sortDir === 'desc' ? ' ▼' : ' ▲'}</span>
                  : <span className={styles.sortIdle}> ⇅</span>}
              </th>
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
                  <td>
                    {/* Transfer source side: always show transaction date (post date belongs to destination) */}
                    {tx.transferTransactionId && tx.amount < 0 ? formatDate(tx.date) :
                      tx.postDate ? (
                        <span title={`Post date. Transaction date: ${formatDate(tx.date)}`}>
                          {formatDate(tx.postDate)}<span className={styles.postDateMark}>*</span>
                        </span>
                      ) : formatDate(tx.date)}
                  </td>
                  <td>
                    {tx.transferTransactionId
                      ? <span className={styles.transferLabel}>
                          {tx.amount < 0
                            ? `Transfer → ${allAccounts.find(a => a.id === tx.transferAccountId)?.name ?? 'account'}`
                            : `Transfer ← ${allAccounts.find(a => a.id === tx.transferAccountId)?.name ?? 'account'}`}
                        </span>
                      : (tx.payee?.name ?? '—')}
                  </td>
                  <td>{getCategoryLabel(tx, categories)}</td>
                  <td>{tx.memo ?? ''}</td>
                  <td className={`${styles.right} ${tx.amount < 0 ? styles.debit : styles.credit}`}>
                    {formatCurrency(tx.amount)}
                  </td>
                  <td className={`${styles.right} ${(balanceMap.get(tx.id) ?? 0) < 0 ? styles.debit : ''}`}>
                    {formatCurrency(balanceMap.get(tx.id) ?? 0)}
                  </td>
                  <td>
                    <button
                      className={styles[`status${tx.status}`]}
                      onClick={() => handleToggleStatus(tx)}
                      title={tx.status === 'Uncleared' ? 'Mark Cleared' : tx.status === 'Cleared' ? 'Mark Reconciled' : 'Mark Uncleared'}
                    >
                      {tx.status === 'Uncleared' ? '○' : tx.status === 'Cleared' ? '✓' : '✓✓'}
                    </button>
                  </td>
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
