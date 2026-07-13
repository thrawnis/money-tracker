import { useState, type FormEvent } from 'react';
import api from '../api/client';
import styles from './ExportModal.module.css';

interface Props {
  title?: string;
  hint?: string;
  onVerified: (exportToken: string) => void;
  onClose: () => void;
}

type AuthMethod = 'password' | 'totp';

// Re-authentication prompt shared by anything that needs a fresh password/TOTP
// check before proceeding (Export, and downloading an account backup). Issues
// the same short-lived, single-use exportToken Export uses.
export default function ReauthModal({ title = 'Confirm Identity', hint = 'Confirm your identity to continue.', onVerified, onClose }: Props) {
  const [authMethod, setAuthMethod] = useState<AuthMethod>('password');
  const [password, setPassword] = useState('');
  const [totpCode, setTotpCode] = useState('');
  const [authError, setAuthError] = useState('');
  const [authLoading, setAuthLoading] = useState(false);

  const handleAuth = async (e: FormEvent) => {
    e.preventDefault();
    setAuthError('');
    setAuthLoading(true);
    try {
      const payload = authMethod === 'password' ? { password } : { totpCode };
      const res = await api.post<{ exportToken: string }>('/export/confirm-identity', payload);
      onVerified(res.data.exportToken);
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setAuthError(msg ?? 'Authentication failed.');
    } finally {
      setAuthLoading(false);
    }
  };

  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={styles.modal}>
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle}>{title}</h2>
          <button className={styles.closeBtn} onClick={onClose}>x</button>
        </div>

        <form onSubmit={handleAuth} className={styles.modalBody}>
          <p className={styles.authHint}>{hint}</p>
          <div className={styles.authToggle}>
            <button
              type="button"
              className={`${styles.toggleBtn} ${authMethod === 'password' ? styles.toggleActive : ''}`}
              onClick={() => setAuthMethod('password')}
            >
              Password
            </button>
            <button
              type="button"
              className={`${styles.toggleBtn} ${authMethod === 'totp' ? styles.toggleActive : ''}`}
              onClick={() => setAuthMethod('totp')}
            >
              Authenticator Code
            </button>
          </div>

          {authMethod === 'password' ? (
            <div className={styles.field}>
              <label className={styles.label}>Password</label>
              <input
                type="password"
                className={styles.input}
                value={password}
                onChange={e => setPassword(e.target.value)}
                autoFocus
                autoComplete="current-password"
              />
            </div>
          ) : (
            <div className={styles.field}>
              <label className={styles.label}>6-digit code</label>
              <input
                type="text"
                inputMode="numeric"
                maxLength={6}
                className={styles.input}
                value={totpCode}
                onChange={e => setTotpCode(e.target.value.replace(/\D/g, ''))}
                autoFocus
                autoComplete="one-time-code"
                placeholder="000000"
              />
            </div>
          )}

          {authError && <div className={styles.error}>{authError}</div>}

          <button type="submit" className={styles.btnPrimary} disabled={authLoading}>
            {authLoading ? 'Verifying…' : 'Continue'}
          </button>
        </form>
      </div>
    </div>
  );
}
