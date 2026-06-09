import { useState, useEffect, type FormEvent } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import { useAuth } from '../../contexts/AuthContext';
import * as authApi from '../../api/auth';
import { setAccessToken } from '../../api/client';
import { getDemoInfo } from '../../api/demo';
import styles from './Login.module.css';

export default function Login() {
  const navigate = useNavigate();
  const { setTokenAndUser } = useAuth();

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);
  const [emailError, setEmailError] = useState('');
  const [passwordError, setPasswordError] = useState('');
  const [globalError, setGlobalError] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [demoEmail, setDemoEmail] = useState<string | null>(null);

  useEffect(() => {
    getDemoInfo().then(info => {
      if (info.isDemoMode && info.email) setDemoEmail(info.email);
    }).catch(() => {});
  }, []);

  const handleTryDemo = async () => {
    if (!demoEmail) return;
    setGlobalError('');
    setSubmitting(true);
    try {
      await authApi.login(demoEmail, 'Demo123456!!', false);
      const token = await authApi.refreshTokens();
      setAccessToken(token.accessToken);
      setTokenAndUser(token.accessToken, { id: '', email: demoEmail, role: token.role });
      navigate('/');
    } catch {
      setGlobalError('Failed to start demo. Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  const validate = () => {
    let ok = true;
    setEmailError('');
    setPasswordError('');
    if (!email) { setEmailError('Email is required'); ok = false; }
    if (!password) { setPasswordError('Password is required'); ok = false; }
    return ok;
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!validate()) return;
    setGlobalError('');
    setSubmitting(true);
    try {
      const res = await authApi.login(email, password, rememberMe);
      if (res.requiresMfa && res.userId) {
        sessionStorage.setItem('mfa_login_user_id', res.userId);
        navigate('/auth/totp');
      } else if (res.requiresMfaSetup && res.userId) {
        sessionStorage.setItem('mfa_setup_user_id', res.userId);
        navigate('/auth/setup-totp');
      } else {
        try {
          const token = await authApi.refreshTokens();
          setAccessToken(token.accessToken);
          setTokenAndUser(token.accessToken, { id: '', email, role: token.role });
          navigate('/');
        } catch {
          navigate('/');
        }
      }
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setGlobalError(msg ?? 'Login failed. Please check your credentials.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Money Tracker — Sign In</h1>
        {globalError && <div className={styles.globalError}>{globalError}</div>}
        <form onSubmit={handleSubmit} noValidate>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="email">Email</label>
            <input
              id="email"
              type="email"
              className={styles.input}
              value={email}
              onChange={e => setEmail(e.target.value)}
              autoComplete="email"
              autoFocus
            />
            {emailError && <div className={styles.error}>{emailError}</div>}
          </div>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="password">Password</label>
            <input
              id="password"
              type="password"
              className={styles.input}
              value={password}
              onChange={e => setPassword(e.target.value)}
              autoComplete="current-password"
            />
            {passwordError && <div className={styles.error}>{passwordError}</div>}
          </div>
          <div className={styles.rememberRow}>
            <label className={styles.rememberLabel}>
              <input
                type="checkbox"
                checked={rememberMe}
                onChange={e => setRememberMe(e.target.checked)}
                className={styles.rememberCheck}
              />
              Remember me for 2 weeks
            </label>
          </div>
          <button type="submit" className={styles.btn} disabled={submitting}>
            {submitting ? 'Signing in…' : 'Sign In'}
          </button>
        </form>
        <div className={styles.link}>
          Don't have an account? <Link to="/auth/register">Register</Link>
        </div>
        {demoEmail && (
          <>
            <div className={styles.divider}>or</div>
            <button
              type="button"
              className={styles.btnDemo}
              onClick={handleTryDemo}
              disabled={submitting}
            >
              Try Demo
            </button>
          </>
        )}
      </div>
    </div>
  );
}
