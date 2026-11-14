import { useEffect, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import ImportDraftBanner from './ImportDraftBanner';
import KeyboardShortcutsModal from './KeyboardShortcutsModal';
import styles from './Layout.module.css';

interface Props {
  onLogout: () => void;
}

export default function Layout({ onLogout }: Props) {
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [showShortcuts, setShowShortcuts] = useState(false);

  const closeSidebar = () => setSidebarOpen(false);

  // Phones: a Back button in the top bar, for when the app is pinned to the
  // home screen and the browser's own back button isn't there. React Router
  // numbers history entries (state.idx). This layout mounts on the first app
  // page after sign-in (or a reload) and stays mounted while you move around,
  // so Back shows only when there's an earlier *app* page to return to —
  // never back to the sign-in screens. Reading idx during render is enough:
  // useLocation re-renders on every navigation.
  const navigate = useNavigate();
  useLocation();
  const historyIdx = () => (window.history.state as { idx?: number } | null)?.idx ?? 0;
  const [firstAppIdx] = useState(historyIdx);
  const canGoBack = historyIdx() > firstAppIdx;

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
      {/* Phones: a solid bar behind the menu button, so scrolled content passes
          under it instead of showing through around the button. */}
      <div className={`${styles.mobileBar} ${canGoBack ? styles.mobileBarWithBack : ''}`} aria-hidden="true">Money Tracker</div>
      <button
        className={styles.hamburger}
        onClick={() => setSidebarOpen(o => !o)}
        aria-label="Toggle menu"
      >
        ☰
      </button>
      {canGoBack && (
        <button className={styles.backButton} onClick={() => navigate(-1)} aria-label="Back">
          ‹ Back
        </button>
      )}

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
          <NavLink to="/ious" className={navItem} onClick={closeSidebar}>
            IOUs
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
