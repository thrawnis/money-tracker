import { useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import type { Account } from '../types';
import { useAuth } from '../contexts/AuthContext';
import ExportModal from './ExportModal';
import styles from './Layout.module.css';

interface Props {
  accounts: Account[];
  onLogout: () => void;
}

export default function Layout({ accounts, onLogout }: Props) {
  const { user } = useAuth();
  const [showExport, setShowExport] = useState(false);

  return (
    <div className={styles.shell}>
      <nav className={styles.sidebar}>
        <div className={styles.logo}>Money Tracker</div>

        <section className={styles.navSection}>
          <div className={styles.navHeader}>Accounts</div>
          {accounts.map(account => (
            <NavLink
              key={account.id}
              to={`/accounts/${account.id}`}
              className={({ isActive }) =>
                `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
              }
            >
              <span className={styles.accountName}>{account.name}</span>
            </NavLink>
          ))}
          <NavLink
            to="/accounts/new"
            className={({ isActive }) =>
              `${styles.navItem} ${styles.navItemAdd} ${isActive ? styles.navItemActive : ''}`
            }
          >
            + Add Account
          </NavLink>
        </section>

        <section className={styles.navSection}>
          <div className={styles.navHeader}>Tools</div>
          <NavLink
            to="/bills"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
          >
            Bills &amp; Reminders
          </NavLink>
          <NavLink
            to="/reports"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
          >
            Reports
          </NavLink>
          <NavLink
            to="/import"
            className={({ isActive }) =>
              `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
            }
          >
            Import
          </NavLink>
        </section>

        <div className={styles.sidebarFooter}>
          {user && <div className={styles.userEmail}>{user.email}</div>}
          <button className={styles.exportBtn} onClick={() => setShowExport(true)}>
            Export Data
          </button>
          <button className={styles.logoutBtn} onClick={onLogout}>
            Sign Out
          </button>
        </div>
      </nav>

      <main className={styles.content}>
        <Outlet />
      </main>

      {showExport && <ExportModal onClose={() => setShowExport(false)} />}
    </div>
  );
}
