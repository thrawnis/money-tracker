import { usePageTitle } from '../hooks/usePageTitle';
import { WHATS_NEW, WHATS_NEW_DAYS, recentChanges, formatChangeDate } from '../whatsNew';
import styles from './WhatsNew.module.css';

/** The full changelog: recent changes first, then everything older by month. */
export default function WhatsNew() {
  usePageTitle('Money Tracker');
  const recent = recentChanges();
  const older = WHATS_NEW.filter(e => !recent.includes(e));
  const months = new Map<string, typeof older>();
  for (const e of older) {
    const m = new Date(e.date + 'T00:00:00').toLocaleDateString('en-US', { month: 'long', year: 'numeric' });
    months.set(m, [...(months.get(m) ?? []), e]);
  }

  const day = (e: (typeof WHATS_NEW)[number], withYear: boolean) => (
    <div key={e.date} className={styles.day}>
      <div className={styles.date}>{formatChangeDate(e.date, withYear)}</div>
      <ul className={styles.list}>{e.items.map(i => <li key={i}>{i}</li>)}</ul>
    </div>
  );

  return (
    <div className={styles.page}>
      <h2 className={styles.pageTitle}>What's New</h2>
      <p className={styles.hint}>New features and changes to how Money Tracker works, newest first.</p>

      <h3 className={styles.section}>Last {WHATS_NEW_DAYS} days</h3>
      {recent.length === 0 ? <p className={styles.hint}>Nothing new in the last {WHATS_NEW_DAYS} days.</p> : recent.map(e => day(e, false))}

      {[...months].map(([m, entries]) => (
        <section key={m}>
          <h3 className={styles.section}>{m}</h3>
          {entries.map(e => day(e, false))}
        </section>
      ))}
    </div>
  );
}
