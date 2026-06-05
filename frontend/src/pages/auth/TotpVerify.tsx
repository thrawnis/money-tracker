import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import * as authApi from '../../api/auth';
import { setAccessToken } from '../../api/client';
import { useAuth } from '../../contexts/AuthContext';
import styles from './Login.module.css';

export default function TotpVerify() {
  const navigate = useNavigate();
  const { setTokenAndUser } = useAuth();
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const userId = sessionStorage.getItem('mfa_login_user_id') ?? '';

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!code || code.length !== 6) {
      setError('Please enter a 6-digit code');
      return;
    }
    if (!userId) {
      setError('Session expired. Please sign in again.');
      return;
    }
    setError('');
    setSubmitting(true);
    try {
      const res = await authApi.verifyTotp(userId, code);
      sessionStorage.removeItem('mfa_login_user_id');
      setAccessToken(res.accessToken);
      setTokenAndUser(res.accessToken, { id: userId, email: '', role: res.role });
      navigate('/');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Invalid code. Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Two-Factor Authentication</h1>
        <p style={{ fontSize: 13, color: '#333', marginBottom: 16 }}>
          Enter the 6-digit code from your authenticator app.
        </p>
        <form onSubmit={handleSubmit} noValidate>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="totp">Authentication Code</label>
            <input
              id="totp"
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
            {submitting ? 'Verifying…' : 'Verify'}
          </button>
        </form>
      </div>
    </div>
  );
}
