import { useState } from 'react';
import { Link } from 'react-router-dom';
import { recentChanges, formatChangeDate, WHATS_NEW_DAYS } from '../whatsNew';
import styles from './WhatsNewBanner.module.css';

const STORAGE_KEY = 'whatsNew.dismissedThrough';
const readDismissed = () => { try { return localStorage.getItem(STORAGE_KEY) ?? ''; } catch { return ''; } };

/**
 * Banner at the top of every page listing the last couple of weeks' changes,
 * by date. Dismissing it hides it until something newer is added (remembered
 * in this browser). Everything older lives on the What's New page.
 */
export default function WhatsNewBanner() {
  const [dismissed, setDismissed] = useState(readDismissed);
  const [expanded, setExpanded] = useState(false);
  const entries = recentChanges();
  if (entries.length === 0 || entries[0].date <= dismissed) return null;
  const newest = entries[0].date;

  const dismiss = () => {
    try { localStorage.setItem(STORAGE_KEY, newest); } catch { /* just hide it for now */ }
    setDismissed(newest);
  };

  // Collapsed: just the days added since you last dismissed it (at most the newest one).
  const fresh = entries.filter(e => e.date > dismissed);
  const shown = expanded ? entries : fresh.slice(0, 1);
  const more = entries.length - shown.length;

  return (
    <section className={styles.banner} aria-label="What's new">
      <div className={styles.head}>
        <span className={styles.title}>✨ What's New</span>
        <span className={styles.spacer} />
        {more > 0 && (
          <button className={styles.linkBtn} onClick={() => setExpanded(true)}>
            Show the last {WHATS_NEW_DAYS} days
          </button>
        )}
        <Link className={styles.linkBtn} to="/whats-new">All changes</Link>
        <button className={styles.close} onClick={dismiss} aria-label="Dismiss What's New" title="Dismiss">×</button>
      </div>
      <div className={expanded ? styles.scroll : undefined}>
        {shown.map(e => (
          <div key={e.date} className={styles.day}>
            <div className={styles.date}>{formatChangeDate(e.date)}</div>
            <ul className={styles.list}>
              {e.items.map(i => <li key={i}>{i}</li>)}
            </ul>
          </div>
        ))}
      </div>
    </section>
  );
}
