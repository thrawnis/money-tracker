import { useEffect, useState } from 'react';
import { getDigestSettings, updateDigestSettings, sendTestDigest, type DigestSettings } from '../api/digest';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import styles from '../pages/Settings.module.css';

type ApiError = { response?: { data?: { message?: string } } };
const msg = (err: unknown, fallback: string) => (err as ApiError)?.response?.data?.message ?? fallback;

const hourLabel = (h: number) => new Date(2000, 0, 1, h).toLocaleTimeString('en-US', { hour: 'numeric' });

/** Settings → Preferences: the daily summary email and sign-in alert emails. */
export default function EmailDigestSettings() {
  const [saved, setSavedSettings] = useState<DigestSettings | null>(null);
  const [form, setForm] = useState<DigestSettings | null>(null);
  const [busy, setBusy] = useState<'save' | 'test' | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  useEffect(() => {
    getDigestSettings()
      .then(s => { setSavedSettings(s); setForm(s); })
      .catch(() => setError('Failed to load email digest settings.'));
  }, []);

  const dirty = !!form && !!saved && (form.enabled !== saved.enabled || form.hour !== saved.hour
    || form.includeAmounts !== saved.includeAmounts || form.onlyWhenNews !== saved.onlyWhenNews
    || form.loginAlerts !== saved.loginAlerts);
  useUnsavedChanges(dirty);

  if (!form) return error ? <div className={styles.error}>{error}</div> : null;
  const set = (patch: Partial<DigestSettings>) => { setForm({ ...form, ...patch }); setNotice(''); };

  const save = async () => {
    setBusy('save'); setError(''); setNotice('');
    try {
      const s = await updateDigestSettings(form);
      setSavedSettings(s); setForm(s);
      setNotice('Saved.');
    } catch (err) {
      setError(msg(err, 'Failed to save the email digest settings.'));
    } finally {
      setBusy(null);
    }
  };

  const test = async () => {
    setBusy('test'); setError(''); setNotice('');
    try {
      const r = await sendTestDigest();
      setNotice(`Test email sent to ${r.sentTo}.`);
    } catch (err) {
      setError(msg(err, 'Failed to send the test email.'));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className={styles.prefGroup}>
      <p className={styles.prefGroupTitle}>Email notifications</p>
      <p className={styles.hint}>
        A short daily email to <strong>{form.email}</strong> covering bank-sync reviews waiting and sync problems,
        bills due in the next 3 days or overdue, accounts projected to drop below their warning level in the next
        30 days, and how many transactions are uncategorized.
      </p>
      {!form.emailConfigured && (
        <p className={styles.hint} style={{ color: 'var(--c-cc0000)' }}>
          Email isn't set up on this server yet: add the SMTP settings to the server's <code>.env</code> (see
          {' '}<code>.env.example</code>) and rebuild. You can still choose your settings now.
        </p>
      )}
      {error && <div className={styles.error}>{error}</div>}

      <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
        <input type="checkbox" checked={form.enabled} onChange={e => set({ enabled: e.target.checked })} />
        <strong>Send me a daily email digest</strong>
      </label>
      <div style={{ display: 'flex', gap: 20, flexWrap: 'wrap', alignItems: 'flex-end', opacity: form.enabled ? 1 : 0.6 }}>
        <div>
          <label style={{ display: 'block', fontSize: 13, fontWeight: 700, marginBottom: 4 }} htmlFor="digest-hour">Send at</label>
          <select id="digest-hour" value={form.hour} onChange={e => set({ hour: Number(e.target.value) })} disabled={!form.enabled}>
            {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{hourLabel(h)}</option>)}
          </select>
          <span className={styles.hint} style={{ marginLeft: 6 }}>your time zone</span>
        </div>
      </div>
      <label style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '10px 0 4px' }}>
        <input type="checkbox" checked={form.onlyWhenNews} disabled={!form.enabled}
          onChange={e => set({ onlyWhenNews: e.target.checked })} />
        Skip days with nothing to report (uncategorized transactions alone don't count)
      </label>
      <label style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '4px 0 10px' }}>
        <input type="checkbox" checked={form.includeAmounts} disabled={!form.enabled}
          onChange={e => set({ includeAmounts: e.target.checked })} />
        Include dollar amounts (off by default: email isn't encrypted the way your data here is)
      </label>

      <label style={{ display: 'flex', alignItems: 'center', gap: 8, margin: '14px 0 4px', fontWeight: 600 }}>
        <input type="checkbox" checked={form.loginAlerts} onChange={e => set({ loginAlerts: e.target.checked })} />
        Email me whenever someone signs in to my account
      </label>
      <p className={styles.hint} style={{ margin: '0 0 12px 24px' }}>
        Sent for every sign-in (not when the app silently keeps you signed in), with the time, sign-in method,
        browser and IP address, so you'd notice a sign-in that wasn't you.
      </p>

      <div style={{ display: 'flex', gap: 10, alignItems: 'center' }}>
        <button className={styles.btnPrimary} onClick={save} disabled={busy !== null || !dirty}>
          {busy === 'save' ? 'Saving…' : 'Save Email Settings'}
        </button>
        <button className={styles.btnSecondary} onClick={test} disabled={busy !== null || !form.emailConfigured}>
          {busy === 'test' ? 'Sending…' : 'Send a Test Digest'}
        </button>
        {notice && <span style={{ color: '#1a7a40', fontSize: 13 }}>{notice}</span>}
      </div>
    </div>
  );
}
