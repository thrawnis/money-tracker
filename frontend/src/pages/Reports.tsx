import styles from './Reports.module.css';

export default function Reports() {
  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h2 className={styles.title}>Reports</h2>
      </div>
      <p className={styles.placeholder}>Spending, cash flow, and net worth reports coming soon.</p>
    </div>
  );
}
