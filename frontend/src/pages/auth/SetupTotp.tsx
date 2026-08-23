import { useState, useEffect, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { QRCodeSVG } from 'qrcode.react';
import * as authApi from '../../api/auth';
import { useAuth } from '../../contexts/auth-context';
import styles from './Login.module.css';
import localStyles from './SetupTotp.module.css';

export default function SetupTotp() {
  const navigate = useNavigate();
  const { signIn } = useAuth();
  const [sharedKey, setSharedKey] = useState('');
  const [authenticatorUri, setAuthenticatorUri] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [loadError, setLoadError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [loading, setLoading] = useState(true);

  const userId = sessionStorage.getItem('mfa_setup_user_id') ?? '';

  useEffect(() => {
    if (!userId) {
      setLoadError('Session expired. Please register again.');
      setLoading(false);
      return;
    }
    authApi.setupTotp(userId)
      .then(data => {
        setSharedKey(data.sharedKey);
        setAuthenticatorUri(data.authenticatorUri);
      })
      .catch(() => setLoadError('Failed to load TOTP setup. Please try again.'))
      .finally(() => setLoading(false));
  }, [userId]);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!code || code.length !== 6) {
      setError('Please enter a 6-digit code');
      return;
    }
    setError('');
    setSubmitting(true);
    try {
      const res = await authApi.enrollTotp(userId, code);
      sessionStorage.removeItem('mfa_setup_user_id');
      signIn(res.accessToken);
      navigate('/');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Invalid code. Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) return <div className={styles.page}><div className={styles.card}><p>Loading…</p></div></div>;
  if (loadError) return <div className={styles.page}><div className={styles.card}><div className={styles.globalError}>{loadError}</div></div></div>;

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Set Up Two-Factor Auth</h1>
        <p className={localStyles.instructions}>
          Add your account to an authenticator app (e.g. Google Authenticator, Authy) using the key below.
        </p>
        {authenticatorUri && (
          <div className={localStyles.qrBox}>
            <QRCodeSVG value={authenticatorUri} size={180} />
          </div>
        )}
        <div className={localStyles.keyBox}>
          <div className={localStyles.keyLabel}>Or enter key manually</div>
          <div className={localStyles.keyValue}>{sharedKey}</div>
        </div>
        <form onSubmit={handleSubmit} noValidate>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="totp-code">Enter 6-digit code to confirm</label>
            <input
              id="totp-code"
              type="text"
              inputMode="numeric"
              pattern="[0-9]{6}"
              maxLength={6}
              className={styles.input}
              value={code}
              onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
              autoComplete="one-time-code"
              autoFocus
              placeholder="000000"
            />
            {error && <div className={styles.error}>{error}</div>}
          </div>
          <button type="submit" className={styles.btn} disabled={submitting}>
            {submitting ? 'Verifying…' : 'Enable Two-Factor Auth'}
          </button>
        </form>
      </div>
    </div>
  );
}
