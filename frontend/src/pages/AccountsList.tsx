import { Fragment, useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import { getAccounts } from '../api/accounts';
import { getSimpleFinStatus, type SimpleFinAccount, type SimpleFinStatus } from '../api/simplefin';
import InfoIcon from '../components/InfoIcon';
import BankSyncSettings from '../components/BankSyncSettings';
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

function ColumnHeaders() {
  return (
    <tr>
      <th className={styles.thName}>Account</th>
      <th className={styles.thInst}>Institution</th>
      {/* Narrow screens: one combined column replaces First/Last (see .wideOnly/.narrowOnly). */}
      <th className={`${styles.thDate} ${styles.narrowOnly}`}>
        Transactions
        <InfoIcon text="Dates of the earliest and latest transactions in this account, including future-dated transactions." />
      </th>
      <th className={`${styles.thDate} ${styles.wideOnly}`}>
        First Transaction
        <InfoIcon text="Date of the earliest transaction ever entered in this account, including future-dated transactions." />
      </th>
      <th className={`${styles.thDate} ${styles.wideOnly}`}>
        Last Transaction
        <InfoIcon text="Date of the latest transaction ever entered in this account, including future-dated transactions." />
      </th>
      <th className={styles.thCount}>
        #<span className={styles.wideOnlyInline}> Transactions</span>
        <InfoIcon text="Total number of transactions in this account, including future-dated transactions." />
      </th>
      <th className={styles.thBal}>
        Balance
        <InfoIcon text="Running balance as of today — opening balance plus all transactions dated today or earlier. Future-dated transactions are not included." />
      </th>
    </tr>
  );
}

export default function AccountsList() {
  usePageTitle('Accounts');
  const navigate = useNavigate();
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [showInactive, setShowInactive] = useState(false);
  const [syncStatus, setSyncStatus] = useState<SimpleFinStatus | null>(null);
  const location = useLocation();
  // A one-off message from the page that sent us here (e.g. the end of the
  // bank sync review queue).
  const notice = (location.state as { notice?: string } | null)?.notice;

  useEffect(() => {
    getAccounts(true)
      .then(setAccounts)
      .catch(() => setError('Failed to load accounts.'))
      .finally(() => setLoading(false));
    getSimpleFinStatus().then(setSyncStatus).catch(() => setSyncStatus({ connected: false }));
  }, []);

  // Links elsewhere point at /accounts#bank-sync.
  useEffect(() => {
    if (location.hash === '#bank-sync' && !loading && syncStatus)
      document.getElementById('bank-sync')?.scrollIntoView({ behavior: 'smooth' });
  }, [location.hash, loading, syncStatus]);

  // Which bank account (if any) feeds each local account.
  const bankLinks = new Map<number, SimpleFinAccount>();
  if (syncStatus?.connected)
    for (const sf of syncStatus.accounts) if (sf.linkedAccountId != null) bankLinks.set(sf.linkedAccountId, sf);


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
      {notice && (
        <div className={styles.notice} role="status">
          ✓ {notice}
        </div>
      )}
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
            <ColumnHeaders />
          </thead>
          <tbody>
            {groups.map(({ type, label, items }) => (
              <Fragment key={type}>
                {showTypeHeaders && (
                  <tr>
                    <td colSpan={7} className={styles.groupHeaderCell}>{label}</td>
                  </tr>
                )}
                {items.map(acc => (
                  <AccountRow key={acc.id} acc={acc} onClick={() => navigate(`/accounts/${acc.id}`)}
                    bankLink={bankLinks.get(acc.id)} />
                ))}
              </Fragment>
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
                <ColumnHeaders />
              </thead>
              <tbody>
                {inactive.map(acc => (
                  <AccountRow key={acc.id} acc={acc} onClick={() => navigate(`/accounts/${acc.id}`)} inactive
                    bankLink={bankLinks.get(acc.id)} />
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      <section id="bank-sync" className={styles.bankSync}>
        <h3 className={styles.sectionTitle}>Bank Sync</h3>
        <BankSyncSettings status={syncStatus} onStatus={setSyncStatus} accounts={accounts} />
      </section>
    </div>
  );
}

interface RowProps {
  acc: Account;
  onClick: () => void;
  inactive?: boolean;
  /** The bank account syncing into this account, if any. */
  bankLink?: SimpleFinAccount;
}

/**
 * Which bank account feeds this one. Larger screens spell it out under the
 * name; small screens show just 🔗, which reveals the same when tapped.
 * Unlinking is done in the Bank Sync section below.
 */
function BankLinkInfo({ sf }: { sf: SimpleFinAccount }) {
  const [open, setOpen] = useState(false);
  const label = `${sf.orgName ? `${sf.orgName} · ` : ''}${sf.name}${sf.balanceOnly ? ' (balance only)' : ''}`;
  return (
    // Clicks here show the link details, not open the register.
    <span onClick={e => e.stopPropagation()}>
      <span className={styles.linkInfo}>
        <span aria-hidden="true">🔗</span> Synced from {label}
      </span>
      <span
        className={styles.linkIconOnly}
        onBlur={e => { if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setOpen(false); }}
      >
        <button
          type="button"
          className={styles.linkBadge}
          title={`Synced from ${label}`}
          aria-label={`Synced from ${label}. Show details`}
          aria-expanded={open}
          onClick={() => setOpen(o => !o)}
        >
          🔗
        </button>
        {open && (
          <span role="tooltip" className={styles.linkPop}>
            Synced from <strong>{label}</strong>
          </span>
        )}
      </span>
    </span>
  );
}

function AccountRow({ acc, onClick, inactive = false, bankLink }: RowProps) {
  return (
    <tr
      className={`${styles.row} ${inactive ? styles.rowInactive : ''}`}
      onClick={onClick}
    >
      <td className={styles.tdName}>
        {acc.name}
        {bankLink && <BankLinkInfo sf={bankLink} />}
      </td>
      <td className={styles.tdInst}>{acc.institution?.name ?? <span className={styles.none}>—</span>}</td>
      <td className={`${styles.tdDate} ${styles.narrowOnly}`}>
        {acc.firstTransactionDate && acc.lastTransactionDate
          ? <>{formatDate(acc.firstTransactionDate)}<br />– {formatDate(acc.lastTransactionDate)}</>
          : <span className={styles.none}>—</span>}
      </td>
      <td className={`${styles.tdDate} ${styles.wideOnly}`}>
        {acc.firstTransactionDate
          ? formatDate(acc.firstTransactionDate)
          : <span className={styles.none}>—</span>}
      </td>
      <td className={`${styles.tdDate} ${styles.wideOnly}`}>
        {acc.lastTransactionDate
          ? formatDate(acc.lastTransactionDate)
          : <span className={styles.none}>—</span>}
      </td>
      <td className={styles.tdCount}>
        {acc.transactionCount ?? 0}
      </td>
      <td className={`${styles.tdBal} ${(acc.currentBalance ?? 0) < 0 ? styles.negative : ''}`}>
        {formatCurrency(acc.currentBalance ?? acc.openingBalance)}
      </td>
    </tr>
  );
}
