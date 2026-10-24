import BalanceHistoryReport from '../components/BalanceHistoryReport';
import { getIous, type IouPage } from '../api/ious';
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { getDashboard, type DashboardData, type DashboardAccount } from '../api/dashboard';
import { usePageTitle } from '../hooks/usePageTitle';
import { ACCOUNT_TYPE_GROUP_LABELS } from '../utils/accountTypes';
import type { AccountType } from '../types';
import styles from './Dashboard.module.css';

const ACCOUNT_TYPE_ORDER = ['Checking', 'Savings', 'CreditCard', 'Cash', 'Investment', 'Loan', 'Other'];

const TYPE_COLORS: Record<string, string> = {
  Checking: '#003366',
  Savings: '#006666',
  CreditCard: '#cc0000',
  Cash: '#006600',
  Investment: '#996600',
  Loan: '#660066',
  Other: '#666666',
};

function formatCurrency(amount: number) {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(amount);
}

function formatDate(dateStr: string) {
  return new Date(dateStr + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

function PieChart({ accounts }: { accounts: DashboardAccount[] }) {
  const typeBalances: Record<string, number> = {};
  for (const acc of accounts) {
    const balance = Math.abs(acc.currentBalance);
    typeBalances[acc.type] = (typeBalances[acc.type] ?? 0) + balance;
  }
  const entries = Object.entries(typeBalances).filter(([, v]) => v > 0);
  const total = entries.reduce((s, [, v]) => s + v, 0);
  if (total === 0 || entries.length === 0) return null;

  const SIZE = 160;
  const CX = SIZE / 2;
  const CY = SIZE / 2;
  const R = 65;

  let startAngle = -Math.PI / 2;
  const slices = entries.map(([type, value]) => {
    const pct = value / total;
    const angle = pct * 2 * Math.PI;
    const endAngle = startAngle + angle;
    const x1 = CX + R * Math.cos(startAngle);
    const y1 = CY + R * Math.sin(startAngle);
    const x2 = CX + R * Math.cos(endAngle);
    const y2 = CY + R * Math.sin(endAngle);
    const largeArc = angle > Math.PI ? 1 : 0;
    const d = `M ${CX} ${CY} L ${x1} ${y1} A ${R} ${R} 0 ${largeArc} 1 ${x2} ${y2} Z`;
    const slice = { type, value, pct, d, color: TYPE_COLORS[type] ?? '#999' };
    startAngle = endAngle;
    return slice;
  });

  return (
    <div className={styles.pieSection}>
      <h3 className={styles.sectionTitle}>Balance by Account Type</h3>
      <div className={styles.pieWrapper}>
        <svg width={SIZE} height={SIZE} viewBox={`0 0 ${SIZE} ${SIZE}`}>
          {slices.map(s => (
            <path key={s.type} d={s.d} fill={s.color} stroke="#fff" strokeWidth={1} />
          ))}
        </svg>
        <ul className={styles.pieLegend}>
          {slices.map(s => (
            <li key={s.type} className={styles.pieLegendItem}>
              <span className={styles.pieDot} style={{ background: s.color }} />
              <span className={styles.pieLegendType}>{ACCOUNT_TYPE_GROUP_LABELS[s.type as AccountType] ?? s.type}</span>
              <span className={styles.pieLegendPct}>{(s.pct * 100).toFixed(1)}%</span>
            </li>
          ))}
        </ul>
      </div>
    </div>
  );
}

/**
 * At-a-glance IOUs: what's owed each way, the net, and the oldest unpaid.
 * Follows the Dashboard's hide-balances toggle.
 */
function IousTile({ data, showBalances }: { data: IouPage; showBalances: boolean }) {
  const money = (n: number) => (showBalances ? n.toLocaleString('en-US', { style: 'currency', currency: 'USD' }) : '••••••');
  const { owedToMe, iOwe, peopleOwingMe, peopleIOwe } = data.totals;
  const net = owedToMe + iOwe;
  const oldest = data.people
    .filter(p => p.balance > 0 && p.oldestOpen)
    .sort((a, b) => (a.oldestOpen! < b.oldestOpen! ? -1 : 1))[0];
  const daysAgo = (d: string) =>
    Math.max(0, Math.floor((Date.parse(new Date().toLocaleDateString('en-CA')) - Date.parse(d)) / 864e5));
  const plural = (n: number) => `${n} ${n === 1 ? 'person' : 'people'}`;
  return (
    <div className={styles.billsSection}>
      <h3 className={styles.sectionTitle}><Link to="/ious">IOUs</Link></h3>
      {data.summary.length === 0 ? (
        <p className={styles.iouLine}>All square — nobody owes anything right now.</p>
      ) : (
        <>
          <p className={styles.iouLine}>
            <span>Owed to you</span>
            <span><strong className={owedToMe > 0 ? styles.positive : undefined}>{money(owedToMe)}</strong> <span className={styles.iouMuted}>({plural(peopleOwingMe)})</span></span>
          </p>
          <p className={styles.iouLine}>
            <span>You owe</span>
            <span><strong className={iOwe < 0 ? styles.negative : undefined}>{money(Math.abs(iOwe))}</strong> <span className={styles.iouMuted}>({plural(peopleIOwe)})</span></span>
          </p>
          <p className={`${styles.iouLine} ${styles.iouNet}`}>
            <span>Net</span>
            <strong className={net >= 0 ? styles.positive : styles.negative}>{showBalances ? (net >= 0 ? '+' : '−') : ''}{money(Math.abs(net))}</strong>
          </p>
          {oldest && (
            <p className={styles.iouMuted}>
              Oldest unpaid: {oldest.name} · {money(oldest.balance)} · {daysAgo(oldest.oldestOpen!)} days
            </p>
          )}
        </>
      )}
      <p className={styles.iouLink}><Link to="/ious">View IOUs →</Link></p>
    </div>
  );
}

export default function Dashboard() {
  usePageTitle('Dashboard');
  const [data, setData] = useState<DashboardData | null>(null);
  // Privacy toggle for the account balances below — defaults to hidden every
  // time the Dashboard loads (not persisted), so a screen share or a glance
  // over your shoulder doesn't default to showing dollar figures.
  const [showBalances, setShowBalances] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [ious, setIous] = useState<IouPage | null>(null);

  useEffect(() => {
    getDashboard()
      .then(setData)
      .catch(() => setError('Failed to load dashboard data.'))
      .finally(() => setLoading(false));
    // The IOUs tile is optional: if it fails, the rest of the Dashboard stands.
    getIous().then(setIous).catch(() => {});
  }, []);

  if (loading) return <div className={styles.page}><p className={styles.loading}>Loading…</p></div>;
  if (error) return <div className={styles.page}><p className={styles.errorMsg}>{error}</p></div>;
  if (!data) return null;

  const byType: Record<string, DashboardAccount[]> = {};
  for (const acc of data.accounts) {
    if (!byType[acc.type]) byType[acc.type] = [];
    byType[acc.type].push(acc);
  }
  for (const key of Object.keys(byType)) {
    byType[key].sort((a, b) => a.name.localeCompare(b.name));
  }
  const sortedTypes = ACCOUNT_TYPE_ORDER.filter(t => byType[t]);

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>Dashboard</h2>
      </div>

      <div className={styles.grid}>
        {/* Left: Accounts */}
        <div className={styles.accountsPanel}>
          <h3 className={styles.sectionTitle}>
            Accounts
            <button
              type="button"
              className={styles.balanceToggle}
              onClick={() => setShowBalances(v => !v)}
              title={showBalances ? 'Hide balances' : 'Show balances'}
              aria-label={showBalances ? 'Hide balances' : 'Show balances'}
            >
              {showBalances ? '🙈 Hide' : '👁 Show'} balances
            </button>
          </h3>
          {sortedTypes.map(type => (
            <div key={type} className={styles.accountGroup}>
              <div className={styles.accountGroupHeader}>{ACCOUNT_TYPE_GROUP_LABELS[type as AccountType] ?? type}</div>
              {byType[type].map(acc => (
                <Link key={acc.id} to={`/accounts/${acc.id}`} className={styles.accountRow}>
                  <span className={styles.accountRowName}>{acc.name}</span>
                  <span className={`${styles.accountRowBalance} ${showBalances && acc.currentBalance < 0 ? styles.negative : ''}`}>
                    {showBalances ? formatCurrency(acc.currentBalance) : '••••••'}
                  </span>
                </Link>
              ))}
              <div className={styles.accountGroupTotal}>
                Total: {showBalances
                  ? formatCurrency(byType[type].reduce((s, a) => s + a.currentBalance, 0))
                  : '••••••'}
              </div>
            </div>
          ))}
          <PieChart accounts={data.accounts} />
        </div>

        {/* Right: Net worth + Bills + Uncategorized */}
        <div className={styles.rightPanel}>
          <div className={styles.billsSection}>
            <h3 className={styles.sectionTitle}>
              <Link to="/reports" state={{ report: 'balances' }}>Net Worth</Link>
            </h3>
            {/* Follows the balance toggle: hidden balances stay hidden here too. */}
            {showBalances
              ? <BalanceHistoryReport compact defaultRange="1y" />
              : <p style={{ fontSize: 13, opacity: 0.75, margin: 0 }}>Show balances to see your net worth over time.</p>}
          </div>
          {ious && ious.people.length > 0 && <IousTile data={ious} showBalances={showBalances} />}

          <div className={styles.billsSection}>
            <h3 className={styles.sectionTitle}>Upcoming Bills (Next 14 Days)</h3>
            {data.upcomingBills.length === 0 ? (
              <p className={styles.emptyMsg}>No upcoming bills.</p>
            ) : (
              <table className={styles.billsTable}>
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Payee</th>
                    <th>Amount</th>
                    <th>Due Date</th>
                    <th>Days</th>
                  </tr>
                </thead>
                <tbody>
                  {data.upcomingBills.map(bill => (
                    <tr key={bill.id} className={bill.isOverdue ? styles.overdue : ''}>
                      <td>{bill.name}</td>
                      <td>{bill.payeeName ?? '—'}</td>
                      <td className={styles.amount}>{formatCurrency(bill.amount)}</td>
                      <td>{formatDate(bill.nextDueDate)}</td>
                      <td>{bill.isOverdue ? 'Overdue' : `${bill.daysUntilDue}d`}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          {data.uncategorizedCount > 0 && (
            <div className={styles.uncatSection}>
              <h3 className={styles.sectionTitle}>
                <Link to="/all-transactions?uncategorized=true" className={styles.uncatTitleLink}>
                  Uncategorized Transactions
                  <span className={styles.uncatBadge}>{data.uncategorizedCount}</span>
                </Link>
              </h3>
              <p className={styles.uncatHint}>
                {data.uncategorizedCount} transaction{data.uncategorizedCount !== 1 ? 's' : ''} need categorization.
              </p>
              <table className={styles.uncatTable}>
                <thead>
                  <tr>
                    <th>Date</th>
                    <th>Account</th>
                    <th>Payee</th>
                    <th>Amount</th>
                  </tr>
                </thead>
                <tbody>
                  {data.uncategorizedSamples.map(tx => (
                    <tr key={tx.id}>
                      <td>{formatDate(tx.date)}</td>
                      <td>
                        <Link to={`/accounts/${tx.accountId}`} className={styles.txLink}>
                          {tx.accountName}
                        </Link>
                      </td>
                      <td>{tx.payee ?? '—'}</td>
                      <td className={`${styles.amount} ${tx.amount < 0 ? styles.negative : ''}`}>
                        {formatCurrency(tx.amount)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
