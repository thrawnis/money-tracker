import { useState, type FormEvent } from 'react';
import { setTimeZone } from '../../api/preferences';
import { useAuth } from '../../contexts/auth-context';
import TimeZoneSelect from '../../components/TimeZoneSelect';
import { detectTimeZone } from '../../utils/timezone';
import styles from './Login.module.css';

/**
 * Blocking screen for accounts created before the timezone requirement.
 *
 * Rendered by AppShell in place of the whole app — not as a route — so there
 * is no URL to navigate around it and no partially-usable UI behind it. The
 * API enforces the same rule independently (RequireTimeZoneFilter returns 428),
 * so this screen is the convenient way past the gate, not the gate itself.
 *
 * Saving returns a fresh access token carrying the new "tz" claim; feeding it
 * to signIn() clears `needsTimeZone` and the app renders normally. No reload,
 * and no window where the client thinks it's allowed through but the API
 * disagrees.
 */
export default function RequireTimeZone() {
  const { signIn, logout } = useAuth();
  const [value, setValue] = useState(detectTimeZone());
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!value) { setError('Please select your time zone.'); return; }
    setSaving(true);
    setError('');
    try {
      const res = await setTimeZone(value);
      signIn(res.accessToken);
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message;
      setError(msg ?? 'Failed to save your time zone. Please try again.');
      setSaving(false);
    }
  };

  return (
    <div className={styles.page}>
      <div className={styles.card}>
        <h1 className={styles.title}>Set your time zone</h1>
        <p style={{ fontSize: 12, color: '#555', lineHeight: 1.5, marginBottom: 16 }}>
          Money Tracker needs to know your time zone before you continue. It decides what counts
          as "today" — your account balances as of today, the date ranges reports default to, and
          the moment a scheduled bill posts. Until it's set, those are calculated in UTC, which
          shifts them for anyone not living there.
        </p>
        <p style={{ fontSize: 12, color: '#555', lineHeight: 1.5, marginBottom: 16 }}>
          You can change it later in Settings → Preferences.
        </p>

        {error && <div className={styles.globalError}>{error}</div>}

        <form onSubmit={handleSubmit} noValidate>
          <div className={styles.field}>
            <label className={styles.label} htmlFor="tz">Time zone</label>
            <TimeZoneSelect id="tz" className={styles.input} value={value} onChange={setValue} />
          </div>
          <button type="submit" className={styles.btn} disabled={saving}>
            {saving ? 'Saving…' : 'Continue'}
          </button>
        </form>

        <div className={styles.link}>
          <button
            type="button"
            onClick={logout}
            style={{ background: 'none', border: 'none', color: '#005f5f', cursor: 'pointer', textDecoration: 'underline', fontSize: 12 }}
          >
            Sign out instead
          </button>
        </div>
      </div>
    </div>
  );
}
