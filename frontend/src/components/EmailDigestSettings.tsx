import { useEffect, useState, type ReactNode } from 'react';
import { getDigestSettings, updateDigestSettings, sendTestDigest, type DigestSettings } from '../api/digest';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import styles from '../pages/Settings.module.css';

type ApiError = { response?: { data?: { message?: string } } };
const msg = (err: unknown, fallback: string) => (err as ApiError)?.response?.data?.message ?? fallback;

const hourLabel = (h: number) => new Date(2000, 0, 1, h).toLocaleTimeString('en-US', { hour: 'numeric' });
const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const FIELDS = ['enabled', 'hour', 'includeAmounts', 'onlyWhenNews', 'loginAlerts', 'syncAlerts',
  'largeTxAlerts', 'largeTxThreshold', 'weeklyEnabled', 'weeklyDay', 'weeklyHour'] as const;

const sub = { margin: '16px 0 4px', fontSize: 14, fontWeight: 700 } as const;
const row = { display: 'flex', alignItems: 'center', gap: 8, margin: '6px 0' } as const;
const indent = { marginLeft: 24 } as const;

function Check({ checked, onChange, disabled, children }: { checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; children: ReactNode }) {
  return (
    <label style={row}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={e => onChange(e.target.checked)} />
      <span>{children}</span>
    </label>
  );
}

function HourSelect({ value, onChange, disabled, id }: { value: number; onChange: (h: number) => void; disabled?: boolean; id: string }) {
  return (
    <select id={id} value={value} onChange={e => onChange(Number(e.target.value))} disabled={disabled}>
      {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{hourLabel(h)}</option>)}
    </select>
  );
}

/** Settings → Preferences: every email the app can send, in one place. */
export default function EmailDigestSettings() {
  const [saved, setSavedSettings] = useState<DigestSettings | null>(null);
  const [form, setForm] = useState<DigestSettings | null>(null);
  const [threshold, setThreshold] = useState('');
  const [busy, setBusy] = useState<'save' | 'daily' | 'weekly' | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  useEffect(() => {
    getDigestSettings()
      .then(s => { setSavedSettings(s); setForm(s); setThreshold(String(s.largeTxThreshold)); })
      .catch(() => setError('Failed to load email settings.'));
  }, []);

  const dirty = !!form && !!saved && (FIELDS.some(f => form[f] !== saved[f]) || Number(threshold) !== saved.largeTxThreshold);
  useUnsavedChanges(dirty);

  if (!form) return error ? <div className={styles.error}>{error}</div> : null;
  const set = (patch: Partial<DigestSettings>) => { setForm({ ...form, ...patch }); setNotice(''); };

  const save = async () => {
    const amount = Number(threshold);
    if (!Number.isFinite(amount) || amount <= 0) { setError('Enter a large-transaction amount above $0.'); return; }
    setBusy('save'); setError(''); setNotice('');
    try {
      const s = await updateDigestSettings({ ...form, largeTxThreshold: amount });
      setSavedSettings(s); setForm(s); setThreshold(String(s.largeTxThreshold));
      setNotice('Saved.');
    } catch (err) {
      setError(msg(err, 'Failed to save the email settings.'));
    } finally {
      setBusy(null);
    }
  };

  const test = async (kind: 'daily' | 'weekly') => {
    setBusy(kind); setError(''); setNotice('');
    try {
      const r = await sendTestDigest(kind);
      setNotice(`Test ${kind} digest sent to ${r.sentTo}.`);
    } catch (err) {
      setError(msg(err, 'Failed to send the test email.'));
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className={styles.prefGroup}>
      <p className={styles.prefGroupTitle}>Email notifications</p>
      <p className={styles.hint}>Emails go to <strong>{form.email}</strong>. Everything except sign-in alerts is off until you turn it on.</p>
      {!form.emailConfigured && (
        <p className={styles.hint} style={{ color: 'var(--c-cc0000)' }}>
          Email isn't set up on this server yet: add the SMTP settings to the server's <code>.env</code> (see
          {' '}<code>.env.example</code>) and rebuild. You can still choose your settings now.
        </p>
      )}
      {error && <div className={styles.error}>{error}</div>}

      <p style={sub}>Bank sync</p>
      <Check checked={form.syncAlerts} onChange={v => set({ syncAlerts: v })}>
        Email me what the nightly bank sync downloaded (per account, for accounts linked to SimpleFIN)
      </Check>
      <Check checked={form.largeTxAlerts} onChange={v => set({ largeTxAlerts: v })}>
        Email me when a bank sync finds a transaction of
        {' '}<input type="number" min="1" step="1" value={threshold} aria-label="Large transaction amount"
          onChange={e => { setThreshold(e.target.value); setNotice(''); }} disabled={!form.largeTxAlerts}
          style={{ width: 90 }} />{' '}dollars or more (money in or out)
      </Check>

      <p style={sub}>Daily digest</p>
      <Check checked={form.enabled} onChange={v => set({ enabled: v })}>
        Send me a daily digest at{' '}
        <HourSelect id="digest-hour" value={form.hour} onChange={h => set({ hour: h })} disabled={!form.enabled} /> your time
      </Check>
      <p className={styles.hint} style={{ ...indent, margin: '0 0 0 24px' }}>
        Bank-sync reviews waiting and sync problems, bills due in the next 3 days, low-balance warnings for the next
        30 days, and the uncategorized count.
      </p>
      <div style={indent}>
        <Check checked={form.onlyWhenNews} disabled={!form.enabled} onChange={v => set({ onlyWhenNews: v })}>
          Skip days with nothing to report (uncategorized transactions alone don't count)
        </Check>
      </div>

      <p style={sub}>Weekly digest</p>
      <Check checked={form.weeklyEnabled} onChange={v => set({ weeklyEnabled: v })}>
        Send me a weekly summary every{' '}
        <select value={form.weeklyDay} onChange={e => set({ weeklyDay: Number(e.target.value) })} disabled={!form.weeklyEnabled} aria-label="Weekly digest day">
          {DAYS.map((d, i) => <option key={d} value={i}>{d}</option>)}
        </select>{' '}at{' '}
        <HourSelect id="weekly-hour" value={form.weeklyHour} onChange={h => set({ weeklyHour: h })} disabled={!form.weeklyEnabled} />
      </Check>
      <p className={styles.hint} style={{ margin: '0 0 0 24px' }}>
        Net worth and how it changed, the week's spending against a usual week, categories that moved most,
        the month's pace, the biggest purchases, what's coming up, and anything waiting for you.
      </p>

      <p style={sub}>Sign-in alerts</p>
      <Check checked={form.loginAlerts} onChange={v => set({ loginAlerts: v })}>
        Email me whenever someone signs in to my account (time, method, browser and IP address)
      </Check>

      <p style={sub}>Privacy</p>
      <Check checked={form.includeAmounts} onChange={v => set({ includeAmounts: v })}>
        Include amounts and payee names in emails
      </Check>
      <p className={styles.hint} style={{ margin: '0 0 12px 24px' }}>
        Off by default: email isn't encrypted the way your data here is. When off, emails give counts, dates and
        percentages instead (a large-transaction alert still says which account and that it was over your amount).
      </p>

      <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap' }}>
        <button className={styles.btnPrimary} onClick={save} disabled={busy !== null || !dirty}>
          {busy === 'save' ? 'Saving…' : 'Save Email Settings'}
        </button>
        <button className={styles.btnSecondary} onClick={() => test('daily')} disabled={busy !== null || !form.emailConfigured}>
          {busy === 'daily' ? 'Sending…' : 'Send Test Daily Digest'}
        </button>
        <button className={styles.btnSecondary} onClick={() => test('weekly')} disabled={busy !== null || !form.emailConfigured}>
          {busy === 'weekly' ? 'Sending…' : 'Send Test Weekly Digest'}
        </button>
        {notice && <span style={{ color: '#1a7a40', fontSize: 13 }}>{notice}</span>}
      </div>
    </div>
  );
}
