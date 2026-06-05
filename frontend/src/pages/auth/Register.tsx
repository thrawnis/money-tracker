import { useState, type FormEvent } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import * as authApi from '../../api/auth';
import { setAccessToken } from '../../api/client';
import { useAuth } from '../../contexts/AuthContext';
import styles from './Login.module.css';

export default function Register() {
  const navigate = useNavigate();
  const { setTokenAndUser } = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [globalError, setGlobalError] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const validate = () => {
    const e: Record<string, string> = {};
    if (!email) e.email = 'Email is required';
    if (!password) e.password = 'Password is required';
    else if (password.length < 8) e.password = 'Password must be at least 8 characters';
    if (!confirm) e.confirm = 'Please confirm your password';
    else if (confirm !== password) e.confirm = 'Passwords do not match';
    setErrors(e);
    return Object.keys(e).length === 0;
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!validate()) return;
    setGlobalError('');
    setSubmitting(true);
    try {
      const res = await authApi.register(email, password);
      if (res.accessToken) {
        setAccessToken(res.accessToken);
        setTokenAndUser(res.accessToken, { id: '', email, role: res.role });
      }
      navigate('/auth/setup-totp');
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setGlobalError(msg ?? 'Registration failed. Please try again.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Money Tracker — Register</h1>
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
            {errors.email && <div className={styles.error}>{errors.email}</div>}
          </div>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="password">Password</label>
            <input
              id="password"
              type="password"
              className={styles.input}
              value={password}
              onChange={e => setPassword(e.target.value)}
              autoComplete="new-password"
            />
            {errors.password && <div className={styles.error}>{errors.password}</div>}
          </div>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="confirm">Confirm Password</label>
            <input
              id="confirm"
              type="password"
              className={styles.input}
              value={confirm}
              onChange={e => setConfirm(e.target.value)}
              autoComplete="new-password"
            />
            {errors.confirm && <div className={styles.error}>{errors.confirm}</div>}
          </div>
          <button type="submit" className={styles.btn} disabled={submitting}>
            {submitting ? 'Registering…' : 'Create Account'}
          </button>
        </form>
        <div className={styles.link}>
          Already have an account? <Link to="/auth/login">Sign In</Link>
        </div>
      </div>
    </div>
  );
}
