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

/** Settings → AI Assistant. */
export default function AiSettings() {
  const [saved, setSaved] = useState<Settings | null>(null);
  const [enabled, setEnabled] = useState(false);
  const [usePersonal, setUsePersonal] = useState(false);
  const [baseUrl, setBaseUrl] = useState('');
  const [model, setModel] = useState('');
  const [apiKey, setApiKey] = useState('');      // '' = keep the saved key
  const [removeKey, setRemoveKey] = useState(false);
  const [share, setShare] = useState<AiShare>({ balances: true, transactions: true, memos: true, bills: true, ious: true });
  const [busy, setBusy] = useState<'save' | 'test' | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  const apply = (s: Settings) => {
    setSaved(s);
    setEnabled(s.enabled);
    setUsePersonal(!!s.baseUrl || (!s.server && s.allowUserServers));
    setBaseUrl(s.baseUrl ?? '');
    setModel(s.model ?? '');
    setApiKey(''); setRemoveKey(false);
    setShare(s.share);
  };
  useEffect(() => { getAiSettings().then(apply).catch(() => setError('Failed to load the AI assistant settings.')); }, []);

  const dirty = !!saved && (
    enabled !== saved.enabled
    || (usePersonal ? baseUrl.trim() : '') !== (saved.baseUrl ?? '')
    || model.trim() !== (saved.model ?? '')
    || apiKey !== '' || removeKey
    || SHARE.some(s => share[s.key] !== saved.share[s.key]));
  useUnsavedChanges(dirty && busy === null);

  if (!saved) return <p className={styles.hint}>{error || 'Loading…'}</p>;

  const personal = usePersonal && saved.allowUserServers;
  const keyArg = removeKey ? '' : apiKey.trim() || undefined;

  const save = async () => {
    setError(''); setNotice('');
    if (enabled && personal && !saved.allowUserServers) return;
    // Turning sharing on for a server outside your network: make sure it's understood.
    const host = personal ? (() => { try { return new URL(baseUrl.trim()).host; } catch { return baseUrl.trim(); } })() : saved.server?.host;
    if (enabled && !saved.enabled && !(await confirmDialog(
      `Turn on the AI assistant?\n\nYour questions, and the information you've chosen to share below, will be sent to ${host ?? 'the AI server'} `
      + 'to answer them. Your data is decrypted to do this. Conversations are saved in Money Tracker, encrypted.',
      { title: 'AI assistant', confirmLabel: 'Turn on' }))) return;
    setBusy('save');
    try {
      const s = await saveAiSettings({
        enabled,
        baseUrl: personal ? baseUrl.trim() || null : null,
        model: model.trim() || null,
        apiKey: personal ? keyArg : '',
        share,
      });
      apply(s);
      setNotice('Saved.');
    } catch (err) { setError(msg(err, 'Failed to save.')); }
    finally { setBusy(null); }
  };

  const test = async () => {
    setError(''); setNotice('');
    setBusy('test');
    try {
      const r = await testAiServer({ baseUrl: personal ? baseUrl.trim() || null : null, model: model.trim() || null, apiKey: personal ? keyArg : undefined });
      setNotice(r.message);
    } catch (err) { setError(msg(err, 'The test failed.')); }
    finally { setBusy(null); }
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Ask questions about your money in plain English on the <Link to="/ask">Ask</Link> page — "How much did I spend on
        restaurants this year?", "What's due this week?". The assistant uses an AI server you choose (OpenAI, or one you
        run yourself such as Ollama or LM Studio) and can only <strong>look things up</strong>; it can't change anything.
      </p>

      <div className={styles.prefGroup}>
        <Check checked={enabled} onChange={setEnabled}><strong>Turn on the AI assistant</strong></Check>
        {saved.ready && saved.active && (
          <p className={styles.hint} style={{ margin: '2px 0 0 24px' }}>
            Using {saved.active.model} on {saved.active.host}{saved.active.personal ? ' (your server)' : ' (this app\'s server default)'}.
          </p>
        )}
      </div>

      <div className={styles.prefGroup}>
        <p className={styles.prefGroupTitle}>AI server</p>
        {saved.server && (
          <label style={row}>
            <input type="radio" name="ai-server" checked={!personal} onChange={() => setUsePersonal(false)} style={{ marginTop: 3 }} />
            <span>Use this app's default: <strong>{saved.server.model}</strong> on {saved.server.host}</span>
          </label>
        )}
        {saved.allowUserServers ? (
          <>
            {saved.server && (
              <label style={row}>
                <input type="radio" name="ai-server" checked={personal} onChange={() => setUsePersonal(true)} style={{ marginTop: 3 }} />
                <span>Use my own server</span>
              </label>
            )}
            {personal && (
              <div style={{ marginLeft: saved.server ? 24 : 0 }}>
                <div className={styles.field}>
                  <label className={styles.label} htmlFor="ai-url">Server address</label>
                  <input id="ai-url" className={styles.input} value={baseUrl} onChange={e => setBaseUrl(e.target.value)}
                    placeholder="https://api.openai.com/v1" autoComplete="off" spellCheck={false} />
                  <p className={styles.hint} style={{ margin: '4px 0 0' }}>
                    Any OpenAI-compatible API, usually ending in /v1 — e.g. https://api.openai.com/v1,
                    http://192.168.1.20:11434/v1 (Ollama) or http://192.168.1.20:1234/v1 (LM Studio).
                  </p>
                </div>
                <div className={styles.field}>
                  <label className={styles.label} htmlFor="ai-key">API key {saved.hasApiKey && !removeKey ? '(saved — leave blank to keep it)' : '(if the server needs one)'}</label>
                  <input id="ai-key" className={styles.input} type="password" value={apiKey} disabled={removeKey}
                    onChange={e => setApiKey(e.target.value)} autoComplete="new-password" placeholder={saved.hasApiKey ? '••••••••' : ''} />
                  {saved.hasApiKey && (
                    <Check checked={removeKey} onChange={setRemoveKey}>Remove the saved key</Check>
                  )}
                </div>
              </div>
            )}
          </>
        ) : !saved.server && (
          <p className={styles.hint}>No AI server is set up for this app yet. Ask whoever runs it to set AI_BASE_URL and AI_MODEL.</p>
        )}
        <div className={styles.field} style={{ marginLeft: personal && saved.server ? 24 : 0 }}>
          <label className={styles.label} htmlFor="ai-model">Model {!personal || saved.server ? '(blank for the default)' : ''}</label>
          <input id="ai-model" className={styles.input} value={model} onChange={e => setModel(e.target.value)}
            placeholder={saved.server?.model ?? 'e.g. gpt-4o-mini or qwen2.5:14b'} autoComplete="off" spellCheck={false} />
          <p className={styles.hint} style={{ margin: '4px 0 0' }}>
            Pick a model that supports tool (function) calling. Larger models give better answers; small local ones may struggle with multi-step questions.
          </p>
        </div>
        <button className={styles.btnSecondary} onClick={test} disabled={busy !== null}>
          {busy === 'test' ? 'Testing…' : 'Test connection'}
        </button>
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
