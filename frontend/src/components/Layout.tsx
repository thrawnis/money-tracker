import { useEffect, useState } from 'react';
import { NavLink, Outlet } from 'react-router-dom';
import { useAuth } from '../contexts/AuthContext';
import ImportDraftBanner from './ImportDraftBanner';
import KeyboardShortcutsModal from './KeyboardShortcutsModal';
import styles from './Layout.module.css';

interface Props {
  onLogout: () => void;
}

export default function Layout({ onLogout }: Props) {
  const { user } = useAuth();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [showShortcuts, setShowShortcuts] = useState(false);

  const closeSidebar = () => setSidebarOpen(false);

  // "?" opens the shortcuts list from anywhere, except while typing in a
  // field (where Shift+/ should just type a literal "?") or another modal
  // that has its own key handling is already open.
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key !== '?') return;
      const tag = (e.target as HTMLElement).tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      e.preventDefault();
      setShowShortcuts(true);
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, []);

  const navItem = ({ isActive }: { isActive: boolean }) =>
    `${styles.navItem} ${isActive ? styles.navItemActive : ''}`;

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
          <NavLink to="/accounts" className={navItem} onClick={closeSidebar}>
            Accounts
          </NavLink>
          <NavLink to="/all-transactions" className={navItem} onClick={closeSidebar}>
            Transactions
          </NavLink>
          <NavLink to="/bills" className={navItem} onClick={closeSidebar}>
            Bills &amp; Reminders
          </NavLink>
          <NavLink to="/payees" className={navItem} onClick={closeSidebar}>
            Payees
          </NavLink>
          <NavLink to="/categories" className={navItem} onClick={closeSidebar}>
            Categories
          </NavLink>
          <NavLink to="/reports" className={navItem} onClick={closeSidebar}>
            Reports
          </NavLink>
          <NavLink to="/utilities" className={navItem} onClick={closeSidebar}>
            Utilities
          </NavLink>
          <NavLink to="/settings" className={navItem} onClick={closeSidebar}>
            Settings
          </NavLink>
        </section>

        <div className={styles.sidebarFooter}>
          {user && <div className={styles.userEmail}>{user.email}</div>}
          <button
            className={styles.shortcutsBtn}
            onClick={() => { setShowShortcuts(true); closeSidebar(); }}
          >
            Keyboard Shortcuts
          </button>
          <button className={styles.logoutBtn} onClick={onLogout}>
            Sign Out
          </button>
        </div>
      </nav>

      <main className={styles.content}>
        <ImportDraftBanner />
        <Outlet />
      </main>

      {showShortcuts && <KeyboardShortcutsModal onClose={() => setShowShortcuts(false)} />}
    </div>
  );
}
