import { useState, useEffect, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import * as authApi from '../../api/auth';
import { setAccessToken } from '../../api/client';
import { useAuth } from '../../contexts/AuthContext';
import styles from './Login.module.css';
import localStyles from './SetupTotp.module.css';

export default function SetupTotp() {
  const navigate = useNavigate();
  const { setTokenAndUser } = useAuth();
  const [sharedKey, setSharedKey] = useState('');
  const [authenticatorUri, setAuthenticatorUri] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [loadError, setLoadError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    authApi.setupTotp()
      .then(data => {
        setSharedKey(data.sharedKey);
        setAuthenticatorUri(data.authenticatorUri);
      })
      .catch(() => setLoadError('Failed to load TOTP setup. Please try again.'))
      .finally(() => setLoading(false));
  }, []);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!code || code.length !== 6) {
      setError('Please enter a 6-digit code');
      return;
    }
    setError('');
    setSubmitting(true);
    try {
      await authApi.enrollTotp(code);
      // After enrollment, try to get a fresh token
      try {
        const token = await authApi.refreshTokens();
        setAccessToken(token.accessToken);
        setTokenAndUser(token.accessToken, { id: '', email: '', role: token.role });
      } catch {
        // ignore refresh errors
      }
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
        <div className={localStyles.keyBox}>
          <div className={localStyles.keyLabel}>Manual Entry Key</div>
          <div className={localStyles.keyValue}>{sharedKey}</div>
        </div>
        {authenticatorUri && (
          <div className={localStyles.uriBox}>
            <div className={localStyles.keyLabel}>Authenticator URI</div>
            <div className={localStyles.uriValue}>{authenticatorUri}</div>
          </div>
        )}
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
