import styles from './BillsReminders.module.css';

export default function BillsReminders() {
  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h2 className={styles.title}>Bills &amp; Reminders</h2>
      </div>
      <p className={styles.placeholder}>Scheduled transactions and reminders coming soon.</p>
    </div>
  );
}
