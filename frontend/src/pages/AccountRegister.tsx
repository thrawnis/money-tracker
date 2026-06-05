import { useParams } from 'react-router-dom';
import styles from './AccountRegister.module.css';

export default function AccountRegister() {
  const { id } = useParams<{ id: string }>();

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h2 className={styles.title}>Account Register</h2>
        <span className={styles.accountId}>Account #{id}</span>
      </div>
      <p className={styles.placeholder}>Transaction register coming soon.</p>
    </div>
  );
}
