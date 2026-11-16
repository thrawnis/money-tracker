import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  askAi, deleteAiConversation, getAiConversation, getAiConversations, getAiSettings,
  type AiConversationSummary, type AiMessage, type AiSettings,
} from '../api/ai';
import Markdown from '../components/Markdown';
import { usePageTitle } from '../hooks/usePageTitle';
import { useUnsavedChanges } from '../hooks/useUnsavedChanges';
import { confirmDialog } from '../utils/dialogs';
import styles from './Ask.module.css';

type ApiError = { response?: { data?: { message?: string } } };
const msg = (err: unknown, fallback: string) => (err as ApiError)?.response?.data?.message ?? fallback;

const EXAMPLES = [
  'How much did I spend on restaurants this month compared with last month?',
  'What are my biggest spending categories this year?',
  'Which bills are due in the next two weeks?',
  'Show my largest purchases over $200 in the last 90 days.',
  'Who owes me money right now?',
];

const when = (iso: string) => {
  const d = new Date(iso);
  return d.toDateString() === new Date().toDateString()
    ? d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })
    : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
};

/**
 * Ask: chat with the AI assistant about your own money. Conversations are
 * saved (encrypted) and listed on the left; ?c=<id> opens one.
 */
export default function Ask() {
  usePageTitle('Money Tracker');
  const [params, setParams] = useSearchParams();
  const openId = params.get('c') ? Number(params.get('c')) : null;

  const [settings, setSettings] = useState<AiSettings | null>(null);
  const [list, setList] = useState<AiConversationSummary[]>([]);
  // The messages shown, tagged with the conversation they belong to (null: a new chat).
  const [convo, setConvo] = useState<{ id: number | null; messages: AiMessage[] }>({ id: null, messages: [] });
  const messages = convo.id === openId ? convo.messages : [];
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [listOpen, setListOpen] = useState(false);   // phones: conversation list shown
  const endRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);

  useUnsavedChanges(draft.trim() !== '' && !busy);

  useEffect(() => {
    getAiSettings().then(setSettings).catch(() => setError('Failed to load the assistant settings.'));
    getAiConversations().then(setList).catch(() => {});
  }, []);

  // Opening a saved conversation (?c=id) loads its messages.
  useEffect(() => {
    if (openId == null || convo.id === openId) return;
    let stale = false;
    getAiConversation(openId)
      .then(c => { if (!stale) setConvo({ id: openId, messages: c.messages }); })
      .catch(() => { if (!stale) setError('That conversation could not be opened.'); });
    return () => { stale = true; };
  }, [openId, convo.id]);

  useEffect(() => { endRef.current?.scrollIntoView({ block: 'end' }); }, [convo, busy]);

  const open = (id: number | null) => {
    setListOpen(false);
    setError('');
    setParams(id == null ? {} : { c: String(id) });
    setTimeout(() => inputRef.current?.focus(), 0);
  };

  const send = async (text = draft) => {
    const q = text.trim();
    if (!q || busy) return;
    setError('');
    setBusy(true);
    setDraft('');
    const before = messages;
    const asked: AiMessage = { role: 'user', content: q };
    setConvo({ id: openId, messages: [...before, asked] });
    try {
      const r = await askAi(openId, q);
      // Tag with the (possibly new) conversation id first, so opening it doesn't reload.
      setConvo({ id: r.conversationId, messages: [...before, asked, { role: 'assistant', content: r.answer }] });
      if (openId == null) setParams({ c: String(r.conversationId) }, { replace: true });
      getAiConversations().then(setList).catch(() => {});
    } catch (err) {
      setError(msg(err, 'The assistant couldn\'t answer.'));
      // Put the question back so it can be retried.
      setConvo({ id: openId, messages: before });
      setDraft(q);
    } finally {
      setBusy(false);
      inputRef.current?.focus();
    }
  };

  const remove = async (c: AiConversationSummary) => {
    if (!(await confirmDialog(`Delete the conversation "${c.title}"?`))) return;
    try {
      await deleteAiConversation(c.id);
      setList(l => l.filter(x => x.id !== c.id));
      if (openId === c.id) open(null);
    } catch { setError('Failed to delete the conversation.'); }
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void send(); }
  };

  const ready = settings?.ready;
  const current = list.find(c => c.id === openId);

  return (
    <div className={styles.page}>
      <div className={styles.header}>
        <h2 className={styles.title}>Ask</h2>
        {settings?.active && ready && (
          <span className={styles.using}>{settings.active.model} · {settings.active.host}</span>
        )}
        <span style={{ flex: 1 }} />
        <button className={`${styles.btnSecondary} ${styles.phoneOnly}`} onClick={() => setListOpen(o => !o)}>
          {listOpen ? 'Hide chats' : `Chats (${list.length})`}
        </button>
        <button className={styles.btnPrimary} onClick={() => open(null)} disabled={busy}>+ New chat</button>
      </div>

      <div className={styles.layout}>
        <aside className={`${styles.sidebar} ${listOpen ? styles.sidebarOpen : ''}`} aria-label="Saved conversations">
          {list.length === 0 && <p className={styles.empty}>Your conversations will be listed here.</p>}
          {list.map(c => (
            <div key={c.id} className={`${styles.convo} ${c.id === openId ? styles.convoActive : ''}`}>
              <button className={styles.convoOpen} onClick={() => open(c.id)} disabled={busy} title={c.title}>
                <span className={styles.convoTitle}>{c.title}</span>
                <span className={styles.convoDate}>{when(c.updatedAt)}</span>
              </button>
              <button className={styles.convoDelete} onClick={() => remove(c)} aria-label={`Delete "${c.title}"`} title="Delete">×</button>
            </div>
          ))}
        </aside>

        <section className={styles.chat}>
          {settings && !ready ? (
            <div className={styles.setup}>
              <p><strong>Ask questions about your money in plain English.</strong></p>
              <p>
                The AI assistant is {settings.enabled ? 'turned on, but no AI server is set up' : 'turned off'}.
                Choose an AI server and what it may look up in <Link to="/settings?tab=ai">Settings → AI Assistant</Link>.
              </p>
            </div>
          ) : (
            <>
              <div className={styles.messages} aria-live="polite">
                {current && <p className={styles.chatTitle}>{current.title}</p>}
                {messages.length === 0 && !busy && (
                  <div className={styles.intro}>
                    <p>Ask about your accounts, spending, bills or IOUs. The assistant looks up your data to answer, and can't change anything.</p>
                    <div className={styles.examples}>
                      {EXAMPLES.map(e => (
                        <button key={e} className={styles.example} onClick={() => void send(e)} disabled={!ready}>{e}</button>
                      ))}
                    </div>
                  </div>
                )}
                {messages.map((m, i) => (
                  <div key={m.id ?? `n${i}`} className={m.role === 'user' ? styles.userMsg : styles.botMsg}>
                    {m.role === 'user' ? <p className={styles.userText}>{m.content}</p> : <Markdown text={m.content} />}
                  </div>
                ))}
                {busy && (
                  <div className={`${styles.botMsg} ${styles.thinking}`} role="status">
                    <span className={styles.dot} /><span className={styles.dot} /><span className={styles.dot} />
                    <span className={styles.thinkingText}>Looking that up…</span>
                  </div>
                )}
                <div ref={endRef} />
              </div>

              {error && <div className={styles.error} role="alert">{error}</div>}
              <form className={styles.composer} onSubmit={e => { e.preventDefault(); void send(); }}>
                <textarea
                  ref={inputRef}
                  className={styles.input}
                  value={draft}
                  onChange={e => setDraft(e.target.value)}
                  onKeyDown={onKeyDown}
                  placeholder={ready ? 'Ask about your money…  (Enter to send, Shift+Enter for a new line)' : ''}
                  rows={2}
                  maxLength={4000}
                  disabled={!ready}
                  aria-label="Your question"
                />
                <button type="submit" className={styles.btnPrimary} disabled={!ready || busy || !draft.trim()}>
                  {busy ? 'Asking…' : 'Ask'}
                </button>
              </form>
              <p className={styles.footnote}>
                Answers come from an AI and can be wrong — check important numbers in your registers.
              </p>
            </>
          )}
        </section>
      </div>
    </div>
  );
}
