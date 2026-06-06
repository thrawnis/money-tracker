import { useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import type { Account } from '../types';
import { useAuth } from '../contexts/AuthContext';
import styles from './Layout.module.css';

interface Props {
  accounts: Account[];
  onLogout: () => void;
}

export default function Layout({ accounts, onLogout }: Props) {
  const { user } = useAuth();
  const [sidebarOpen, setSidebarOpen] = useState(false);

  const closeSidebar = () => setSidebarOpen(false);

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
          {accounts.map(account => (
            <NavLink
              key={account.id}
              to={`/accounts/${account.id}`}
              className={({ isActive }) =>
                `${styles.navItem} ${isActive ? styles.navItemActive : ''}`
              }
              onClick={closeSidebar}
            >
              <span className={styles.accountName}>{account.name}</span>
            </NavLink>
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
        </section>

        <section className={styles.navSection}>
          <div className={styles.navHeader}>Tools</div>
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
