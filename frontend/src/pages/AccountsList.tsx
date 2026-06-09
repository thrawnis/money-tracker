import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { getAccounts } from '../api/accounts';
import type { Account, AccountType } from '../types';
import styles from './AccountsList.module.css';

function formatCurrency(n: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
}

function formatDate(d: string) {
  return new Date(d + 'T00:00:00').toLocaleDateString('en-US', {
    month: 'short', day: 'numeric', year: 'numeric',
  });
}

const TYPE_ORDER: AccountType[] = [
  'Checking', 'Savings', 'CreditCard', 'Loan', 'Investment', 'Cash', 'Other',
];

const TYPE_LABELS: Record<AccountType, string> = {
  Checking:   'Checking',
  Savings:    'Savings',
  CreditCard: 'Credit Cards',
  Loan:       'Loans',
  Investment: 'Investments',
  Cash:       'Cash',
  Other:      'Other',
};

export default function AccountsList() {
  usePageTitle('Accounts');
  const navigate = useNavigate();
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showInactive, setShowInactive] = useState(false);

  useEffect(() => {
    getAccounts(true)
      .then(setAccounts)
      .catch(() => setError('Failed to load accounts.'))
      .finally(() => setLoading(false));
  }, []);

  const active   = accounts.filter(a =>  a.isActive);
  const inactive = accounts.filter(a => !a.isActive).sort((a, b) => a.name.localeCompare(b.name));

  // Group active accounts by type, preserving TYPE_ORDER
  const groups = TYPE_ORDER
    .map(type => ({
      type,
      label: TYPE_LABELS[type],
      items: active.filter(a => a.type === type).sort((a, b) => a.name.localeCompare(b.name)),
    }))
    .filter(g => g.items.length > 0);

  const showTypeHeaders = groups.length > 1;

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>;
  if (error)   return <div className={styles.page}><p className={styles.errorMsg}>{error}</p></div>;

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Accounts</h2>
        <button className={styles.btnPrimary} onClick={() => navigate('/accounts/new')}>
          + New Account
        </button>
      </div>

      {groups.length === 0 ? (
        <p className={styles.empty}>No active accounts. <button className={styles.btnLink} onClick={() => navigate('/accounts/new')}>Add one</button></p>
      ) : (
        <table className={styles.table}>
          <thead>
            <tr>
              <th className={styles.thName}>Account</th>
              <th className={styles.thInst}>Institution</th>
              <th className={styles.thDate}>Last Transaction</th>
              <th className={styles.thBal}>Balance</th>
            </tr>
          </thead>
          <tbody>
            {groups.map(({ type, label, items }) => (
              <>
                {showTypeHeaders && (
                  <tr key={`hdr-${type}`}>
                    <td colSpan={4} className={styles.groupHeaderCell}>{label}</td>
                  </tr>
                )}
                {items.map(acc => (
                  <AccountRow key={acc.id} acc={acc} onClick={() => navigate(`/accounts/${acc.id}`)} />
                ))}
              </>
            ))}
          </tbody>
        </table>
      )}

      {inactive.length > 0 && (
        <div className={styles.inactiveSection}>
          <button
            className={styles.inactiveToggle}
            onClick={() => setShowInactive(s => !s)}
          >
            {showInactive ? '▾' : '▸'} Inactive Accounts ({inactive.length})
          </button>
          {showInactive && (
            <table className={styles.table}>
              <thead>
                <tr>
                  <th className={styles.thName}>Account</th>
                  <th className={styles.thInst}>Institution</th>
                  <th className={styles.thDate}>Last Transaction</th>
                  <th className={styles.thBal}>Balance</th>
                </tr>
              </thead>
              <tbody>
                {inactive.map(acc => (
                  <AccountRow key={acc.id} acc={acc} onClick={() => navigate(`/accounts/${acc.id}`)} inactive />
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </div>
  );
}

function AccountRow({ acc, onClick, inactive = false }: { acc: Account; onClick: () => void; inactive?: boolean }) {
  return (
    <tr
      className={`${styles.row} ${inactive ? styles.rowInactive : ''}`}
      onClick={onClick}
    >
      <td className={styles.tdName}>{acc.name}</td>
      <td className={styles.tdInst}>{acc.institution?.name ?? <span className={styles.none}>—</span>}</td>
      <td className={styles.tdDate}>
        {acc.lastTransactionDate
          ? formatDate(acc.lastTransactionDate)
          : <span className={styles.none}>—</span>}
      </td>
      <td className={`${styles.tdBal} ${(acc.currentBalance ?? 0) < 0 ? styles.negative : ''}`}>
        {formatCurrency(acc.currentBalance ?? acc.openingBalance)}
      </td>
    </tr>
  );
}
