import { useEffect, useState } from 'react';
import { useSearchParams, useNavigate } from 'react-router-dom';
import { searchTransactions, type SearchTransaction } from '../api/transactionSearch';
import { usePageTitle } from '../hooks/usePageTitle';
import styles from './TransactionSearch.module.css';

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}
function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: '2-digit', day: '2-digit', year: 'numeric' });
}

const PAGE_SIZE = 50;

export default function TransactionSearch() {
  usePageTitle('Transaction Search');
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();

  const categoryId = searchParams.get('categoryId') ? Number(searchParams.get('categoryId')) : undefined;
  const payeeId    = searchParams.get('payeeId')    ? Number(searchParams.get('payeeId'))    : undefined;
  const label      = searchParams.get('label') ?? '';

  const [items, setItems] = useState<SearchTransaction[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    setPage(1);
    setItems([]);
  }, [categoryId, payeeId]);

  useEffect(() => {
    if (!categoryId && !payeeId) return;
    setLoading(true);
    searchTransactions({ categoryId, payeeId, page, pageSize: PAGE_SIZE })
      .then(r => {
        setItems(prev => page === 1 ? r.items : [...prev, ...r.items]);
        setTotal(r.total);
      })
      .catch(() => setError('Failed to load transactions.'))
      .finally(() => setLoading(false));
  }, [categoryId, payeeId, page]);

  const hasMore = items.length < total;

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <div>
          <button className={styles.backBtn} onClick={() => navigate(-1)}>← Back</button>
          <h2 className={styles.pageTitle}>
            Transactions{label ? `: ${label}` : ''}
          </h2>
          {total > 0 && <div className={styles.meta}>{total} transaction{total !== 1 ? 's' : ''}</div>}
        </div>
      </div>

      {error && <div className={styles.error}>{error}</div>}

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
            </tr>
          </thead>
          <tbody>
            {items.map(tx => (
              <tr
                key={tx.id}
                className={styles.row}
                onClick={() => navigate(`/accounts/${tx.accountId}`)}
                title="Go to account register"
              >
                <td>{formatDate(tx.date)}</td>
                <td>{tx.accountName}</td>
                <td>{tx.payee ?? '—'}</td>
                <td>{tx.category ?? '—'}</td>
                <td>{tx.memo ?? ''}</td>
                <td className={`${styles.right} ${tx.amount < 0 ? styles.negative : ''}`}>
                  {formatCurrency(tx.amount)}
                </td>
              </tr>
            ))}
            {items.length === 0 && !loading && (
              <tr><td colSpan={6} className={styles.empty}>No transactions found.</td></tr>
            )}
          </tbody>
        </table>
      </div>

      {hasMore && (
        <button className={styles.loadMore} onClick={() => setPage(p => p + 1)} disabled={loading}>
          {loading ? 'Loading…' : `Load more (${total - items.length} remaining)`}
        </button>
      )}
      {loading && items.length === 0 && <div className={styles.loading}>Loading…</div>}
    </div>
  );
}
