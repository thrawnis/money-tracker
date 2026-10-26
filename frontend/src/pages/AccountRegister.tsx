import HoldingsPanel from '../components/HoldingsPanel';
import { useEffect, useState, useCallback, useRef, useLayoutEffect } from 'react';
import { useParams, useNavigate, useSearchParams } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { getAccount, getAccounts } from '../api/accounts';
import { useDeleteAccountFlow } from '../hooks/useDeleteAccountFlow';
import { getTransactions, createTransaction, updateTransaction, updateTransactionStatus, updateTransactionVoided, deleteTransaction, createTransfer, bulkSetCategory } from '../api/transactions';
import CategoryAutocomplete from '../components/CategoryAutocomplete';
import IouDialog from '../components/IouDialog';
import IouPaymentDialog from '../components/IouPaymentDialog';
import { getIouBadges, getIous, type IouBadge, type IouLinkedTransaction, type IouPage } from '../api/ious';
import { flattenCategories } from '../utils/categories';
import { getUpcoming } from '../api/scheduledTransactions';
import { getPreferences } from '../api/preferences';
import { getCategories } from '../api/categories';
import { getInstitutions } from '../api/institutions';
import type { Account, Transaction, Category, ScheduledTransaction, Institution } from '../types';
import TransactionForm, { type SplitInput } from '../components/TransactionForm';
import ReceiptScanner from '../components/ReceiptScanner';
import AccountEditModal from '../components/AccountEditModal';
import type { ExtractedReceipt } from '../api/receipts';
import styles from './AccountRegister.module.css';

type ApiError = { response?: { data?: { message?: string } } };

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

// 500 is the backend's own clamp ceiling (TransactionsController), so this is
// the largest page the server will actually honor — anything higher would
// silently get truncated back down to 500 anyway.
const PAST_PAGE_SIZE = 500;
const FUTURE_BATCH = 5;

// Past transactions are ALWAYS fetched newest-first, regardless of the display
// sort, so page 1 is always the window nearest today (that's what "open to
// Today" and the running-balance-from-now anchoring need). Only date-ascending
// display differs from fetch order, and we reverse the loaded rows at render
// time for that case. Non-date sorts fetch in the chosen direction as before —
// there's no "today" anchor to preserve for them.
const fetchSortDir = (sortField: string, displayDir: 'asc' | 'desc'): 'asc' | 'desc' =>
  sortField === 'date' ? 'desc' : displayDir;

interface TxFilters {
  from?: string;
  to?: string;
  minAmount?: number;
  maxAmount?: number;
  payeeName?: string;
  categoryId?: number;
  memo?: string;
  uncategorized?: boolean;
  status?: string;
  voided?: boolean;
}

/**
 * Status symbols, drawn rather than typed so each reads clearly at a glance:
 * Uncleared an empty circle, Cleared a ringed check, Reconciled a solid
 * disc with a check. Different shapes, not just colors.
 */
function StatusIcon({ status }: { status: string }) {
  return (
    <svg width="18" height="18" viewBox="0 0 18 18" aria-hidden="true" style={{ display: 'block' }}>
      {status === 'Reconciled' ? (
        <>
          <circle cx="9" cy="9" r="8" fill="currentColor" />
          <path d="M5 9.3l2.6 2.6L13 6.5" fill="none" stroke="var(--c-ffffff)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
        </>
      ) : status === 'Cleared' ? (
        <>
          <circle cx="9" cy="9" r="7.25" fill="none" stroke="currentColor" strokeWidth="1.5" />
          <path d="M5 9.3l2.6 2.6L13 6.5" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
        </>
      ) : (
        <circle cx="9" cy="9" r="7.25" fill="none" stroke="currentColor" strokeWidth="1.5" />
      )}
    </svg>
  );
}

/**
 * Small marker on a register row tied to IOUs: 👥 for a purchase split with
 * people (with what's owed and received so far), "IOU" for money that was
 * recorded as an IOU payment. Clicking opens the IOUs page.
 */
function IouBadgeChip({ badge, onOpen }: { badge?: IouBadge; onOpen: () => void }) {
  if (!badge) return null;
  const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  const parts: string[] = [];
  if (badge.owedToMe > 0) parts.push(`${badge.people.join(', ')} owe${badge.people.length === 1 ? 's' : ''} ${money(badge.owedToMe)} — ${money(badge.receivedSoFar)} received so far`);
  if (badge.iOwe > 0) parts.push(`You owe ${money(badge.iOwe)} — ${money(badge.paidSoFar)} paid so far`);
  for (const p of badge.payments) parts.push(`IOU payment ${p.direction === 'Received' ? 'from' : 'to'} ${p.person}: ${money(p.amount)}`);
  const isPurchase = badge.owedToMe > 0 || badge.iOwe > 0;
  return (
    <button
      type="button"
      className={styles.iouBadge}
      title={`${parts.join('\n')}\nClick to open IOUs`}
      aria-label={`IOU: ${parts.join('; ')}`}
      onClick={e => { e.stopPropagation(); onOpen(); }}
    >
      {isPurchase ? '👥' : 'IOU'}
      {isPurchase && badge.owedToMe > 0 && (
        <span className={styles.iouBadgeText}>{money(badge.receivedSoFar)} / {money(badge.owedToMe)}</span>
      )}
    </button>
  );
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
  const { requestDelete, deleting: deletingAccount, error: deleteAccountError, modal: deleteAccountModal } =
    useDeleteAccountFlow(() => navigate('/accounts'));
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
  // Seeded from the user's saved preference (Settings → Preferences) below;
  // 31 is the built-in default until that loads or if none was ever set.
  const [futureDays, setFutureDays] = useState(31);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const settingsRef = useRef<HTMLDivElement>(null);
  const [futureBills, setFutureBills] = useState<ScheduledTransaction[]>([]);
  const [futureSkip, setFutureSkip] = useState(0);
  const [futureTotal, setFutureTotal] = useState(0);
  const [loadingFuture, setLoadingFuture] = useState(false);
  const [futureError, setFutureError] = useState('');

  // Filters — draft inputs (the panel) vs. applied (what fetches actually use).
  // All pages of a result set must be fetched with the same applied filters,
  // otherwise pagination mixes filtered and unfiltered rows.
  const [jumpDate, setJumpDate] = useState('');
  const [filterOpen, setFilterOpen] = useState(false);
  const [filterFrom, setFilterFrom] = useState('');
  const [filterTo, setFilterTo] = useState('');
  const [filterMinAmount, setFilterMinAmount] = useState('');
  const [filterMaxAmount, setFilterMaxAmount] = useState('');
  const [filterPayee, setFilterPayee] = useState('');
  const [filterCategoryId, setFilterCategoryId] = useState('');
  const [filterMemo, setFilterMemo] = useState('');
  const [filterUncategorized, setFilterUncategorized] = useState(false);
  const [filterStatus, setFilterStatus] = useState('');
  // '' = both (register's normal default), 'true' = voided only, 'false' =
  // exclude voided. A plain checkbox can't express three states, so this is
  // a select like Status rather than a checkbox like Uncategorized above.
  const [filterVoided, setFilterVoided] = useState('');
  const [appliedFilters, setAppliedFilters] = useState<TxFilters>({});

  // Sort — seeded from the user's saved preference (Settings → Preferences),
  // falling back to date/desc until that loads or if none was ever set.
  const [sortBy, setSortBy] = useState('date');
  const [sortDir, setSortDir] = useState<'asc' | 'desc'>('desc');
  const defaultSort = useRef({ sortBy: 'date', sortDir: 'desc' as 'asc' | 'desc' });
  const [defaultSortReady, setDefaultSortReady] = useState(false);

  useEffect(() => {
    getPreferences()
      .then(p => {
        defaultSort.current = {
          sortBy: p.defaultRegisterSortBy ?? 'date',
          sortDir: p.defaultRegisterSortDir ?? 'desc',
        };
        if (p.defaultFutureDays) setFutureDays(p.defaultFutureDays);
      })
      .catch(() => { /* fall back to date/desc, 31 days */ })
      .finally(() => setDefaultSortReady(true));
  }, []);

  // Form
  const [showForm, setShowForm] = useState(false);
  const [editingTx, setEditingTx] = useState<Transaction | null>(null);
  // Bulk edit: ticked transaction rows, the last one clicked (for shift-click
  // ranges), and the category being applied to all of them.
  const [selectedIds, setSelectedIds] = useState<Set<number>>(new Set());
  const [lastSelectedId, setLastSelectedId] = useState<number | null>(null);
  const [bulkCategory, setBulkCategory] = useState('');
  const [bulkBusy, setBulkBusy] = useState(false);
  const [bulkMessage, setBulkMessage] = useState('');
  // A selection belongs to one account's register: switching accounts drops it.
  const [selectionAccountId, setSelectionAccountId] = useState(accountId);
  if (selectionAccountId !== accountId) {
    setSelectionAccountId(accountId);
    setSelectedIds(new Set());
    setBulkMessage('');
  }
  const [formDirty, setFormDirty] = useState(false);
  // Bumped when a "new" form needs a remount despite the key staying 'new'
  // (e.g. a receipt scan landing while a blank form is already open)
  const [formNonce, setFormNonce] = useState(0);
  const [receiptPrefill, setReceiptPrefill] = useState<Partial<Transaction> | null>(null);
  const [receiptPayeeName, setReceiptPayeeName] = useState<string | undefined>(undefined);
  const [receiptCategoryLabel, setReceiptCategoryLabel] = useState<string | undefined>(undefined);
  const [showScanner, setShowScanner] = useState(false);

  // Switching the form's target transaction (a different row, "+ New
  // Transaction", the N shortcut, a receipt scan) while there are unsaved
  // edits in the open form would otherwise silently discard them.
  const confirmDiscardIfDirty = () =>
    !formDirty || confirm('You have unsaved changes to this transaction. Discard them?');
  // State, not a ref: this is read during render to seed the new-transaction
  // form's date, and reading a ref while rendering is exactly the tearing
  // hazard the React Compiler flags. It also genuinely IS render-affecting
  // input, so state is the honest model. lastUsedDateRef mirrors it for the
  // callback paths that need the current value without re-subscribing.
  const [lastUsedDate, setLastUsedDate] = useState<string>(() => new Date().toISOString().slice(0, 10));
  const lastUsedDateRef = useRef(lastUsedDate);

  // Actions menu
  const [openMenuId, setOpenMenuId] = useState<number | null>(null);
  // IOUs: badges for loaded rows tagged with IOUs or linked to an IOU payment,
  // and the tag / record-payment forms opened from a row's menu.
  const [iouBadges, setIouBadges] = useState<Map<number, IouBadge>>(new Map());
  const [iouForm, setIouForm] = useState<{ kind: 'tag' | 'payment'; tx: IouLinkedTransaction; data: IouPage } | null>(null);
  const [iouBadgesVersion, setIouBadgesVersion] = useState(0);
  const menuRef = useRef<HTMLDivElement>(null);

  // Right-click context menu
  const [contextMenu, setContextMenu] = useState<{ x: number; y: number; tx: Transaction } | null>(null);
  const contextMenuRef = useRef<HTMLDivElement>(null);

  // Jump-to-transaction (from a transfer's "Go to Other Account" link)
  const [highlightTxId, setHighlightTxId] = useState<number | null>(null);
  const rowRefs = useRef<Map<number, HTMLTableRowElement>>(new Map());
  const scrolledForHighlightRef = useRef<number | null>(null);

  // Pre-swap scroll height captured by reloadLoadedPages, consumed by the
  // layout effect that keeps the user in place across an in-place refetch.
  const pendingRestoreHeightRef = useRef<number | null>(null);

  // Open the register positioned at the Today divider on first load of an account
  const todayRowRef = useRef<HTMLTableRowElement>(null);
  const [scrollToToday, setScrollToToday] = useState(false);

  // Infinite scroll sentinels
  const topSentinelRef = useRef<HTMLDivElement>(null);
  const bottomSentinelRef = useRef<HTMLDivElement>(null);
  const tableWrapperRef = useRef<HTMLDivElement>(null);
  const formRef = useRef<HTMLDivElement>(null);

  // Keyboard shortcut: N = new transaction
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement).tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      if (e.key === 'n' || e.key === 'N') {
        e.preventDefault();
        if (!confirmDiscardIfDirty()) return;
        setEditingTx(null);
        setShowForm(true);
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [formDirty]);

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

  // ── Data loading ──

  const draftFilters = (): TxFilters => ({
    from: filterFrom || undefined,
    to: filterTo || undefined,
    minAmount: filterMinAmount ? Number(filterMinAmount) : undefined,
    maxAmount: filterMaxAmount ? Number(filterMaxAmount) : undefined,
    payeeName: filterPayee || undefined,
    categoryId: filterCategoryId ? Number(filterCategoryId) : undefined,
    memo: filterMemo || undefined,
    uncategorized: filterUncategorized || undefined,
    status: filterStatus || undefined,
    voided: filterVoided === '' ? undefined : filterVoided === 'true',
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
      const txParams = { ...filters, sortBy: sb, sortDir: fetchSortDir(sb, sd), page: 1, pageSize: PAST_PAGE_SIZE };
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

  // Refetch every page the user has scrolled into view, not just page 1, and
  // hold the scroll position across the swap. Plain reload() replaces pastTxs
  // with page 1 alone, so editing a transaction after scrolling back through
  // history would collapse the list and dump the user somewhere else entirely.
  const reloadLoadedPages = useCallback(async () => {
    const pageCount = pastPage;
    if (pageCount <= 1) return reload();

    const seq = ++requestSeq.current;
    // Captured synchronously, before any await: the browser preserves
    // scrollTop across the row swap, so the layout effect only needs the
    // pre-swap height to compensate for any net size change.
    pendingRestoreHeightRef.current = tableWrapperRef.current?.scrollHeight ?? 0;
    setError('');
    try {
      const results = await Promise.all(
        Array.from({ length: pageCount }, (_, i) =>
          getTransactions(accountId, {
            ...appliedFilters,
            sortBy, sortDir: fetchSortDir(sortBy, sortDir),
            page: i + 1,
            pageSize: PAST_PAGE_SIZE,
          })
        )
      );
      if (seq !== requestSeq.current) return;
      setPastTxs(results.flatMap(r => r.items));
      setPastTotal(results[0].total);
      setAccountBalance(results[0].currentBalance);
      setPastPage(pageCount);
    } catch (err) {
      if (seq !== requestSeq.current) return;
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to load account data.');
    }
  }, [pastPage, reload, accountId, appliedFilters, sortBy, sortDir]);

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
      // The accumulated pages are date-ordered (newest-first internally) — if a
      // non-date sort was active, leaving it would mislabel the list under the
      // old column header AND make the next load-more append pages of a
      // different sort onto this accumulation (duplicates/out-of-order rows).
      // Forcing sortBy to date keeps everything consistent; sortDir is left
      // alone since date display in either direction renders correctly from
      // the newest-first internal order.
      setSortBy('date');
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

  // Loads pages (newest first) until a transaction on or before the target date
  // is in the loaded window, then highlights/scrolls to the most recent one on
  // or before that date. Filters are cleared so the target is always reachable.
  const jumpToDate = useCallback(async (targetDate: string) => {
    const seq = ++requestSeq.current;
    setInitialLoading(true);
    setError('');
    try {
      let page = 1;
      let accumulated: Transaction[] = [];
      let total = 0;
      let balance = 0;
      const maxPages = 200; // safety cap (~10k transactions)
      const onOrBefore = (t: Transaction) => (t.postDate ?? t.date) <= targetDate;
      let reached = false;
      while (page <= maxPages) {
        const result = await getTransactions(accountId, { sortBy: 'date', sortDir: 'desc', page, pageSize: PAST_PAGE_SIZE });
        if (seq !== requestSeq.current) return;
        accumulated = accumulated.concat(result.items);
        total = result.total;
        balance = result.currentBalance;
        reached = result.items.some(onOrBefore);
        if (reached || accumulated.length >= total) break;
        page++;
      }
      // Clear any active filter so what's displayed matches the unfiltered pages
      // we just loaded (and so subsequent load-more stays unfiltered too).
      setFilterFrom(''); setFilterTo(''); setFilterMinAmount(''); setFilterMaxAmount('');
      setFilterPayee(''); setFilterCategoryId(''); setFilterMemo(''); setFilterUncategorized(false);
      setAppliedFilters({});
      // Same reasoning as jumpToTransaction: the accumulation is date-ordered,
      // so a lingering non-date sort would mislabel it and desync load-more.
      setSortBy('date');
      setPastTxs(accumulated);
      setPastTotal(total);
      setAccountBalance(balance);
      setPastPage(page);

      // accumulated is newest-first, so find() returns the most recent tx on or
      // before the target; if none exists (target predates all history) fall
      // back to the oldest loaded row.
      const anchor = accumulated.find(onOrBefore) ?? accumulated[accumulated.length - 1];
      if (anchor) setHighlightTxId(anchor.id);
      else setError('This account has no transactions to jump to.');
    } catch (err) {
      if (seq !== requestSeq.current) return;
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to load account data.');
    } finally {
      if (seq === requestSeq.current) setInitialLoading(false);
    }
  }, [accountId]);

  // Full load on mount and whenever the account changes; filters reset per account.
  // Waits for the default-sort preference to load first so the initial fetch
  // uses it directly instead of loading with date/desc and re-fetching.
  useEffect(() => {
    if (!defaultSortReady) return;
    setFilterFrom(''); setFilterTo(''); setFilterMinAmount(''); setFilterMaxAmount('');
    setFilterPayee(''); setFilterCategoryId(''); setFilterMemo(''); setFilterUncategorized(false);
    setFilterStatus(''); setFilterVoided('');
    setAppliedFilters({});
    setJumpDate('');
    const { sortBy: defBy, sortDir: defDir } = defaultSort.current;
    setSortBy(defBy);
    setSortDir(defDir);

    const targetTx = searchParams.get('tx');
    if (targetTx) {
      setSearchParams(prev => { const p = new URLSearchParams(prev); p.delete('tx'); return p; }, { replace: true });
      loadPage1({}, defBy, defDir, true).then(() => jumpToTransaction(Number(targetTx)));
    } else {
      loadPage1({}, defBy, defDir, true).then(() => setScrollToToday(true));
    }
  }, [accountId, loadPage1, defaultSortReady]); // eslint-disable-line react-hooks/exhaustive-deps

  // Position the register at the Today divider once it's rendered after that
  // initial load (no animation — this is where the page should already be
  // "open to", not a visible jump).
  useEffect(() => {
    if (!scrollToToday) return;
    todayRowRef.current?.scrollIntoView({ behavior: 'auto', block: 'center' });
    setScrollToToday(false);
  }, [scrollToToday, pastTxs, futureBills]);

  // Scroll the highlighted row into view once it's rendered — but only once
  // per highlight, not on every pastTxs change. pastTxs is also a dep here
  // (the row may not exist yet on the render where highlightTxId is set),
  // but loading more past transactions afterward mutates pastTxs too, and
  // without the "already scrolled" guard that re-triggers scrollIntoView and
  // yanks the view back down to the highlight mid-scroll-up.
  useEffect(() => {
    if (highlightTxId == null) { scrolledForHighlightRef.current = null; return; }
    if (scrolledForHighlightRef.current === highlightTxId) return;
    const el = rowRefs.current.get(highlightTxId);
    if (!el) return;
    el.scrollIntoView({ behavior: 'smooth', block: 'center' });
    scrolledForHighlightRef.current = highlightTxId;
  }, [highlightTxId, pastTxs]);

  // ── Load more past (scroll up) ──

  // Synchronous in-flight flag: the loadingPast STATE lags behind setLoadingPast
  // by a render, so two IntersectionObserver entries delivered in one batch
  // would both pass the state check and fetch (and append) the same page twice.
  const loadingPastRef = useRef(false);

  // Scroll-anchoring for oldest-first display: newly-loaded older transactions
  // are inserted ABOVE the content the user is currently looking at (fetch is
  // always newest-first internally; ascending display reverses it, so a
  // "load older" append lands at the top of the visible list). The browser has
  // no way to know it should keep the user's current rows in place — it just
  // renders the taller content with the same scrollTop, which visually shoves
  // the previously-visible rows down and off screen, landing the viewport on
  // the tail end of the newly-inserted batch instead. pendingScrollAnchorRef
  // captures the scroll height right before that insertion so the layout
  // effect below can compensate scrollTop by exactly how much content grew.
  const pendingScrollAnchorRef = useRef<number | null>(null);


  const loadMorePast = useCallback(async () => {
    if (loadingPastRef.current || !hasMorePast) return;
    loadingPastRef.current = true;
    setLoadingPast(true);
    const seq = requestSeq.current; // abandon if a new page-1 load happens meanwhile
    try {
      // Always fetches the next OLDER page (fetch order is newest-first), so
      // this appends to the tail of the newest-first pastTxs array whether the
      // display is ascending or descending — the render layer handles direction.
      const nextPage = pastPage + 1;
      const result = await getTransactions(accountId, {
        ...appliedFilters,
        sortBy, sortDir: fetchSortDir(sortBy, sortDir),
        page: nextPage,
        pageSize: PAST_PAGE_SIZE,
      });
      if (seq !== requestSeq.current) return;
      if (sortBy === 'date' && sortDir === 'asc' && tableWrapperRef.current) {
        pendingScrollAnchorRef.current = tableWrapperRef.current.scrollHeight;
      }
      setPastTxs(prev => [...prev, ...result.items]);
      setPastPage(nextPage);
    } catch {
      // pagination errors are retried by the next sentinel intersection
    } finally {
      loadingPastRef.current = false;
      setLoadingPast(false);
    }
  }, [hasMorePast, pastPage, accountId, appliedFilters, sortBy, sortDir]);

  // Runs synchronously after the DOM reflects the prepended rows but before
  // the browser paints, so the compensating scroll jump is never visible.
  useLayoutEffect(() => {
    const anchor = pendingScrollAnchorRef.current;
    if (anchor == null) return;
    pendingScrollAnchorRef.current = null;
    const el = tableWrapperRef.current;
    if (!el) return;
    el.scrollTop += el.scrollHeight - anchor;
  }, [pastTxs]);

  // Same idea, for the whole-list swap an in-place refetch performs: scrollTop
  // survives the swap, so nudging it by the net height change keeps the rows
  // the user was reading under the cursor instead of jumping to page 1.
  useLayoutEffect(() => {
    const before = pendingRestoreHeightRef.current;
    if (before == null) return;
    pendingRestoreHeightRef.current = null;
    const el = tableWrapperRef.current;
    if (!el) return;
    el.scrollTop += el.scrollHeight - before;
  }, [pastTxs]);

  // ── Load future bills ──

  // Same stale-response protection as past-transaction fetches: a slow response
  // for the previous account must never overwrite the current account's bills,
  // and a replace-load for a new account must not be blocked by (or race with)
  // an in-flight load for the old one.
  const futureSeq = useRef(0);

  const loadFutureBills = useCallback(async (skip = 0, replace = false) => {
    // A replace supersedes any in-flight load (the seq guard discards it);
    // append-loads (infinite scroll) still skip while one is running.
    if (loadingFuture && !replace) return;
    const seq = ++futureSeq.current;
    setLoadingFuture(true);
    setFutureError('');
    try {
      const batch = await getUpcoming({
        accountId,
        days: futureDays,
        limit: FUTURE_BATCH + 1,
        skip,
      });
      if (seq !== futureSeq.current) return;
      const hasMore = batch.length > FUTURE_BATCH;
      const items = hasMore ? batch.slice(0, FUTURE_BATCH) : batch;
      setFutureBills(prev => replace ? items : [...prev, ...items]);
      setFutureSkip(skip + items.length);
      // Update total estimate: if we got a full batch + more indicator, mark as having more
      setFutureTotal(skip + items.length + (hasMore ? 1 : 0));
    } catch {
      if (seq === futureSeq.current) setFutureError('Failed to load upcoming scheduled transactions.');
    } finally {
      if (seq === futureSeq.current) setLoadingFuture(false);
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

  // ── Infinite scroll via IntersectionObserver ──

  // Read the latest callbacks/values via refs instead of effect dependencies.
  // loadMorePast's identity changes every time pastPage advances (same for
  // loadFutureBills/futureSkip), and IntersectionObserver fires its callback
  // immediately on observe() if the target is already intersecting — so
  // recreating the observer on every dependency change re-triggered a load
  // immediately, whose success changed the dependency again, recreating the
  // observer again... a runaway loop that blew through every past page in a
  // fraction of a second. The observer itself now only needs to be (re)created
  // when a sentinel row's presence in the DOM can actually change.
  const loadMorePastRef = useRef(loadMorePast);
  const loadFutureBillsRef = useRef(loadFutureBills);
  const futureSkipRef = useRef(futureSkip);
  const showFutureRef = useRef(showFuture);
  const hasMoreFutureRef = useRef(hasMoreFuture);
  useEffect(() => {
    loadMorePastRef.current = loadMorePast;
    loadFutureBillsRef.current = loadFutureBills;
    futureSkipRef.current = futureSkip;
    showFutureRef.current = showFuture;
    hasMoreFutureRef.current = hasMoreFuture;
  });

  useEffect(() => {
    const observer = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        // loadMorePast is self-guarded (bails if already loading or exhausted).
        if (entry.target === topSentinelRef.current) {
          loadMorePastRef.current();
        }
        if (entry.target === bottomSentinelRef.current && showFutureRef.current && hasMoreFutureRef.current) {
          loadFutureBillsRef.current(futureSkipRef.current);
        }
      }
    // rootMargin extends the trigger zone well past the visible edge, so the
    // next batch is fetched and appended *before* the user's scroll gesture
    // reaches it — without this, the sentinel only fires once it's actually
    // on screen, by which point the user's scroll momentum has already
    // stopped (they hit the bottom of the currently-rendered content), so
    // nothing continues until they manually scroll again. 1500px rather than
    // a smaller margin because PAST_PAGE_SIZE batches are 500 rows — a
    // heavier fetch (more to transfer, more to decrypt server-side when a
    // payee/memo filter is active) needs more lead time to finish before the
    // user's scroll actually catches up to it.
    }, { threshold: 0.1, rootMargin: '1500px 0px' });

    if (topSentinelRef.current) observer.observe(topSentinelRef.current);
    if (bottomSentinelRef.current) observer.observe(bottomSentinelRef.current);
    return () => observer.disconnect();
    // Only re-observe when a sentinel row can actually appear/disappear from
    // the DOM (hasMorePast/showFuture/hasMoreFuture gate their conditional
    // render) or the register itself switches ends (oldest-first display moves
    // the "load older" sentinel from the bottom of the list to the top).
    // Everything else is read via ref above.
  }, [hasMorePast, showFuture, hasMoreFuture, sortBy, sortDir]);

  // ── Filters ──

  const applyFilters = () => {
    const filters = draftFilters();
    setAppliedFilters(filters);
    setHighlightTxId(null);
    loadPage1(filters, sortBy, sortDir, false);
  };

  const clearFilters = () => {
    setFilterFrom(''); setFilterTo(''); setFilterMinAmount(''); setFilterMaxAmount('');
    setFilterPayee(''); setFilterCategoryId(''); setFilterMemo(''); setFilterUncategorized(false);
    setFilterStatus(''); setFilterVoided('');
    setAppliedFilters({});
    setHighlightTxId(null);
    // Pass {} explicitly — state updates above won't be visible to this call yet
    loadPage1({}, sortBy, sortDir, false);
  };

  const handleSort = (field: string) => {
    const newDir = sortBy === field && sortDir === 'desc' ? 'asc' : 'desc';
    setSortBy(field);
    setSortDir(newDir);
    setHighlightTxId(null);
    loadPage1(appliedFilters, field, newDir, false);
  };

  // ── CRUD ──
  // Mutations refetch page 1 from the server: running balances are computed
  // server-side over the full history, so in-place list edits would show
  // stale balances on every other row.

  const handleSaveTx = async (formData: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt' | 'splits' | 'isVoided'> & { targetAccountId?: number; transferDestAccountId?: number; splits?: SplitInput[]; splitWithPeople?: boolean }) => {
    // "Split with people" is a UI choice, not transaction data.
    const { splitWithPeople, ...data } = formData;
    // Only Reconciled transactions ask first; Cleared ones save freely.
    if (editingTx?.status === 'Reconciled') {
      const proceed = confirm(
        'This transaction is marked Reconciled. Editing it may affect your reconciled balance. Save changes anyway?'
      );
      if (!proceed) return;
    }
    // Order matters: an EXISTING transfer submits with transferDestAccountId
    // set too (the form seeds it from the linked leg), so the editingTx branch
    // must win or editing a transfer would create a duplicate pair instead of
    // updating in place (the backend Update syncs the linked leg itself).
    let newTxId: number | null = null;
    // The form's Account field defaults to this register but can redirect the
    // entry elsewhere. Edits keep going through this account's route — the
    // backend's TargetAccountId handles the move.
    const entryAccountId = data.targetAccountId ?? accountId;
    if (editingTx) {
      await updateTransaction(accountId, editingTx.id, data);
    } else if (data.transferDestAccountId) {
      const { debit } = await createTransfer({
        sourceAccountId:      entryAccountId,
        destinationAccountId: data.transferDestAccountId,
        date:                 data.date,
        postDate:             data.postDate,
        amount:               Math.abs(data.amount),
        memo:                 data.memo,
      });
      lastUsedDateRef.current = data.date;
      setLastUsedDate(data.date);
      newTxId = debit.id;
    } else {
      const created = await createTransaction(entryAccountId, data);
      lastUsedDateRef.current = data.date;
      setLastUsedDate(data.date);
      newTxId = created.id;
      // Opted in on the form: go straight to splitting it with people.
      if (splitWithPeople) {
        const payeeName = data.payeeId != null ? created.payee?.name ?? null : null;
        void openIouForm('tag', { ...created, accountId: entryAccountId, payee: created.payee ?? (payeeName ? { id: data.payeeId!, name: payeeName } : undefined) });
      }
    }
    setShowForm(false);
    setEditingTx(null);
    setReceiptPrefill(null);
    setReceiptPayeeName(undefined);
    setReceiptCategoryLabel(undefined);
    // For a brand-new transaction, jump to it (like the "Go to Other Account"
    // link does) so it's visible and highlighted regardless of the active
    // sort/filters — those could otherwise leave it off the loaded page.
    if (newTxId != null) {
      if (entryAccountId !== accountId) {
        // Filed to a different account — follow it there rather than leaving
        // the user staring at a register the row isn't in. ?tx= makes that
        // register open on the new row, highlighted, same as a transfer's
        // "Go to Other Account" link.
        navigate(`/accounts/${entryAccountId}?tx=${newTxId}`);
        return;
      }
      await jumpToTransaction(newTxId);
    } else {
      // Edit of an existing row: refetch in place so the user stays exactly
      // where they were instead of being dropped back at page 1. Not
      // awaited — the save itself already succeeded (the awaits above this
      // point are what can fail/reject), so the form has nothing left to
      // wait on. Awaiting this too just made "Saving…" sit there for as
      // long as it takes to re-fetch every loaded page (up to 500 rows
      // apiece) purely to refresh running-balance figures that were already
      // correct a moment ago and are about to be silently corrected in place.
      reloadLoadedPages();
    }
  };

  const handleDelete = async (txId: number) => {
    if (!confirm('Delete this transaction?')) return;
    // Optimistic: the row disappears the instant you confirm, not after a
    // round trip. Deleting shifts every later row's running balance and the
    // header total, which — like void below — isn't something to recompute
    // by hand client-side; that correction arrives a moment later from the
    // background reconcile, quietly, with no loading state of its own.
    const prevTxs = pastTxs;
    const prevTotal = pastTotal;
    setPastTxs(prev => prev.filter(t => t.id !== txId));
    setPastTotal(prev => Math.max(0, prev - 1));
    try {
      await deleteTransaction(accountId, txId);
      reloadLoadedPages();
    } catch (err) {
      setPastTxs(prevTxs);
      setPastTotal(prevTotal);
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to delete transaction.');
    }
  };

  const handleEdit = (tx: Transaction) => {
    if (editingTx?.id === tx.id && showForm) return; // already editing this one
    if (!confirmDiscardIfDirty()) return;
    setEditingTx(tx);
    setShowForm(true);
    setHighlightTxId(null);
  };

  useEffect(() => {
    if (showForm) formRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }, [showForm]);

  const handleToggleStatus = async (tx: Transaction) => {
    const next: Transaction['status'] =
      tx.status === 'Uncleared' ? 'Cleared' :
      tx.status === 'Cleared'   ? 'Reconciled' : 'Uncleared';
    const previous = tx.status;
    // Optimistic update with rollback on failure
    setPastTxs(prev => prev.map(t => t.id === tx.id ? { ...t, status: next } : t));
    try {
      await updateTransactionStatus(accountId, tx.id, next);
    } catch {
      setPastTxs(prev => prev.map(t => t.id === tx.id ? { ...t, status: previous } : t));
    }
  };

  const handleToggleVoid = async (tx: Transaction) => {
    const next = !tx.isVoided;
    if (next) {
      const msg = tx.transferTransactionId
        ? 'Void this transfer? Both linked transactions will be voided — the row stays visible in both registers, but no longer counts toward either balance.'
        : 'Void this transaction? It stays in the register but is excluded from the balance, Reports, and duplicate detection.';
      if (!confirm(msg)) return;
    }
    // Optimistic: the row dims/undims immediately. Voiding changes the
    // running balance of every row after it and the header total, so those
    // stay as-is for a moment rather than being (incorrectly) patched by
    // hand here — the background reconcile below corrects them quietly,
    // same as delete. Only this row's own isVoided flag is safe to predict
    // without the server: everything downstream needs the real computation.
    setPastTxs(prev => prev.map(t => t.id === tx.id ? { ...t, isVoided: next } : t));
    try {
      await updateTransactionVoided(accountId, tx.id, next);
      reloadLoadedPages();
    } catch (err) {
      setPastTxs(prev => prev.map(t => t.id === tx.id ? { ...t, isVoided: !next } : t));
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to update transaction.');
    }
  };

  // Prefills a new blank transaction with another one's details so the user
  // can review/tweak (date especially) before saving a fresh copy. Not
  // offered for transfers: a new transfer is always created debiting this
  // account, so duplicating the credit/destination leg of an existing
  // transfer would silently reverse its direction.
  const handleDuplicate = (tx: Transaction) => {
    if (!confirmDiscardIfDirty()) return;
    // Bump the form key: if a blank "new" form is already mounted, the key
    // would otherwise stay 'new-N' and the form would never re-read this prefill.
    setFormNonce(n => n + 1);
    setEditingTx(null);
    setReceiptPrefill({
      date:       tx.date,
      postDate:   tx.postDate,
      amount:     tx.amount,
      memo:       tx.memo,
      payeeId:    tx.payeeId,
      payee:      tx.payee,
      categoryId: tx.categoryId,
      category:   tx.category,
      splits:     tx.splits,
    });
    setReceiptPayeeName(undefined);
    setReceiptCategoryLabel(undefined);
    setShowForm(true);
  };

  const handleReceiptConfirm = (data: ExtractedReceipt) => {
    if (!confirmDiscardIfDirty()) return;
    setShowScanner(false);
    // Bump the form key: if a blank "new" form is already mounted, the key would
    // otherwise stay 'new-N' and the form would never re-read the receipt prefill.
    setFormNonce(n => n + 1);
    setEditingTx(null);
    setReceiptPrefill({
      date: data.date ?? lastUsedDateRef.current,
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

  // Today/Future block only sits between past-oldest and future when the
  // register reads chronologically top-to-bottom (sorted oldest-first by
  // date); see isChronologicalAsc usage below for where it's placed instead.
  const isChronologicalAsc = sortBy === 'date' && sortDir === 'asc';
  const isDateSort = sortBy === 'date';

  // pastTxs is stored newest-first (fetch order). Split out real future-dated
  // transactions (e.g. post-dated checks) so they render on the correct side of
  // the Today divider, then flip to oldest-first for ascending date display —
  // fetch order never changes, only how we render it.
  const futureRealTxsRaw = isDateSort ? pastTxs.filter(tx => tx.date > today) : [];
  const restTxsRaw = isDateSort ? pastTxs.filter(tx => tx.date <= today) : pastTxs;
  const futureRealTxs = isChronologicalAsc ? [...futureRealTxsRaw].reverse() : futureRealTxsRaw;
  const restTxs = isChronologicalAsc ? [...restTxsRaw].reverse() : restTxsRaw;
  // Every loaded transaction row in on-screen order — what shift-click
  // ranges and the bulk "select all" checkbox cover.
  const selectableIds = (isChronologicalAsc ? [...restTxs, ...futureRealTxs] : [...futureRealTxs, ...restTxs]).map(t => t.id);
  const allLoadedSelected = selectableIds.length > 0 && selectableIds.every(id => selectedIds.has(id));

  const goToBillsPrefilled = (tx: Transaction) => {
    navigate('/bills', {
      state: {
        prefill: {
          accountId,
          isTransfer: !!tx.transferAccountId,
          transferAccountId: tx.transferAccountId,
          payeeId: tx.payeeId,
          payeeName: tx.payee?.name,
          categoryId: tx.categoryId,
          memo: tx.memo,
          amount: tx.amount,
          nextDueDate: tx.date,
        },
      },
    });
  };

  // Every loaded transaction row, in on-screen order (future then past, or
  // the reverse when sorted oldest-first) — what shift-click ranges and
  // "select all" cover.

  const toggleSelected = (id: number, shift: boolean) => {
    setBulkMessage('');
    setSelectedIds(prev => {
      const next = new Set(prev);
      if (shift && lastSelectedId != null && lastSelectedId !== id) {
        // Shift-click: tick everything between the last click and this one.
        const order = selectableIds;
        const [a, b] = [order.indexOf(lastSelectedId), order.indexOf(id)].sort((x, y) => x - y);
        if (a >= 0) for (const rid of order.slice(a, b + 1)) next.add(rid);
        else next.add(id);
      } else if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
    setLastSelectedId(id);
  };

  const applyBulkCategory = async () => {
    const label = bulkCategory.trim();
    const match = flattenCategories(categories).find(c => c.label.toLowerCase() === label.toLowerCase());
    if (!match) {
      setBulkMessage(`No category called "${label}". Pick one from the suggestions.`);
      return;
    }
    const n = selectedIds.size;
    if (!confirm(`Set the category of ${n} transaction${n === 1 ? '' : 's'} to "${match.label}"?`)) return;
    setBulkBusy(true);
    setBulkMessage('');
    try {
      const res = await bulkSetCategory(accountId, [...selectedIds], match.id);
      const parts = [`Set "${match.label}" on ${res.updated} transaction${res.updated === 1 ? '' : 's'}.`];
      if (res.unchanged > 0) parts.push(`${res.unchanged} already had it.`);
      if (res.skippedSplits > 0) parts.push(`${res.skippedSplits} split transaction${res.skippedSplits === 1 ? ' was' : 's were'} skipped (their categories are per split).`);
      setBulkMessage(parts.join(' '));
      setSelectedIds(new Set());
      setBulkCategory('');
      await reloadLoadedPages();
    } catch (err) {
      setBulkMessage((err as ApiError)?.response?.data?.message ?? 'Failed to update the categories.');
    } finally {
      setBulkBusy(false);
    }
  };

  // Refresh IOU badges whenever the loaded rows change (or an IOU form saves).
  const loadedTxKey = pastTxs.map(t => t.id).join(',');
  useEffect(() => {
    let cancelled = false;
    const ids = loadedTxKey ? loadedTxKey.split(',').map(Number) : [];
    getIouBadges(ids)
      .then(list => { if (!cancelled) setIouBadges(new Map(list.map(b => [b.transactionId, b]))); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [loadedTxKey, iouBadgesVersion]);

  const openIouForm = async (kind: 'tag' | 'payment', tx: Transaction) => {
    try {
      const data = await getIous();
      setIouForm({
        kind,
        data,
        tx: {
          id: tx.id, accountId: tx.accountId,
          accountName: allAccounts.find(a => a.id === tx.accountId)?.name ?? account?.name ?? '',
          date: tx.date, payee: tx.payee?.name ?? null, amount: tx.amount,
        },
      });
    } catch {
      setError('Failed to load IOUs.');
    }
  };

  const renderTxRow = (tx: Transaction) => (
    <tr
      key={tx.id}
      ref={el => { if (el) rowRefs.current.set(tx.id, el); else rowRefs.current.delete(tx.id); }}
      className={`${styles.txRow} ${styles.txRowClickable} ${(tx.postDate ?? tx.date) > today ? styles.txRowFuture : ''} ${highlightTxId === tx.id ? styles.txRowHighlight : ''} ${editingTx?.id === tx.id ? styles.txRowSelected : ''} ${tx.isVoided ? styles.txRowVoided : ''}`}
      onClick={() => handleEdit(tx)}
      onContextMenu={e => { e.preventDefault(); setContextMenu({ x: e.clientX, y: e.clientY, tx }); }}
    >
      <td className={styles.selectCol} onClick={e => e.stopPropagation()}>
        <input
          type="checkbox"
          aria-label={`Select ${formatDate(tx.date)} ${tx.payee?.name ?? ''} ${tx.amount}`}
          checked={selectedIds.has(tx.id)}
          onChange={() => {}}
          onClick={e => toggleSelected(tx.id, e.shiftKey)}
        />
      </td>
      <td>
        {tx.isVoided && <span className={styles.voidBadge} title="Voided — excluded from the balance">VOID</span>}
        {tx.scheduledTransactionId != null && (
          <span className={styles.recurringIcon} title="Created from a recurring schedule">↻</span>
        )}
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
          : (tx.payee?.name ?? (
            <span className={styles.categoryMissing} title="No payee — click the row to set one">Missing</span>
          ))}
        <IouBadgeChip badge={iouBadges.get(tx.id)} onOpen={() => navigate('/ious')} />
      </td>
      <td>
        {getCategoryLabel(tx, categories) === 'Transfer' && !tx.categoryId
          ? <span className={styles.transferLabel}>Transfer</span>
          : getCategoryLabel(tx, categories) || (
          <span className={styles.categoryMissing} title="No category — click the row to set one, or bulk-edit several">Missing</span>
        )}
      </td>
      <td className={styles.colMemo}>{tx.memo ?? ''}</td>
      <td className={`${styles.right} ${tx.amount < 0 ? styles.debit : styles.credit}`}>
        {formatCurrency(tx.amount)}
      </td>
      <td className={`${styles.right} ${(tx.runningBalance ?? 0) < 0 ? styles.debit : ''}`}>
        {tx.runningBalance != null ? formatCurrency(tx.runningBalance) : '—'}
      </td>
      <td className={styles.statusCell}>
        <button
          className={styles[`status${tx.status}`]}
          onClick={e => { e.stopPropagation(); handleToggleStatus(tx); }}
          title={`${tx.status} — click to mark ${tx.status === 'Uncleared' ? 'Cleared' : tx.status === 'Cleared' ? 'Reconciled' : 'Uncleared'}`}
          aria-label={`Status: ${tx.status}. Click to mark ${tx.status === 'Uncleared' ? 'Cleared' : tx.status === 'Cleared' ? 'Reconciled' : 'Uncleared'}`}
        >
          <StatusIcon status={tx.status} />
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
              {tx.transferTransactionId && tx.transferAccountId && (
                <button
                  className={styles.dropdownGoto}
                  onClick={e => {
                    e.stopPropagation();
                    const dest = tx.transferAccountId;
                    const destTxId = tx.transferTransactionId;
                    setOpenMenuId(null);
                    navigate(`/accounts/${dest}?tx=${destTxId}`);
                  }}
                >
                  Go to Other Account →
                </button>
              )}
              <button className={styles.dropdownEdit} onClick={e => { e.stopPropagation(); setOpenMenuId(null); handleEdit(tx); }}>Edit</button>
              <button className={styles.dropdownGoto} onClick={e => { e.stopPropagation(); setOpenMenuId(null); openIouForm('tag', tx); }}>
                Split with people (IOU)…
              </button>
              <button className={styles.dropdownGoto} onClick={e => { e.stopPropagation(); setOpenMenuId(null); openIouForm('payment', tx); }}>
                Record as IOU payment…
              </button>
              {!tx.transferTransactionId && (
                <button
                  className={styles.dropdownGoto}
                  onClick={e => { e.stopPropagation(); setOpenMenuId(null); handleDuplicate(tx); }}
                >
                  Duplicate
                </button>
              )}
              <button
                className={styles.dropdownGoto}
                onClick={e => { e.stopPropagation(); setOpenMenuId(null); goToBillsPrefilled(tx); }}
              >
                Make Recurring…
              </button>
              <button
                className={styles.dropdownGoto}
                onClick={e => { e.stopPropagation(); setOpenMenuId(null); handleToggleVoid(tx); }}
              >
                {tx.isVoided ? 'Un-void' : 'Void…'}
              </button>
              <button className={styles.dropdownDelete} onClick={e => { e.stopPropagation(); setOpenMenuId(null); handleDelete(tx.id); }}>Delete</button>
            </div>
          )}
        </div>
      </td>
    </tr>
  );

  // ── Today divider ──
  const todayDivider = (
    <tr className={styles.todayRow} ref={todayRowRef}>
      <td colSpan={9}>
        <div className={styles.todayDivider}>
          <span className={styles.todayLabel}>Today — {formatDate(today)}</span>
        </div>
      </td>
    </tr>
  );

  // ── "Load older" sentinel ──
  // Sits at whichever end of the past-transaction block holds the OLDEST loaded
  // row: the bottom for newest-first display (scroll down for older), the top
  // for oldest-first display (scroll up for older). Only one instance renders,
  // so topSentinelRef always attaches to the live one.
  const loadOlderSentinel = hasMorePast && (
    <tr>
      <td colSpan={9} className={styles.sentinelCell}>
        <div ref={topSentinelRef} className={styles.sentinel} />
        {loadingPast && <span className={styles.loadingMore}>Loading older transactions…</span>}
      </td>
    </tr>
  );

  const pastRows = restTxs.length === 0 && !initialLoading
    ? (futureRealTxs.length === 0 && (
        <tr><td colSpan={9} className={styles.emptyMsg}>No transactions found.</td></tr>
      ))
    : restTxs.map(renderTxRow);

  // ── Merged future items ──
  // Real future-dated transactions (manual/post-dated entries) and scheduled
  // bill occurrences used to render as two entirely separate blocks — a bill
  // due before a manually-entered future transaction would still show below
  // it. Interleave them by date instead, in a single chronological sequence.
  type FutureItem =
    | { kind: 'tx'; date: string; key: string; tx: Transaction }
    | { kind: 'bill'; date: string; key: string; bill: ScheduledTransaction };

  const futureItemsChrono: FutureItem[] = [
    ...futureRealTxsRaw.map(tx => ({ kind: 'tx' as const, date: tx.postDate ?? tx.date, key: `tx-${tx.id}`, tx })),
    ...(showFuture ? futureBills.map(bill => ({
      kind: 'bill' as const, date: bill.nextDueDate, key: `bill-${bill.id}-${bill.nextDueDate}`, bill,
    })) : []),
  ].sort((a, b) => a.date < b.date ? -1 : a.date > b.date ? 1 : 0);

  // Projected balance for bill occurrences: walk forward from the header's
  // "as of today" balance, accumulating amounts in chronological order — the
  // same formula the server uses for real transactions' own runningBalance
  // (used as-is when present), so a projected bill balance lines up exactly
  // with the real transactions around it.
  const billProjectedBalance = new Map<string, number>();
  {
    let running = accountBalance;
    for (const item of futureItemsChrono) {
      if (item.kind === 'tx') {
        running = item.tx.runningBalance ?? running + item.tx.amount;
      } else {
        running += item.bill.amount;
        billProjectedBalance.set(item.key, running);
      }
    }
  }

  const renderBillRow = (bill: ScheduledTransaction, key: string) => {
    const balance = billProjectedBalance.get(key);
    return (
      <tr key={key} className={`${styles.futureTxRow} ${styles.txRowFuture}`}>
        <td className={styles.selectCol}></td>
        <td>
          <span className={styles.recurringIcon} title="Recurring scheduled transaction">↻</span>
          {formatDate(bill.nextDueDate)}
        </td>
        <td>
          {bill.payee?.name
            ?? (bill.transferAccountId
              ? <span className={styles.transferLabel}>Transfer → {allAccounts.find(a => a.id === bill.transferAccountId)?.name ?? 'account'}</span>
              : (bill.name || '—'))}
        </td>
        <td>
          {!bill.categoryId && bill.transferAccountId
            ? <span className={styles.transferLabel}>Transfer</span>
            : getBillCategoryLabel(bill, categories)}
        </td>
        <td className={styles.colMemo}>{bill.memo ?? ''}</td>
        <td className={`${styles.right} ${bill.amount < 0 ? styles.debit : styles.credit}`}>
          {formatCurrency(bill.amount)}
        </td>
        <td className={`${styles.right} ${(balance ?? 0) < 0 ? styles.debit : ''}`}>
          {balance != null ? formatCurrency(balance) : '—'}
        </td>
        <td><span className={styles.statusScheduled}>Scheduled</span></td>
        <td />
      </tr>
    );
  };

  const futureItemsDisplay = isChronologicalAsc ? futureItemsChrono : [...futureItemsChrono].reverse();
  const futureItemRows = futureItemsDisplay.map(item =>
    item.kind === 'tx' ? renderTxRow(item.tx) : renderBillRow(item.bill, item.key)
  );

  // Bills-specific loading/error/empty state and the "load more upcoming"
  // sentinel aren't dated items, so they stay a fixed block at the
  // far-future edge of the merged rows (top in desc display, bottom in
  // asc) rather than interleaved — same edge-of-block placement as the
  // "load older" sentinel on the past side.
  const futureBillsStatus = showFuture && (
    <>
      {futureError && futureBills.length === 0 ? (
        <tr>
          <td colSpan={9} className={styles.noUpcoming}>{futureError}</td>
        </tr>
      ) : loadingFuture && futureBills.length === 0 ? (
        <tr>
          <td colSpan={9} className={styles.loadingMore}>Loading upcoming…</td>
        </tr>
      ) : futureBills.length === 0 ? (
        <tr>
          <td colSpan={9} className={styles.noUpcoming}>No upcoming scheduled transactions.</td>
        </tr>
      ) : null}

      {hasMoreFuture && (
        <tr>
          <td colSpan={9} className={styles.sentinelCell}>
            <div ref={bottomSentinelRef} className={styles.sentinel} />
            {loadingFuture && <span className={styles.loadingMore}>Loading more upcoming…</span>}
          </td>
        </tr>
      )}
    </>
  );

  if (initialLoading && pastTxs.length === 0) return (
    <div className={styles.page}><p className={styles.loadingMsg}>Loading…</p></div>
  );
  if (error) return <div className={styles.page}><p className={styles.errorMsg}>{error}</p></div>;

  return (
    <div className={styles.page}>
      {/* ── Header ── */}
      <div className={styles.pageHeader}>
        <div>
          <select
            className={styles.accountSelect}
            value={accountId}
            onChange={e => {
              // Same unsaved-changes guard as every other form-switching path
              if (!confirmDiscardIfDirty()) { e.target.value = String(accountId); return; }
              navigate(`/accounts/${e.target.value}`);
            }}
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
              {account.balanceAsOf && (
                <> (reported by your bank, {new Date(account.balanceAsOf + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric' })})</>
              )}
            </div>
          )}
        </div>
        <div className={styles.headerActions}>
          <button
            className={styles.btnPrimary}
            onClick={() => {
              const closingNewForm = showForm && !editingTx;
              if (!closingNewForm && !confirmDiscardIfDirty()) return;
              setEditingTx(null);
              setReceiptPrefill(null);
              setReceiptPayeeName(undefined);
              setReceiptCategoryLabel(undefined);
              setShowForm(s => !s);
            }}
          >
            {showForm && !editingTx ? 'Cancel' : '+ New Transaction'}
          </button>
          <button className={styles.btnSecondary} onClick={() => setShowScanner(true)}>
            📷 Scan Receipt
          </button>
          <label className={styles.jumpToDate} title="Jump to a date in the register">
            <span className={styles.jumpToDateLabel}>Jump to</span>
            <input
              type="date"
              value={jumpDate}
              max={today}
              onChange={e => {
                const d = e.target.value;
                if (d) jumpToDate(d);
                // Reset to empty so picking the SAME date again still fires
                // onChange ('' → date is a change; date → same date is not) —
                // this is an action trigger, not a persistent filter value.
                setJumpDate('');
              }}
            />
          </label>
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
                <p className={styles.settingsHint}>
                  "Days ahead" is set in Settings → Preferences.
                </p>
                <div className={styles.settingsDivider} />
                <button
                  className={styles.settingsAction}
                  onClick={() => { setSettingsOpen(false); setShowEditModal(true); }}
                >
                  Edit Account…
                </button>
                <button
                  className={`${styles.settingsAction} ${styles.settingsActionDanger}`}
                  onClick={() => { setSettingsOpen(false); if (account) requestDelete(account); }}
                  disabled={deletingAccount}
                >
                  {deletingAccount ? 'Deleting…' : 'Delete Account…'}
                </button>
              </div>
            )}
          </div>
        </div>
      </div>

      {account?.type === 'Investment' && <HoldingsPanel accountId={account.id} />}

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
            <div className={styles.filterField}>
              <label>Status</label>
              <select value={filterStatus} onChange={e => setFilterStatus(e.target.value)}>
                <option value="">All</option>
                <option value="Uncleared">Uncleared</option>
                <option value="Cleared">Cleared</option>
                <option value="Reconciled">Reconciled</option>
              </select>
            </div>
            <div className={styles.filterField}>
              <label>Void</label>
              <select value={filterVoided} onChange={e => setFilterVoided(e.target.value)}>
                <option value="">All</option>
                <option value="false">Exclude voided</option>
                <option value="true">Voided only</option>
              </select>
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

      {showScanner && (
        <ReceiptScanner
          onConfirm={handleReceiptConfirm}
          onCancel={() => setShowScanner(false)}
        />
      )}

      {/* ── Bulk edit toolbar ── */}
      {(selectedIds.size > 0 || bulkMessage) && (
        <div className={styles.bulkBar} role="region" aria-label="Bulk edit">
          {selectedIds.size > 0 ? (
            <>
              <strong>{selectedIds.size} selected</strong>
              <span className={styles.bulkLabel}>Set category:</span>
              <div className={styles.bulkCategory}>
                <CategoryAutocomplete
                  value={bulkCategory}
                  onChange={setBulkCategory}
                  categories={categories}
                  placeholder="e.g. Food or Food: Groceries"
                />
              </div>
              <button className={styles.btnPrimary} onClick={applyBulkCategory} disabled={bulkBusy || !bulkCategory.trim()}>
                {bulkBusy ? 'Applying…' : 'Apply'}
              </button>
              <button className={styles.btnSecondary} onClick={() => { setSelectedIds(new Set()); setBulkMessage(''); }} disabled={bulkBusy}>
                Clear selection
              </button>
            </>
          ) : null}
          {bulkMessage && <span className={styles.bulkMessage} role="status">{bulkMessage}</span>}
        </div>
      )}

      {/* ── Register table ── */}
      <div className={styles.tableWrapper} ref={tableWrapperRef}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th className={styles.selectCol}>
                <input
                  type="checkbox"
                  aria-label="Select all loaded transactions"
                  checked={allLoadedSelected}
                  ref={el => { if (el) el.indeterminate = selectedIds.size > 0 && !allLoadedSelected; }}
                  onChange={() => setSelectedIds(allLoadedSelected ? new Set() : new Set(selectableIds))}
                />
              </th>
              {(['date','payee','category','memo'] as const).map(col => (
                <th
                  key={col}
                  className={`${styles.sortable} ${col === 'memo' ? styles.colMemo : ''}`}
                  onClick={() => handleSort(col)}
                >
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
            {/* Two mirror-image layouts. Newest-first (desc, default): future/today
                at the top, newest→oldest past below, "load older" sentinel at the
                bottom. Oldest-first (asc): "load older" sentinel at the top,
                oldest→newest past below, today/future at the bottom. Either way the
                loaded window is the transactions nearest today (fetch is always
                newest-first), so the block adjacent to the Today divider is the
                recent activity and the open-to-Today scroll lands correctly. */}
            {isChronologicalAsc ? (
              <>
                {loadOlderSentinel}
                {pastRows}
                {todayDivider}
                {futureItemRows}
                {futureBillsStatus}
              </>
            ) : (
              <>
                {futureBillsStatus}
                {futureItemRows}
                {todayDivider}
                {pastRows}
                {loadOlderSentinel}
              </>
            )}
          </tbody>
        </table>
      </div>

      {/* ── Transaction entry/edit form (below the register) ── */}
      {showForm && (
        <div ref={formRef}>
        <TransactionForm
          offerIouSplit
          key={editingTx?.id ?? `new-${formNonce}`}
          accountId={accountId}
          accounts={allAccounts}
          initial={editingTx ?? receiptPrefill ?? { date: lastUsedDate }}
          initialPayeeName={editingTx ? undefined : receiptPayeeName}
          initialCategoryLabel={editingTx ? undefined : receiptCategoryLabel}
          onSave={handleSaveTx}
          onCancel={() => { setShowForm(false); setEditingTx(null); setReceiptPrefill(null); setReceiptPayeeName(undefined); setReceiptCategoryLabel(undefined); }}
          onDirtyChange={setFormDirty}
        />
        </div>
      )}

      {iouForm?.kind === 'tag' && (
        <IouDialog
          people={iouForm.data.people}
          transaction={iouForm.tx}
          onSaved={() => { setIouForm(null); setIouBadgesVersion(v => v + 1); }}
          onClose={() => setIouForm(null)}
        />
      )}
      {iouForm?.kind === 'payment' && (
        <IouPaymentDialog
          people={iouForm.data.people}
          openEntries={iouForm.data.entries}
          transaction={iouForm.tx}
          onSaved={() => { setIouForm(null); setIouBadgesVersion(v => v + 1); }}
          onClose={() => setIouForm(null)}
        />
      )}

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
          <button className={styles.dropdownGoto} onClick={() => { const tx = contextMenu.tx; setContextMenu(null); openIouForm('tag', tx); }}>
            Split with people (IOU)…
          </button>
          <button className={styles.dropdownGoto} onClick={() => { const tx = contextMenu.tx; setContextMenu(null); openIouForm('payment', tx); }}>
            Record as IOU payment…
          </button>
          {!contextMenu.tx.transferTransactionId && (
            <button
              className={styles.dropdownGoto}
              onClick={() => { const tx = contextMenu.tx; setContextMenu(null); handleDuplicate(tx); }}
            >
              Duplicate
            </button>
          )}
          <button
            className={styles.dropdownGoto}
            onClick={() => { const tx = contextMenu.tx; setContextMenu(null); goToBillsPrefilled(tx); }}
          >
            Make Recurring…
          </button>
          <button
            className={styles.dropdownGoto}
            onClick={() => { const tx = contextMenu.tx; setContextMenu(null); handleToggleVoid(tx); }}
          >
            {contextMenu.tx.isVoided ? 'Un-void' : 'Void…'}
          </button>
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

      {deleteAccountError && <div className={styles.errorMsg}>{deleteAccountError}</div>}
      {deleteAccountModal}
    </div>
  );
}

function getCategoryLabel(tx: Transaction, categories: Category[]): string {
  if (tx.splits && tx.splits.length > 0) return `Split (${tx.splits.length})`;
  // A transfer with no category of its own reads as "Transfer".
  if (!tx.categoryId) return tx.transferAccountId ? 'Transfer' : '';
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

// Same "Parent:Sub" resolution as getCategoryLabel, but for a scheduled
// transaction (which has no splits).
function getBillCategoryLabel(bill: ScheduledTransaction, categories: Category[]): string {
  if (!bill.categoryId) return bill.transferAccountId ? 'Transfer' : '';
  for (const cat of categories) {
    if (cat.id === bill.categoryId) return cat.name;
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        if (sub.id === bill.categoryId) return `${cat.name}:${sub.name}`;
      }
    }
  }
  return bill.category?.name ?? '';
}
