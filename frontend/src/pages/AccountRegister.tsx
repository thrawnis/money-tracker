import { useEffect, useState, useCallback, useRef } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { getAccount, getAccounts, deleteAccount } from '../api/accounts';
import { getTransactions, createTransaction, updateTransaction, deleteTransaction, createTransfer } from '../api/transactions';
import { getUpcoming } from '../api/scheduledTransactions';
import { getCategories } from '../api/categories';
import { getInstitutions } from '../api/institutions';
import type { Account, Transaction, Category, ScheduledTransaction, Institution } from '../types';
import TransactionForm from '../components/TransactionForm';
import ReceiptScanner from '../components/ReceiptScanner';
import AccountEditModal from '../components/AccountEditModal';
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

interface TxFilters {
  from?: string;
  to?: string;
  minAmount?: number;
  maxAmount?: number;
  payeeName?: string;
  categoryId?: number;
  memo?: string;
  uncategorizedOnly?: boolean;
}

export default function AccountRegister() {
  const { id } = useParams<{ id: string }>();
  const accountId = Number(id);
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();

  const [account, setAccount] = useState<Account | null>(null);
  const [allAccounts, setAllAccounts] = useState<Account[]>([]);
  const [institutions, setInstitutions] = useState<Institution[]>([]);
  const [showEditModal, setShowEditModal] = useState(false);
  usePageTitle('Account Register');
  const [categories, setCategories] = useState<Category[]>([]);
  const [error, setError] = useState('');
  const [initialLoading, setInitialLoading] = useState(true);

  // Past transactions — loaded in pages, accumulated
  const [pastTxs, setPastTxs] = useState<Transaction[]>([]);
  const [pastTotal, setPastTotal] = useState(0);
  const [pastPage, setPastPage] = useState(1);
  const [loadingPast, setLoadingPast] = useState(false);
  // Server-computed: opening balance + all transactions (independent of paging/filters)
  const [accountBalance, setAccountBalance] = useState(0);
  // Guards against a slow response for a previous account overwriting newer data
  const requestSeq = useRef(0);

  // Future scheduled transactions
  const [showFuture, setShowFuture] = useState(true);
  const [futureDays, setFutureDays] = useState(14);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const settingsRef = useRef<HTMLDivElement>(null);
  const [futureBills, setFutureBills] = useState<ScheduledTransaction[]>([]);
  const [futureSkip, setFutureSkip] = useState(0);
  const [futureTotal, setFutureTotal] = useState(0);
  const [loadingFuture, setLoadingFuture] = useState(false);

  // Filters — draft inputs (the panel) vs. applied (what fetches actually use).
  // All pages of a result set must be fetched with the same applied filters,
  // otherwise pagination mixes filtered and unfiltered rows.
  const [filterOpen, setFilterOpen] = useState(false);
  const [filterFrom, setFilterFrom] = useState('');
  const [filterTo, setFilterTo] = useState('');
  const [filterMinAmount, setFilterMinAmount] = useState('');
  const [filterMaxAmount, setFilterMaxAmount] = useState('');
  const [filterPayee, setFilterPayee] = useState('');
  const [filterCategoryId, setFilterCategoryId] = useState('');
  const [filterMemo, setFilterMemo] = useState('');
  const [filterUncategorized, setFilterUncategorized] = useState(false);
  const [appliedFilters, setAppliedFilters] = useState<TxFilters>({});

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

  useUnsavedChanges(showForm);

  // Actions menu
  const [openMenuId, setOpenMenuId] = useState<number | null>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  // Right-click context menu
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; tx: Transaction } | null>(null);
  const contextMenuRef = useRef<HTMLDivElement>(null);

  // Jump-to-transaction (from a transfer's "Go to Other Account" link)
  const [highlightTxId, setHighlightTxId] = useState<number | null>(null);
  const rowRefs = useRef<Map<number, HTMLTableRowElement>>(new Map());

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

  // Close dropdown menus on outside click
  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setOpenMenuId(null);
      }
      if (settingsRef.current && !settingsRef.current.contains(e.target as Node)) {
        setSettingsOpen(false);
      }
      if (contextMenuRef.current && !contextMenuRef.current.contains(e.target as Node)) {
        setContextMenu(null);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, []);

  // Close context menu on Escape or scroll
  useEffect(() => {
    if (!contextMenu) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setContextMenu(null); };
    const onScroll = () => setContextMenu(null);
    document.addEventListener('keydown', onKey);
    window.addEventListener('scroll', onScroll, true);
    return () => {
      document.removeEventListener('keydown', onKey);
      window.removeEventListener('scroll', onScroll, true);
    };
  }, [contextMenu]);

  const hasMorePast = pastTxs.length < pastTotal;
  const hasMoreFuture = futureSkip + FUTURE_BATCH < futureTotal;

  // ── Data loading ──────────────────────────────────────────────────────────

  const draftFilters = (): TxFilters => ({
    from: filterFrom || undefined,
    to: filterTo || undefined,
    minAmount: filterMinAmount ? Number(filterMinAmount) : undefined,
    maxAmount: filterMaxAmount ? Number(filterMaxAmount) : undefined,
    payeeName: filterPayee || undefined,
    categoryId: filterCategoryId ? Number(filterCategoryId) : undefined,
    memo: filterMemo || undefined,
    uncategorizedOnly: filterUncategorized || undefined,
  });

  /** Fetches page 1 with explicit filters/sort. `full` also reloads account,
   *  categories, accounts, and institutions (used on mount / account change). */
  const loadPage1 = useCallback(async (
    filters: TxFilters,
    sb: string,
    sd: 'asc' | 'desc',
    full: boolean,
  ) => {
    if (!accountId) return;
    const seq = ++requestSeq.current;
    setInitialLoading(true);
    setError('');
    try {
      const txParams = { ...filters, sortBy: sb, sortDir: sd, page: 1, pageSize: PAST_PAGE_SIZE };
      if (full) {
        const [acc, txResult, cats, accs, insts] = await Promise.all([
          getAccount(accountId),
          getTransactions(accountId, txParams),
          getCategories(),
          getAccounts(true),
          getInstitutions(),
        ]);
        if (seq !== requestSeq.current) return; // a newer request superseded this one
        setAccount(acc);
        setAllAccounts(accs);
        setInstitutions(insts);
        setCategories(cats);
        setPastTxs(txResult.items);
        setPastTotal(txResult.total);
        setAccountBalance(txResult.currentBalance);
        setPastPage(1);
      } else {
        const txResult = await getTransactions(accountId, txParams);
        if (seq !== requestSeq.current) return;
        setPastTxs(txResult.items);
        setPastTotal(txResult.total);
        setAccountBalance(txResult.currentBalance);
        setPastPage(1);
      }
    } catch (err) {
      if (seq !== requestSeq.current) return;
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to load account data.');
    } finally {
      if (seq === requestSeq.current) setInitialLoading(false);
    }
  }, [accountId]);

  // Refetch page 1 with the currently applied filters and sort (after mutations)
  const reload = useCallback(
    () => loadPage1(appliedFilters, sortBy, sortDir, false),
    [loadPage1, appliedFilters, sortBy, sortDir],
  );

  // Loads unfiltered pages (newest first) until the target transaction is found,
  // then flags it for scroll-into-view + highlight. Used when arriving via a
  // transfer's "Go to Other Account" link, so the matching leg is visible.
  const jumpToTransaction = useCallback(async (txId: number) => {
    const seq = ++requestSeq.current;
    setInitialLoading(true);
    setError('');
    try {
      let page = 1;
      let accumulated: Transaction[] = [];
      let total = 0;
      let balance = 0;
      const maxPages = 100; // safety cap (~5000 transactions)
      let found = false;
      while (page <= maxPages) {
        const result = await getTransactions(accountId, { sortBy: 'date', sortDir: 'desc', page, pageSize: PAST_PAGE_SIZE });
        if (seq !== requestSeq.current) return;
        accumulated = accumulated.concat(result.items);
        total = result.total;
        balance = result.currentBalance;
        found = result.items.some(t => t.id === txId);
        if (found || accumulated.length >= total) break;
        page++;
      }
      setPastTxs(accumulated);
      setPastTotal(total);
      setAccountBalance(balance);
      setPastPage(page);
      if (found) setHighlightTxId(txId);
    } catch (err) {
      if (seq !== requestSeq.current) return;
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to load account data.');
    } finally {
      if (seq === requestSeq.current) setInitialLoading(false);
    }
  }, [accountId]);

  // Full load on mount and whenever the account changes; filters reset per account
  useEffect(() => {
    setFilterFrom(''); setFilterTo(''); setFilterMinAmount(''); setFilterMaxAmount('');
    setFilterPayee(''); setFilterCategoryId(''); setFilterMemo(''); setFilterUncategorized(false);
    setAppliedFilters({});
    setSortBy('date');
    setSortDir('desc');

    const targetTx = searchParams.get('tx');
    if (targetTx) {
      setSearchParams(prev => { const p = new URLSearchParams(prev); p.delete('tx'); return p; }, { replace: true });
      loadPage1({}, 'date', 'desc', true).then(() => jumpToTransaction(Number(targetTx)));
    } else {
      loadPage1({}, 'date', 'desc', true);
    }
  }, [accountId, loadPage1]); // eslint-disable-line react-hooks/exhaustive-deps

  // Scroll the highlighted row into view once it's rendered; clear after a moment
  useEffect(() => {
    if (highlightTxId == null) return;
    const el = rowRefs.current.get(highlightTxId);
    el?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    const t = setTimeout(() => setHighlightTxId(null), 2500);
    return () => clearTimeout(t);
  }, [highlightTxId, pastTxs]);

  // ── Load more past (scroll up) ───────────────────────────────────────────────

  const loadMorePast = useCallback(async () => {
    if (loadingPast || !hasMorePast) return;
    setLoadingPast(true);
    const seq = requestSeq.current; // abandon if a new page-1 load happens meanwhile
    try {
      const nextPage = pastPage + 1;
      const result = await getTransactions(accountId, {
        ...appliedFilters,
        sortBy, sortDir,
        page: nextPage,
        pageSize: PAST_PAGE_SIZE,
      });
      if (seq !== requestSeq.current) return;
      setPastTxs(prev => [...prev, ...result.items]);
      setPastPage(nextPage);
    } catch {
      // pagination errors are retried by the next sentinel intersection
    } finally {
      setLoadingPast(false);
    }
  }, [loadingPast, hasMorePast, pastPage, accountId, appliedFilters, sortBy, sortDir]);

  // ── Load future bills ───────────────────────────────────────────────

  const loadFutureBills = useCallback(async (skip = 0, replace = false) => {
    if (loadingFuture) return;
    setLoadingFuture(true);
    try {
      const batch = await getUpcoming({
        accountId,
        days: futureDays,
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
  }, [loadingFuture, accountId, futureDays]);

  useEffect(() => {
    setFutureBills([]);
    setFutureSkip(0);
    setFutureTotal(0);
    if (showFuture) {
      loadFutureBills(0, true);
    }
  }, [showFuture, futureDays, accountId]); // eslint-disable-line react-hooks/exhaustive-deps

  // ── Infinite scroll via IntersectionObserver ─────────────────────────────────

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

  // ── Filters ──────────────────────────────────────────────────────────

  const applyFilters = () => {
    const filters = draftFilters();
    setAppliedFilters(filters);
    loadPage1(filters, sortBy, sortDir, false);
  };

  const clearFilters = () => {
    setFilterFrom(''); setFilterTo(''); setFilterMinAmount(''); setFilterMaxAmount('');
    setFilterPayee(''); setFilterCategoryId(''); setFilterMemo(''); setFilterUncategorized(false);
    setAppliedFilters({});
    // Pass {} explicitly — state updates above won't be visible to this call yet
    loadPage1({}, sortBy, sortDir, false);
  };

  const handleSort = (field: string) => {
    const newDir = sortBy === field && sortDir === 'desc' ? 'asc' : 'desc';
    setSortBy(field);
    setSortDir(newDir);
    loadPage1(appliedFilters, field, newDir, false);
  };

  // ── CRUD ──────────────────────────────────────────────────────────
  // Mutations refetch page 1 from the server: running balances are computed
  // server-side over the full history, so in-place list edits would show
  // stale balances on every other row.

  const handleSaveTx = async (data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt'> & { targetAccountId?: number; transferDestAccountId?: number }) => {
    if (data.transferDestAccountId) {
      await createTransfer({
        sourceAccountId:      accountId,
        destinationAccountId: data.transferDestAccountId,
        date:                 data.date,
        postDate:             data.postDate,
        amount:               Math.abs(data.amount),
        memo:                 data.memo,
      });
      lastUsedDate.current = data.date;
    } else if (editingTx) {
      await updateTransaction(accountId, editingTx.id, data);
    } else {
      await createTransaction(accountId, data);
      lastUsedDate.current = data.date;
    }
    setShowForm(false);
    setEditingTx(null);
    setReceiptPrefill(null);
    setReceiptPayeeName(undefined);
    setReceiptCategoryLabel(undefined);
    await reload();
  };

  const handleDelete = async (txId: number) => {
    if (!confirm('Delete this transaction?')) return;
    try {
      await deleteTransaction(accountId, txId);
      await reload();
    } catch (err) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to delete transaction.');
    }
  };

  const handleEdit = (tx: Transaction) => {
    setEditingTx(tx);
    setShowForm(true);
  };

  const handleToggleStatus = async (tx: Transaction) => {
    const next: Transaction['status'] =
      tx.status === 'Uncleared' ? 'Cleared' :
      tx.status === 'Cleared'   ? 'Reconciled' : 'Uncleared';
    const previous = tx.status;
    // Optimistic update with rollback on failure
    setPastTxs(prev => prev.map(t => t.id === tx.id ? { ...t, status: next } : t));
    try {
      await updateTransaction(accountId, tx.id, { status: next });
    } catch {
      setPastTxs(prev => prev.map(t => t.id === tx.id ? { ...t, status: previous } : t));
    }
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

  // Running balances come from the server (computed over the full account
  // history in date order, regardless of paging/filters/sort), as does the
  // header total (accountBalance).

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
      {/* ── Header ───────────────────────────────────────────────────────────────── */}
      <div className={styles.pageHeader}>
        <div>
          <select
            className={styles.accountSelect}
            value={accountId}
            onChange={e => navigate(`/accounts/${e.target.value}`)}
          >
            {(() => {
              const active   = allAccounts.filter(a => a.isActive)  .sort((a, b) => a.name.localeCompare(b.name));
              const inactive = allAccounts.filter(a => !a.isActive) .sort((a, b) => a.name.localeCompare(b.name));
              return (
                <>
                  {active.map(a => (
                    <option key={a.id} value={a.id}>{a.name}</option>
                  ))}
                  {inactive.length > 0 && active.length > 0 && (
                    <option disabled>──────────</option>
                  )}
                  {inactive.map(a => (
                    <option key={a.id} value={a.id}>{a.name} (inactive)</option>
                  ))}
                </>
              );
            })()}
          </select>
          {account && (
            <div className={styles.accountMeta}>
              {account.type} &bull; Balance: <strong>{formatCurrency(accountBalance)}</strong>
            </div>
          )}
        </div>
        <div className={styles.headerActions}>
          <button className={styles.btnPrimary} onClick={() => { setEditingTx(null); setReceiptPrefill(null); setReceiptPayeeName(undefined); setReceiptCategoryLabel(undefined); setShowForm(s => !s); }}>
            {showForm && !editingTx ? 'Cancel' : '+ New Transaction'}
          </button>
          <button className={styles.btnSecondary} onClick={() => setShowScanner(true)}>
            📷 Scan Receipt
          </button>
          <button className={styles.btnSecondary} onClick={() => setFilterOpen(o => !o)}>
            {filterOpen ? 'Hide Filters' : 'Filters'}
          </button>
          <div className={styles.settingsDropdown} ref={settingsRef}>
            <button
              className={`${styles.btnSecondary} ${settingsOpen ? styles.btnSecondaryActive : ''}`}
              onClick={() => setSettingsOpen(o => !o)}
              title="Register settings"
            >⚙</button>
            {settingsOpen && (
              <div className={styles.settingsMenu}>
                <label className={styles.settingsRow}>
                  <input
                    type="checkbox"
                    checked={showFuture}
                    onChange={e => setShowFuture(e.target.checked)}
                  />
                  Show upcoming transactions
                </label>
                <div className={styles.settingsRow}>
                  <label htmlFor="futureDaysInput">Days ahead</label>
                  <input
                    id="futureDaysInput"
                    type="number"
                    min={1}
                    max={3650}
                    value={futureDays}
                    onChange={e => {
                      const v = Math.max(1, Math.min(3650, Number(e.target.value) || 14));
                      setFutureDays(v);
                      setFutureBills([]);
                      setFutureSkip(0);
                    }}
                    className={styles.settingsDaysInput}
                  />
                </div>
                <div className={styles.settingsDivider} />
                <button
                  className={styles.settingsAction}
                  onClick={() => { setSettingsOpen(false); setShowEditModal(true); }}
                >
                  Edit Account…
                </button>
                <button
                  className={`${styles.settingsAction} ${styles.settingsActionDanger}`}
                  onClick={async () => {
                    setSettingsOpen(false);
                    if (!account) return;
                    if (!confirm(`Delete "${account.name}"? This permanently deletes the account and all its transactions. This cannot be undone.`)) return;
                    try {
                      await deleteAccount(account.id);
                      navigate('/accounts');
                    } catch {
                      alert('Failed to delete account.');
                    }
                  }}
                >
                  Delete Account…
                </button>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* ── Filter panel ──────────────────────────────────────────────────────── */}
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
            <button className={styles.btnSecondary} onClick={clearFilters}>Clear</button>
          </div>
        </div>
      )}

      {/* ── Transaction entry form ───────────────────────────────────────────────── */}
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

      {/* ── Register table ───────────────────────────────────────────────── */}
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
            {/* ── Load-more-past sentinel (top) ─────────────────────────── */}
            {hasMorePast && (
              <tr>
                <td colSpan={8} className={styles.sentinelCell}>
                  <div ref={topSentinelRef} className={styles.sentinel} />
                  {loadingPast && <span className={styles.loadingMore}>Loading older transactions…</span>}
                </td>
              </tr>
            )}

            {/* ── Past / current transactions (newest first) ─────────────── */}
            {pastTxs.length === 0 && !initialLoading ? (
              <tr>
                <td colSpan={8} className={styles.emptyMsg}>No transactions found.</td>
              </tr>
            ) : (
              pastTxs.map(tx => (
                <tr
                  key={tx.id}
                  ref={el => { if (el) rowRefs.current.set(tx.id, el); else rowRefs.current.delete(tx.id); }}
                  className={`${styles.txRow} ${highlightTxId === tx.id ? styles.txRowHighlight : ''}`}
                  onContextMenu={e => { e.preventDefault(); setContextMenu({ x: e.clientX, y: e.clientY, tx }); }}
                >
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
                  <td className={`${styles.right} ${(tx.runningBalance ?? 0) < 0 ? styles.debit : ''}`}>
                    {tx.runningBalance != null ? formatCurrency(tx.runningBalance) : '—'}
                  </td>
                  <td className={styles.statusCell}>
                    <button
                      className={styles[`status${tx.status}`]}
                      onClick={() => handleToggleStatus(tx)}
                      title={tx.status === 'Uncleared' ? 'Mark Cleared' : tx.status === 'Cleared' ? 'Mark Reconciled' : 'Mark Uncleared'}
                    >
                      {tx.status === 'Uncleared' ? '○' : tx.status === 'Cleared' ? '✓' : '✓✓'}
                    </button>
                  </td>
                  <td className={styles.actions}>
                    <div className={styles.actionsMenu} ref={openMenuId === tx.id ? menuRef : undefined}>
                      <button
                        className={styles.btnActionsToggle}
                        onClick={e => { e.stopPropagation(); setOpenMenuId(id => id === tx.id ? null : tx.id); }}
                        title="Actions"
                      >⋯</button>
                      {openMenuId === tx.id && (
                        <div className={styles.actionsDropdown}>
                          <button className={styles.dropdownEdit} onClick={() => { setOpenMenuId(null); handleEdit(tx); }}>Edit</button>
                          <button className={styles.dropdownDelete} onClick={() => { setOpenMenuId(null); handleDelete(tx.id); }}>Delete</button>
                        </div>
                      )}
                    </div>
                  </td>
                </tr>
              ))
            )}

            {/* ── Today divider ──────────────────────────────────────── */}
            <tr className={styles.todayRow}>
              <td colSpan={8}>
                <div className={styles.todayDivider}>
                  <span className={styles.todayLabel}>Today — {formatDate(today)}</span>
                </div>
              </td>
            </tr>

            {/* ── Future scheduled transactions ────────────────────────── */}
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

      {contextMenu && (
        <div
          ref={contextMenuRef}
          className={styles.contextMenu}
          style={{ left: contextMenu.x, top: contextMenu.y }}
        >
          {contextMenu.tx.transferTransactionId && contextMenu.tx.transferAccountId && (
            <button
              className={styles.dropdownGoto}
              onClick={() => {
                const dest = contextMenu.tx.transferAccountId;
                const destTxId = contextMenu.tx.transferTransactionId;
                setContextMenu(null);
                navigate(`/accounts/${dest}?tx=${destTxId}`);
              }}
            >
              Go to Other Account →
            </button>
          )}
          <button className={styles.dropdownEdit} onClick={() => { const tx = contextMenu.tx; setContextMenu(null); handleEdit(tx); }}>Edit</button>
          <button className={styles.dropdownDelete} onClick={() => { const id = contextMenu.tx.id; setContextMenu(null); handleDelete(id); }}>Delete</button>
        </div>
      )}

      {showEditModal && account && (
        <AccountEditModal
          account={account}
          institutions={institutions}
          onSaved={updated => { setAccount(updated); setShowEditModal(false); }}
          onDeleted={() => navigate('/accounts')}
          onClose={() => setShowEditModal(false)}
        />
      )}
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
