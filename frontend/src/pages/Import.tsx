import styles from './Import.module.css';

export default function Import() {
  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h2 className={styles.title}>Import</h2>
      </div>
      <p className={styles.placeholder}>
        QIF, OFX/QFX, CSV, and XLSX import coming soon.
      </p>
    </div>
  );
}
