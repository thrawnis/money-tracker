import { useEffect, useState, useCallback } from 'react';
import { useParams } from 'react-router-dom';
import { getAccount } from '../api/accounts';
import { getTransactions, createTransaction, updateTransaction, deleteTransaction } from '../api/transactions';
import { getCategories } from '../api/categories';
import type { Account, Transaction, TransactionPage, Category } from '../types';
import TransactionForm from '../components/TransactionForm';
import styles from './AccountRegister.module.css';

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

const PAGE_SIZE = 50;

export default function AccountRegister() {
  const { id } = useParams<{ id: string }>();
  const accountId = Number(id);

  const [account, setAccount] = useState<Account | null>(null);
  const [txPage, setTxPage] = useState<TransactionPage | null>(null);
  const [categories, setCategories] = useState<Category[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [page, setPage] = useState(1);

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

  const fetchData = useCallback(async (pg = page) => {
    if (!accountId) return;
    setLoading(true);
    setError('');
    try {
      const [acc, txs, cats] = await Promise.all([
        getAccount(accountId),
        getTransactions(accountId, {
          from: filterFrom || undefined,
          to: filterTo || undefined,
          minAmount: filterMinAmount ? Number(filterMinAmount) : undefined,
          maxAmount: filterMaxAmount ? Number(filterMaxAmount) : undefined,
          payeeName: filterPayee || undefined,
          categoryId: filterCategoryId ? Number(filterCategoryId) : undefined,
          memo: filterMemo || undefined,
          uncategorizedOnly: filterUncategorized || undefined,
          page: pg,
          pageSize: PAGE_SIZE,
        }),
        getCategories(),
      ]);
      setAccount(acc);
      setTxPage(txs);
      setCategories(cats);
    } catch {
      setError('Failed to load account data.');
    } finally {
      setLoading(false);
    }
  }, [accountId, page, filterFrom, filterTo, filterMinAmount, filterMaxAmount, filterPayee, filterCategoryId, filterMemo, filterUncategorized]);

  useEffect(() => {
    fetchData(page);
  }, [fetchData, page]);

  const handleFilter = () => {
    setPage(1);
    fetchData(1);
  };

  const handleSaveTx = async (data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt'>) => {
    if (editingTx) {
      await updateTransaction(accountId, editingTx.id, data);
    } else {
      await createTransaction(accountId, data);
    }
    setShowForm(false);
    setEditingTx(null);
    fetchData(page);
  };

  const handleDelete = async (txId: number) => {
    if (!confirm('Delete this transaction?')) return;
    await deleteTransaction(accountId, txId);
    fetchData(page);
  };

  const handleEdit = (tx: Transaction) => {
    setEditingTx(tx);
    setShowForm(true);
  };

  // Running balance
  const transactions = txPage?.items ?? [];
  const openingBalance = account?.openingBalance ?? 0;
  const runningBalances: number[] = [];
  let running = openingBalance;
  for (const tx of transactions) {
    running += tx.amount;
    runningBalances.push(running);
  }

  const allCategories: { id: number; label: string }[] = [];
  for (const cat of categories) {
    allCategories.push({ id: cat.id, label: cat.name });
    if (cat.subCategories) {
      for (const sub of cat.subCategories) {
        allCategories.push({ id: sub.id, label: `${cat.name} : ${sub.name}` });
      }
    }
  }

  const totalPages = txPage ? Math.ceil(txPage.total / PAGE_SIZE) : 1;

  if (!account && loading) return <div className={styles.page}><p>Loading…</p></div>;
  if (error) return <div className={styles.page}><p className={styles.errorMsg}>{error}</p></div>;

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{account?.name ?? 'Account Register'}</h2>
          {account && (
            <div className={styles.accountMeta}>
              {account.type} &bull; Balance: <strong>{formatCurrency(runningBalances[runningBalances.length - 1] ?? openingBalance)}</strong>
            </div>
          )}
        </div>
        <div className={styles.headerActions}>
          <button className={styles.btnPrimary} onClick={() => { setEditingTx(null); setShowForm(s => !s); }}>
            {showForm && !editingTx ? 'Cancel' : '+ New Transaction'}
          </button>
          <button className={styles.btnSecondary} onClick={() => setFilterOpen(o => !o)}>
            {filterOpen ? 'Hide Filters' : 'Filters'}
          </button>
        </div>
      </div>

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
            <button className={styles.btnPrimary} onClick={handleFilter}>Apply</button>
            <button className={styles.btnSecondary} onClick={() => {
              setFilterFrom(''); setFilterTo(''); setFilterMinAmount(''); setFilterMaxAmount('');
              setFilterPayee(''); setFilterCategoryId(''); setFilterMemo(''); setFilterUncategorized(false);
              setPage(1); fetchData(1);
            }}>Clear</button>
          </div>
        </div>
      )}

      {showForm && (
        <TransactionForm
          accountId={accountId}
          initial={editingTx ?? undefined}
          onSave={handleSaveTx}
          onCancel={() => { setShowForm(false); setEditingTx(null); }}
        />
      )}

      {loading ? (
        <p className={styles.loadingMsg}>Loading…</p>
      ) : (
        <>
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
              {transactions.length === 0 ? (
                <tr>
                  <td colSpan={8} className={styles.emptyMsg}>No transactions found.</td>
                </tr>
              ) : (
                transactions.map((tx, i) => (
                  <tr key={tx.id} className={styles.txRow}>
                    <td>{formatDate(tx.date)}</td>
                    <td>{tx.payee?.name ?? '—'}</td>
                    <td>{getCategoryLabel(tx, categories)}</td>
                    <td>{tx.memo ?? ''}</td>
                    <td className={`${styles.right} ${tx.amount < 0 ? styles.debit : styles.credit}`}>
                      {formatCurrency(tx.amount)}
                    </td>
                    <td className={`${styles.right} ${runningBalances[i] < 0 ? styles.debit : ''}`}>
                      {formatCurrency(runningBalances[i])}
                    </td>
                    <td><span className={styles[`status${tx.status}`]}>{tx.status}</span></td>
                    <td className={styles.actions}>
                      <button className={styles.btnEdit} onClick={() => handleEdit(tx)}>Edit</button>
                      <button className={styles.btnDelete} onClick={() => handleDelete(tx.id)}>Del</button>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>

          {totalPages > 1 && (
            <div className={styles.pagination}>
              <button disabled={page <= 1} onClick={() => setPage(p => p - 1)} className={styles.btnSecondary}>Prev</button>
              <span>Page {page} of {totalPages}</span>
              <button disabled={page >= totalPages} onClick={() => setPage(p => p + 1)} className={styles.btnSecondary}>Next</button>
            </div>
          )}
        </>
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
