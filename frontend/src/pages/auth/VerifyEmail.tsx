import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import * as authApi from '../../api/auth';
import { useAuth } from '../../contexts/auth-context';
import { usePageTitle } from '../../hooks/usePageTitle';
import { finishSignIn } from '../../utils/finishSignIn';
import styles from './Login.module.css';

type ApiError = { response?: { data?: { message?: string } } };

/**
 * The last step of signing in while the account's email address is unverified
 * (required whenever the server can send email): enter the 6-digit code that
 * was just emailed. Success marks the address verified and finishes sign-in.
 */
export default function VerifyEmail() {
  usePageTitle('Money Tracker');
  const navigate = useNavigate();
  const { signIn } = useAuth();
  const email = sessionStorage.getItem('verify_email') ?? '';
  const [code, setCode] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState<'verify' | 'resend' | null>(null);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!/^\d{6}$/.test(code)) { setError('Enter the 6-digit code from the email.'); return; }
    setBusy('verify'); setError(''); setNotice('');
    try {
      const res = await authApi.verifyEmailCode(code);
      sessionStorage.removeItem('verify_email');
      finishSignIn(res, signIn, navigate);
    } catch (err) {
      setError((err as ApiError)?.response?.data?.message ?? "That code didn't work. Please try again.");
    } finally {
      setBusy(null);
    }
  };

  const resend = async () => {
    setBusy('resend'); setError(''); setNotice('');
    try {
      const r = await authApi.resendEmailCode();
      setNotice(`A new code was sent to ${r.email}.`);
    } catch (err) {
      setError((err as ApiError)?.response?.data?.message ?? "Couldn't send a new code.");
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Verify Your Email</h1>
        <p className={styles.subtitle}>
          We sent a 6-digit code to <strong>{email || 'your email address'}</strong>. Enter it to confirm the address
          and finish signing in. You'll only need to do this once.
        </p>
        {error && <div className={styles.globalError}>{error}</div>}
        {notice && <p className={styles.hint}>{notice}</p>}
        <form onSubmit={submit} noValidate>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="email-code">Verification code</label>
            <input
              id="email-code"
              className={styles.input}
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={6}
              value={code}
              onChange={e => setCode(e.target.value.replace(/\D/g, ''))}
              placeholder="000000"
              autoFocus
            />
          </div>
          <button type="submit" className={styles.btn} disabled={busy !== null}>
            {busy === 'verify' ? 'Verifying…' : 'Verify and Sign In'}
          </button>
        </form>
        <div className={styles.link}>
          Didn't get it? Check spam, or{' '}
          <button type="button" className={styles.linkBtn} onClick={resend} disabled={busy !== null}>
            {busy === 'resend' ? 'sending…' : 'send a new code'}
          </button>
        </div>
        <div className={styles.link}><Link to="/auth/login">Back to sign in</Link></div>
      </div>
    </div>
  );
}
