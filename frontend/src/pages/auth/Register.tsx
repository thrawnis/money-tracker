import { useState, type FormEvent } from 'react';
import { useNavigate, Link } from 'react-router-dom';
import * as authApi from '../../api/auth';
import TimeZoneSelect from '../../components/TimeZoneSelect';
import { detectTimeZone } from '../../utils/timezone';
import styles from './Login.module.css';

export default function Register() {
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  // Preselected from the browser so the common case is already correct.
  const [timeZoneId, setTimeZoneId] = useState(detectTimeZone());
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [globalError, setGlobalError] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const validate = () => {
    const e: Record<string, string> = {};
    if (!email) e.email = 'Email is required';
    if (!password) e.password = 'Password is required';
    else if (password.length < 12) e.password = 'Password must be at least 12 characters';
    else if (!/[A-Z]/.test(password)) e.password = 'Password must contain an uppercase letter';
    else if (!/[a-z]/.test(password)) e.password = 'Password must contain a lowercase letter';
    else if (!/[0-9]/.test(password)) e.password = 'Password must contain a number';
    else if (!/[^A-Za-z0-9]/.test(password)) e.password = 'Password must contain a special character';
    if (!confirm) e.confirm = 'Please confirm your password';
    else if (confirm !== password) e.confirm = 'Passwords do not match';
    if (!timeZoneId) e.timeZoneId = 'Time zone is required';
    setErrors(e);
    return Object.keys(e).length === 0;
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!validate()) return;
    setGlobalError('');
    setSubmitting(true);
    try {
      const res = await authApi.register(email, password, timeZoneId);
      // Store userId so the TOTP setup page can use it
      sessionStorage.setItem('mfa_setup_user_id', res.userId);
      navigate('/auth/setup-totp');
    } catch (err: unknown) {
      const response = (err as { response?: { status?: number; data?: unknown } })?.response;
      const data = response?.data;
      let msg = 'Registration failed.';
      if (typeof data === 'string' && data) {
        msg = data;
      } else if (Array.isArray(data)) {
        msg = data.join(' ');
      } else if (data && typeof data === 'object' && 'message' in data) {
        msg = String((data as { message: unknown }).message);
      } else if (data && typeof data === 'object') {
        msg = JSON.stringify(data);
      } else if (response?.status) {
        msg = `Registration failed (HTTP ${response.status}).`;
      }
      setGlobalError(msg);
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
            <div className={styles.hint}>Min 12 chars · uppercase · lowercase · number · special character</div>
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
          <div className={styles.field}>
            <label className={styles.label} htmlFor="timeZoneId">Time zone</label>
            <TimeZoneSelect
              id="timeZoneId"
              className={styles.input}
              value={timeZoneId}
              onChange={setTimeZoneId}
            />
            <div style={{ fontSize: 11, color: '#666', marginTop: 4 }}>
              Sets what counts as "today" for balances, reports, and scheduled bills.
            </div>
            {errors.timeZoneId && <div className={styles.error}>{errors.timeZoneId}</div>}
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
