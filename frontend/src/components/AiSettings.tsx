import { useEffect, useState, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { getAiSettings, saveAiSettings, testAiServer, type AiSettings as Settings, type AiShare } from '../api/ai';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { confirmDialog } from '../utils/dialogs';
import styles from '../pages/Settings.module.css';

type ApiError = { response?: { data?: { message?: string } } };
const msg = (err: unknown, fallback: string) => (err as ApiError)?.response?.data?.message ?? fallback;

const row = { display: 'flex', alignItems: 'flex-start', gap: 8, margin: '6px 0' } as const;

function Check({ checked, onChange, disabled, children }: { checked: boolean; onChange: (v: boolean) => void; disabled?: boolean; children: ReactNode }) {
  return (
    <label style={row}>
      <input type="checkbox" checked={checked} disabled={disabled} onChange={e => onChange(e.target.checked)} style={{ marginTop: 3 }} />
      <span>{children}</span>
    </label>
  );
}

const SHARE: { key: keyof AiShare; label: string; hint: string }[] = [
  { key: 'balances', label: 'Accounts and balances', hint: 'Account names, types, institutions and current balances. Never account numbers or notes.' },
  { key: 'transactions', label: 'Transactions', hint: 'Dates, payees, categories and amounts, and totals by category, payee or month.' },
  { key: 'memos', label: 'Memos', hint: 'The memo text on transactions and splits (only with Transactions).' },
  { key: 'bills', label: 'Bills & reminders', hint: 'Recurring bills, amounts and due dates.' },
  { key: 'ious', label: 'IOUs', hint: 'Who owes whom, and IOU descriptions.' },
];

const PROVIDERS: Record<string, string> = { openai: 'OpenAI-compatible', ollama: 'Ollama', anthropic: 'Anthropic', gemini: 'Google Gemini' };

/** Settings → AI Assistant: turn it on and choose what it may look up. The AI server is set in .env. */
export default function AiSettings() {
  const [saved, setSaved] = useState<Settings | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [share, setShare] = useState<AiShare>({ balances: true, transactions: true, memos: true, bills: true, ious: true });
  const [busy, setBusy] = useState<'save' | 'test' | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  const apply = (s: Settings) => { setSaved(s); setEnabled(s.enabled); setShare(s.share); };
  useEffect(() => { getAiSettings().then(apply).catch(() => setError('Failed to load the AI assistant settings.')); }, []);

  const dirty = !!saved && (enabled !== saved.enabled || SHARE.some(s => share[s.key] !== saved.share[s.key]));
  useUnsavedChanges(dirty && busy === null);

  if (!saved) return <p className={styles.hint}>{error || 'Loading…'}</p>;
  const server = saved.server;

  const save = async () => {
    setError(''); setNotice('');
    if (enabled && !saved.enabled && !(await confirmDialog(
      `Turn on the AI assistant?\n\nYour questions, and the information you've chosen to share below, will be sent to ${server?.host ?? 'the AI server'} `
      + 'to answer them. Your data is decrypted to do this. Conversations are saved in Money Tracker, encrypted.',
      { title: 'AI assistant', confirmLabel: 'Turn on' }))) return;
    setBusy('save');
    try { apply(await saveAiSettings({ enabled, share })); setNotice('Saved.'); }
    catch (err) { setError(msg(err, 'Failed to save.')); }
    finally { setBusy(null); }
  };

  const test = async () => {
    setError(''); setNotice('');
    setBusy('test');
    try { setNotice((await testAiServer()).message); }
    catch (err) { setError(msg(err, 'The test failed.')); }
    finally { setBusy(null); }
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Ask questions about your money in plain English on the <Link to="/ask">Ask AI</Link> page — "How much did I spend on
        restaurants this year?", "What's due this week?". The assistant can only <strong>look things up</strong>; it can't change anything.
      </p>

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>AI server</p>
        {server ? (
          <>
            <p className={styles.hint} style={{ margin: '0 0 8px' }}>
              <strong>{server.model}</strong> on {server.host} ({PROVIDERS[server.provider] ?? server.provider}).
              Set by whoever runs Money Tracker, in the server's <code>.env</code> file.
            </p>
            <button className={styles.btnSecondary} onClick={test} disabled={busy !== null}>
              {busy === 'test' ? 'Testing…' : 'Test connection'}
            </button>
          </>
        ) : (
          <p className={styles.hint} style={{ margin: 0 }}>
            No AI server is set up yet{saved.serverProblem ? ` — ${saved.serverProblem}` : '.'} Whoever runs Money Tracker
            can set one up in the server's <code>.env</code> file (AI_PROVIDER, AI_BASE_URL, AI_API_KEY, AI_MODEL).
          </p>
        )}
      </div>

      <div className={styles.prefGroup}>
        <Check checked={enabled} onChange={setEnabled} disabled={!server && !saved.enabled}>
          <strong>Turn on the AI assistant</strong>
        </Check>
      </div>

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>What the assistant can look up</p>
        <p className={styles.hint}>
          Only what's ticked is ever sent to the AI server, and only the parts needed to answer each question.
        </p>
        {SHARE.map(s => (
          <Check key={s.key} checked={share[s.key]} disabled={s.key === 'memos' && !share.transactions}
            onChange={v => setShare({ ...share, [s.key]: v })}>
            {s.label}<br /><span className={styles.hint} style={{ margin: 0 }}>{s.hint}</span>
          </Check>
        ))}
      </div>

      {error && <div className={styles.error}>{error}</div>}
      {notice && <div className={styles.success}>{notice}</div>}
      <button className={styles.btnPrimary} onClick={save} disabled={busy !== null || !dirty}>
        {busy === 'save' ? 'Saving…' : 'Save'}
      </button>
    </div>
  );
}
