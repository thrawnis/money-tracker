import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { baseURL } from '../api/client';
import { usePageTitle } from '../hooks/usePageTitle';
import { promptDialog } from '../utils/dialogs';
import styles from './Pay.module.css';

interface PayOption { type: string; name: string; display: string; url: string | null; copy: string | null; hint: string }
interface PayPage {
  from: string; to: string; total: number; itemsTotal: number;
  items: { date: string; payee: string; amount: number }[];
  options: PayOption[]; expiresAt: string;
}

const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
const day = (d: string) => new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });

/**
 * The public IOU payment page (/pay/:token), opened from a link someone was
 * sent: what they owe (date, payee, amount), the total, and one-tap ways to
 * pay. No sign-in; the private, expiring link is the key.
 */
export default function Pay() {
  usePageTitle('Money Tracker');
  const { token = '' } = useParams();
  const [page, setPage] = useState<PayPage | null>(null);
  const [error, setError] = useState('');
  const [copied, setCopied] = useState<string | null>(null);
  const [chosen, setChosen] = useState<string>('');
  const [sent, setSent] = useState<'no' | 'sending' | 'yes'>('no');

  // Private page: keep it out of search engines and don't pass the link on to payment sites.
  useEffect(() => {
    const metas = [['robots', 'noindex, nofollow'], ['referrer', 'no-referrer']].map(([name, content]) => {
      const m = document.createElement('meta'); m.name = name; m.content = content; document.head.appendChild(m); return m;
    });
    return () => metas.forEach(m => m.remove());
  }, []);

  useEffect(() => {
    let stale = false;
    fetch(`${baseURL}/pay/${encodeURIComponent(token)}`, { credentials: 'omit', referrerPolicy: 'no-referrer' })
      .then(async r => {
        const body = await r.json().catch(() => null);
        if (stale) return;
        if (!r.ok) setError(body?.message ?? (r.status === 429 ? 'Too many requests — try again in a minute.' : `This page couldn't be loaded (error ${r.status}).`));
        else if (!body || !Array.isArray(body.items) || !Array.isArray(body.options))
          // Reached something, but not the payment details (e.g. a proxy answering for the server).
          setError(`This page couldn't be loaded: the server sent an unexpected reply (${r.headers.get('content-type') ?? 'no content type'}).`);
        else { setPage(body as PayPage); setChosen((body as PayPage).options[0]?.type ?? ''); }
      })
      .catch((err: unknown) => {
        // The request never got an answer: offline, or blocked by the browser (an ad or content blocker, for example).
        if (!stale) setError(`This page couldn't be loaded — the request was blocked or couldn't reach the server (${err instanceof Error ? err.message : String(err)}). Check your connection, or try turning off an ad/content blocker for this site.`);
      });
    return () => { stale = true; };
  }, [token]);

  const copy = async (o: PayOption) => {
    setChosen(o.type);
    try { await navigator.clipboard.writeText(o.copy ?? ''); setCopied(o.type); setTimeout(() => setCopied(c => (c === o.type ? null : c)), 2500); }
    catch { await promptDialog(`${o.name} — copy this:`, { title: o.name, defaultValue: o.copy ?? '', copyOnly: true }); }
  };

  const markSent = async () => {
    setSent('sending');
    try {
      const r = await fetch(`${baseURL}/pay/${encodeURIComponent(token)}/sent`, {
        method: 'POST', credentials: 'omit', referrerPolicy: 'no-referrer',
        headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ method: chosen || 'other' }),
      });
      setSent(r.ok ? 'yes' : 'no');
      if (!r.ok) setError('That didn\'t go through — please try again.');
    } catch { setSent('no'); setError('That didn\'t go through — check your connection.'); }
  };

  if (error && !page) {
    return <main className={styles.page}><div className={styles.card}><h1 className={styles.title}>Payment request</h1><p className={styles.muted}>{error}</p></div></main>;
  }
  if (!page) return <main className={styles.page}><div className={styles.card}><p className={styles.muted}>Loading…</p></div></main>;

  const credit = page.itemsTotal - page.total;
  return (
    <main className={styles.page}>
      <div className={styles.card}>
        <p className={styles.kicker}>Payment request</p>
        <h1 className={styles.title}>Hi {page.to}, here's what you owe {page.from}</h1>

        {page.total <= 0 ? (
          <p className={styles.allSquare}>You're all square — nothing is owed right now. 🎉</p>
        ) : (
          <>
            <ul className={styles.items}>
              {page.items.map((i, k) => (
                <li key={k}>
                  <span className={styles.date}>{day(i.date)}</span>
                  <span className={styles.payee}>{i.payee}</span>
                  <span className={styles.amount}>{money(i.amount)}</span>
                </li>
              ))}
              {credit > 0.004 && (
                <li className={styles.credit}>
                  <span className={styles.date} />
                  <span className={styles.payee}>Less what {page.from} owes you or you've already paid</span>
                  <span className={styles.amount}>−{money(credit)}</span>
                </li>
              )}
            </ul>

            <div className={styles.total}>
              <span>Total</span>
              <strong>{money(page.total)}</strong>
            </div>

            <h2 className={styles.subtitle}>Pay {page.from}</h2>
            {page.options.length === 0 && <p className={styles.muted}>{page.from} hasn't added any payment details yet.</p>}
            <div className={styles.options}>
              {page.options.map(o => (
                <div key={o.type + o.display} className={styles.option}>
                  <div className={styles.optionText}>
                    <span className={styles.optionName}>{o.name}</span>
                    <span className={styles.optionDisplay}>{o.display}</span>
                    <span className={styles.optionHint}>{o.hint}</span>
                  </div>
                  {o.url ? (
                    <a className={styles.payBtn} href={o.url} target="_blank" rel="noopener noreferrer" onClick={() => setChosen(o.type)}>
                      Pay {money(page.total)}
                    </a>
                  ) : (
                    <button className={styles.copyBtn} onClick={() => void copy(o)}>{copied === o.type ? '✓ Copied' : 'Copy'}</button>
                  )}
                </div>
              ))}
            </div>

            {page.options.length > 0 && (
              <div className={styles.sent}>
                {sent === 'yes' ? (
                  <p className={styles.thanks}>Thanks! {page.from} has been told you've paid.</p>
                ) : (
                  <>
                    <p className={styles.muted}>Once you've paid, let {page.from} know:</p>
                    <div className={styles.sentRow}>
                      <select value={chosen} onChange={e => setChosen(e.target.value)} aria-label="How you paid">
                        {page.options.map(o => <option key={o.type + o.display} value={o.type}>{o.name}</option>)}
                      </select>
                      <button className={styles.sentBtn} onClick={() => void markSent()} disabled={sent === 'sending'}>
                        {sent === 'sending' ? 'Sending…' : "I've sent it"}
                      </button>
                    </div>
                  </>
                )}
                {error && <p className={styles.error}>{error}</p>}
              </div>
            )}
          </>
        )}
        <p className={styles.footer}>This private link works until {day(page.expiresAt.slice(0, 10))}. Sent with Money Tracker.</p>
      </div>
    </main>
  );
}
