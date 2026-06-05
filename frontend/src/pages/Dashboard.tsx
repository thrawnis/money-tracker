import styles from './Dashboard.module.css';

export default function Dashboard() {
  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h2 className={styles.title}>Welcome to Money Tracker</h2>
      </div>
      <p className={styles.hint}>Select an account from the sidebar to view its register.</p>
    </div>
  );
}
