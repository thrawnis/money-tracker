import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import type { Account, AccountType } from '../types';
import { useAuth } from '../contexts/AuthContext';
import styles from './Layout.module.css';

interface Props {
  accounts: Account[];
  onLogout: () => void;
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

export default function Layout({ accounts, onLogout }: Props) {
  const { user } = useAuth();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const navigate = useNavigate();

  const closeSidebar = () => setSidebarOpen(false);

  // Group active accounts by type, preserving order
  const grouped = TYPE_ORDER
    .map(type => ({ type, items: accounts.filter(a => a.type === type) }))
    .filter(g => g.items.length > 0);

  return (
    <div className={styles.shell}>
      <button
        className={styles.hamburger}
        onClick={() => setSidebarOpen(o => !o)}
        aria-label="Toggle menu"
      >
        ☰
      </button>

      {sidebarOpen && (
        <div className={styles.overlay} onClick={closeSidebar} />
      )}

      <nav className={`${styles.sidebar} ${sidebarOpen ? styles.sidebarOpen : ''}`}>
        <div className={styles.logo}>Money Tracker</div>

        <section className={styles.navSection}>
          <div className={styles.navHeader}>Accounts</div>
          {grouped.map(({ type, items }) => (
            <div key={type} className={styles.typeGroup}>
              {grouped.length > 1 && (
                <div className={styles.typeGroupLabel}>{TYPE_LABELS[type]}</div>
              )}
              {items.map(account => (
                <NavLink
                  key={account.id}
                  to={`/accounts/${account.id}`}
                  className={({ isActive }) =>
                    `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
                  }
                  onClick={closeSidebar}
                  title={
                    account.currentBalance !== undefined
                      ? `${account.name}\nBalance: ${new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(account.currentBalance)}`
                      : account.name
                  }
                >
                  <span className={styles.accountName}>{account.name}</span>
                </NavLink>
              ))}
            </div>
          ))}
          <NavLink
            to="/accounts/new"
            className={({ isActive }) =>
              `${styles.navItem} ${styles.navItemAdd} ${isActive ? styles.navItemActive : ''}`
            }
            onClick={closeSidebar}
          >
            + Add Account
          </NavLink>
          <button
            className={styles.manageAccountsBtn}
            onClick={() => { closeSidebar(); navigate('/settings'); }}
          >
            Manage Accounts
          </button>
        </section>

        <section className={styles.navSection}>
          <div className={styles.navHeader}>Tools</div>
          <NavLink
            to="/all-transactions"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
            onClick={closeSidebar}
          >
            Transactions
          </NavLink>
          <NavLink
            to="/bills"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
            onClick={closeSidebar}
          >
            Bills &amp; Reminders
          </NavLink>
          <NavLink
            to="/payees"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
            onClick={closeSidebar}
          >
            Payees
          </NavLink>
          <NavLink
            to="/categories"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
            onClick={closeSidebar}
          >
            Categories
          </NavLink>
          <NavLink
            to="/reports"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
            onClick={closeSidebar}
          >
            Reports
          </NavLink>
          <NavLink
            to="/settings"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
            onClick={closeSidebar}
          >
            Settings
          </NavLink>
        </section>

        <div className={styles.sidebarFooter}>
          {user && <div className={styles.userEmail}>{user.email}</div>}
          <button className={styles.logoutBtn} onClick={onLogout}>
            Sign Out
          </button>
        </div>
      </nav>

      <main className={styles.content}>
        <Outlet />
      </main>
    </div>
  );
}
