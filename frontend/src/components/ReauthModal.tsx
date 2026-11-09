import { useState, useEffect, type FormEvent } from 'react';
import api from '../api/client';
import { getMfaStatus } from '../api/auth';
import { passkeysSupported, getPasskey, passkeyErrorMessage } from '../utils/webauthn';
import styles from './ExportModal.module.css';

interface Props {
  title?: string;
  hint?: string;
  onVerified: (exportToken: string) => void;
  onClose: () => void;
}

type AuthMethod = 'passkey' | 'password' | 'totp';

/**
 * The identity check itself (passkey, password or authenticator code), issuing
 * the short-lived, single-use exportToken. Passkey is offered — and chosen by
 * default — when the user has one, so people who sign in without a password
 * can still confirm it's them. Used by ReauthModal and the Export dialog.
 */
export function ReauthForm({ hint, onVerified }: { hint: string; onVerified: (exportToken: string) => void }) {
  const [hasPasskey, setHasPasskey] = useState(false);
  const [authMethod, setAuthMethod] = useState<AuthMethod>('password');
  const [password, setPassword] = useState('');
  const [totpCode, setTotpCode] = useState('');
  const [authError, setAuthError] = useState('');
  const [authLoading, setAuthLoading] = useState(false);

  useEffect(() => {
    if (!passkeysSupported()) return;
    getMfaStatus().then(s => {
      if (s.passkeys.length > 0) { setHasPasskey(true); setAuthMethod('passkey'); }
    }).catch(() => {});
  }, []);

  const handleAuth = async (e: FormEvent) => {
    e.preventDefault();
    setAuthError('');
    setAuthLoading(true);
    try {
      let payload: Record<string, string>;
      if (authMethod === 'passkey') {
        const options = await api.post<Record<string, unknown>>('/export/confirm-identity/passkey').then(r => r.data);
        payload = { assertionResponseJson: await getPasskey(options) };
      } else {
        payload = authMethod === 'password' ? { password } : { totpCode };
      }
      const res = await api.post<{ exportToken: string }>('/export/confirm-identity', payload);
      onVerified(res.data.exportToken);
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setAuthError(authMethod === 'passkey' && !msg ? passkeyErrorMessage(err) : (msg ?? 'Authentication failed.'));
    } finally {
      setAuthLoading(false);
    }
  };

  const toggle = (m: AuthMethod, label: string) => (
    <button
      type="button"
      className={`${styles.toggleBtn} ${authMethod === m ? styles.toggleActive : ''}`}
      onClick={() => { setAuthMethod(m); setAuthError(''); }}
    >
      {label}
    </button>
  );

  return (
    <form onSubmit={handleAuth} className={styles.modalBody}>
      <p className={styles.authHint}>{hint}</p>
      <div className={styles.authToggle}>
        {hasPasskey && toggle('passkey', 'Passkey')}
        {toggle('password', 'Password')}
        {toggle('totp', 'Authenticator Code')}
      </div>

      {authMethod === 'passkey' && (
        <p className={styles.authHint}>Use your passkey (fingerprint, face or device PIN) to continue.</p>
      )}
      {authMethod === 'password' && (
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
      )}
      {authMethod === 'totp' && (
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
        {authLoading ? 'Verifying…' : authMethod === 'passkey' ? 'Use Passkey' : 'Continue'}
      </button>
    </form>
  );
}

// Re-authentication prompt shared by anything that needs a fresh identity
// check before proceeding (account backups, Settings → Security changes).
export default function ReauthModal({ title = 'Confirm Identity', hint = 'Confirm your identity to continue.', onVerified, onClose }: Props) {
  return (
    <div className={styles.overlay} onClick={e => { if (e.target === e.currentTarget) onClose(); }}>
      <div className={styles.modal}>
        <div className={styles.modalHeader}>
          <h2 className={styles.modalTitle}>{title}</h2>
          <button className={styles.closeBtn} onClick={onClose}>x</button>
        </div>
        <ReauthForm hint={hint} onVerified={onVerified} />
      </div>
    </div>
  );
}
