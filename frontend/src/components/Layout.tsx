import { NavLink, Outlet } from 'react-router-dom';
import type { Account } from '../types';
import styles from './Layout.module.css';

interface Props {
  accounts: Account[];
}

export default function Layout({ accounts }: Props) {
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
      </nav>

      <main className={styles.content}>
        <Outlet />
      </main>
    </div>
  );
}
