import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import {
  getIous, deleteIou, deleteIouPayment, exportIous, type IouPerson, methodLabel,
  type IouEntry, type IouLinkedTransaction, type IouPage, type IouPayment,
} from '../api/ious';
import IouDialog from '../components/IouDialog';
import IouPaymentDialog from '../components/IouPaymentDialog';
import PaymentDetailsModal from '../components/PaymentDetailsModal';
import {
  getIouSharing, createPayLink, revokePayLinks, dismissPaidNotice, type IouSharing,
} from '../api/ious';
import styles from './Ious.module.css';
import IouPersonDialog from '../components/IouPersonDialog';
import { confirmDialog, promptDialog } from '../utils/dialogs';

type ApiError = { response?: { data?: { message?: string } } };

const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
const fmtDate = (d: string) =>
  new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
const shortDate = (d: string) => new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric' });

/** A small stroke icon (24×24 path data). */
function Icon({ d }: { d: string }) {
  return (
    <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2"
      strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={d} /></svg>
  );
}

type Row =
  | { kind: 'iou'; date: string; id: number; e: IouEntry }
  | { kind: 'payment'; date: string; id: number; p: IouPayment };

type Dialog =
  | { type: 'iou'; entry?: IouEntry }
  | { type: 'payment'; payment?: IouPayment; personId?: number; appliesToEntryId?: number }
  | null;

/** +/− amount, green when they owe the user, red when the user owes. */
function Signed({ n }: { n: number }) {
  if (n === 0) return <span className={styles.muted}>{money(0)}</span>;
  return <span className={n > 0 ? styles.pos : styles.neg}>{n > 0 ? '+' : '−'}{money(Math.abs(n))}</span>;
}

function TxLink({ tx }: { tx?: IouLinkedTransaction | null }) {
  if (!tx) return <span className={styles.muted}>—</span>;
  return (
    <Link to={`/accounts/${tx.accountId}?tx=${tx.id}`} title={`Open in ${tx.accountName}`}>
      {shortDate(tx.date)} · {tx.payee ?? '—'} · {money(tx.amount)}
    </Link>
  );
}

/**
 * IOUs: who owes the user money and whom the user owes. Balances at the
 * top, then a register of IOUs and the payments made against them.
 */
export default function Ious() {
  usePageTitle('IOUs');
  const [data, setData] = useState<IouPage | null>(null);
  const [personId, setPersonId] = useState<number | ''>('');
  const [showPaid, setShowPaid] = useState(false);
  const [dialog, setDialog] = useState<Dialog>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  const load = useCallback(() => {
    getIous({ personId: personId === '' ? undefined : personId, showPaid })
      .then(d => { setData(d); setError(''); })
      .catch(() => setError('Failed to load IOUs.'));
  }, [personId, showPaid]);

  useEffect(() => { load(); }, [load]);

  // Payment pages: your payment details, live links, "I've sent it" notices.
  const [sharing, setSharing] = useState<IouSharing | null>(null);
  const [showDetails, setShowDetails] = useState(false);
  const [linkBusy, setLinkBusy] = useState<string | null>(null);
  const [editPerson, setEditPerson] = useState<IouPerson | null>(null);
  const loadSharing = useCallback(() => { getIouSharing().then(setSharing).catch(() => {}); }, []);
  useEffect(() => { loadSharing(); }, [loadSharing]);

  if (!data) {
    return (
      <div className={styles.page}>
        <h2 className={styles.pageTitle}>IOUs</h2>
        {error ? <p className={styles.error}>{error}</p> : <p className={styles.muted}>Loading…</p>}
      </div>
    );
  }

  const person = personId === '' ? null : data.people.find(p => p.id === personId) ?? null;
  const rows: Row[] = [
    ...data.entries.map(e => ({ kind: 'iou' as const, date: e.date, id: e.id, e })),
    ...data.payments.map(p => ({ kind: 'payment' as const, date: p.date, id: p.id, p })),
  ].sort((a, b) => {
    if (a.date !== b.date) return a.date < b.date ? 1 : -1;
    // Same day: an IOU comes before the payments made against it (newest
    // first on screen, so payments sit above it).
    if (a.kind !== b.kind) return a.kind === 'payment' ? -1 : 1;
    return b.id - a.id;
  });

  // A running balance only makes sense for one person with everything shown.
  const showRunning = person != null && showPaid;
  const running = new Map<string, number>();
  if (showRunning) {
    let bal = 0;
    for (const r of [...rows].reverse()) {
      if (r.kind === 'iou') { if (!r.e.paidOffManually) bal += r.e.amount; }
      else bal += r.p.direction === 'Received' ? -r.p.amount : r.p.amount;
      running.set(`${r.kind}${r.id}`, bal);
    }
  }

  const closeDialog = (saved: boolean) => {
    setDialog(null);
    if (saved) load();
  };

  const removeIou = async (e: IouEntry) => {
    if (!(await confirmDialog(`Delete the ${money(Math.abs(e.amount))} IOU ${e.amount > 0 ? 'from' : 'to'} ${e.personName}${e.description ? ` (${e.description})` : ''}? Payments already applied to it will be re-applied to their other IOUs or kept as credit.`))) return;
    try { await deleteIou(e.id); load(); } catch { setError('Failed to delete that IOU.'); }
  };

  const removePayment = async (p: IouPayment) => {
    if (!(await confirmDialog(`Delete the ${money(p.amount)} payment ${p.direction === 'Received' ? 'from' : 'to'} ${p.personName}? The IOUs it paid will be open again.`))) return;
    try { await deleteIouPayment(p.id); load(); } catch { setError('Failed to delete that payment.'); }
  };

  const linkFor = (id: number) => sharing?.links.find(l => l.personId === id);
  const needDetails = () => {
    if (sharing && sharing.details.methods.length > 0) return false;
    setError('Add at least one way to pay you first (Payment details).');
    setShowDetails(true);
    return true;
  };

  // Puts text — and, where the browser allows, a formatted HTML version — on the
  // clipboard. The write starts straight away (still inside the click, which
  // Safari requires) and is filled in once the content arrives.
  const copyRich = (pending: Promise<{ text: string; html?: string }>): Promise<boolean> => {
    const plainOnly = () => pending.then(r => navigator.clipboard.writeText(r.text)).then(() => true, () => false);
    try {
      if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
        const parts: Record<string, Promise<Blob>> = {
          'text/plain': pending.then(r => new Blob([r.text], { type: 'text/plain' })),
        };
        parts['text/html'] = pending.then(r => new Blob([r.html ?? r.text], { type: 'text/html' }));
        return navigator.clipboard.write([new ClipboardItem(parts)]).then(() => true, plainOnly);
      }
    } catch { /* fall back to plain text */ }
    return plainOnly();
  };

  // A private 30-day link to a page showing what they owe and how to pay you:
  // copied on its own, or as an itemized message (a table + clickable link when
  // pasted into an email, plain text in a text message) ending with it.
  const copyPayLink = async (p: IouPerson, what: 'url' | 'message') => {
    if (needDetails()) return;
    setLinkBusy(`${what}:${p.id}`); setError(''); setNotice('');
    const pending = createPayLink(p.id);
    const copied = copyRich(pending.then(r => what === 'url'
      ? { text: r.url, html: `<a href="${r.url}">${r.url}</a>` }
      : { text: r.message, html: r.html }));
    try {
      const { url, message, expiresAt } = await pending;
      const until = fmtDate(expiresAt.slice(0, 10));
      const text = what === 'url' ? url : message;
      const label = what === 'url' ? `Payment link for ${p.name}` : `Payment request for ${p.name}`;
      if (await copied) {
        setNotice(what === 'url'
          ? `${label} copied. It works until ${until}.`
          : `${label} copied — paste it into a text, chat or email (emails get a formatted table). The link works until ${until}.`);
      } else await promptDialog(`${label} (link works until ${until}):`, { title: what === 'url' ? 'Payment link' : 'Payment request', defaultValue: text, copyOnly: true });
      loadSharing();
    } catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to create the link.'); }
    finally { setLinkBusy(null); }
  };

  // Opens the device's messaging app (sms:) with the itemized message and link;
  // there's no phone number on file, so you pick the recipient there.
  const textPayLinkTo = async (p: IouPerson) => {
    if (needDetails()) return;
    setLinkBusy(`sms:${p.id}`); setError(''); setNotice('');
    try {
      const { message, expiresAt } = await createPayLink(p.id);
      // "sms:?&body=" is understood by both iOS and Android.
      window.location.href = `sms:?&body=${encodeURIComponent(message)}`;
      setNotice(`Opening a text to ${p.name} in your messaging app — choose them as the recipient. The link works until ${fmtDate(expiresAt.slice(0, 10))}. (On a computer without a messaging app, use Copy message instead.)`);
      loadSharing();
    } catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to create the link.'); }
    finally { setLinkBusy(null); }
  };

  // Money Tracker never sends these emails: it opens your own email app with
  // their address, a subject and the itemized message (with the link) filled in,
  // and puts the formatted version (table + clickable link) on the clipboard.
  const emailPayLinkTo = async (p: IouPerson) => {
    if (!p.email || needDetails()) return;
    setLinkBusy(`email:${p.id}`); setError(''); setNotice('');
    const pending = createPayLink(p.id);
    const copied = copyRich(pending.then(r => ({ text: r.message, html: r.html })));
    try {
      const { message, subject, expiresAt } = await pending;
      const rich = await copied;
      const body = message.replace(/\r?\n/g, '\r\n');
      window.location.href = `mailto:${encodeURIComponent(p.email)}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
      const until = fmtDate(expiresAt.slice(0, 10));
      setNotice(rich
        ? `Opening an email to ${p.name} in your email app. A formatted version (a table with a clickable link) is also on your clipboard — select the message text and paste over it if you'd like that instead. The link works until ${until}.`
        : `Opening an email to ${p.name} in your email app. The link works until ${until}.`);
      loadSharing();
    } catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to create the link.'); }
    finally { setLinkBusy(null); }
  };

  const revokeLinks = async () => {
    if (!person) return;
    if (!(await confirmDialog(`Turn off ${person.name}'s payment links? Links already sent will stop working.`, { confirmLabel: 'Turn off', danger: true }))) return;
    setLinkBusy('revoke'); setError('');
    try { await revokePayLinks(person.id); setNotice(`${person.name}'s payment links are turned off.`); loadSharing(); }
    catch { setError('Failed to turn the links off.'); }
    finally { setLinkBusy(null); }
  };

  const dismissNotice = async (id: number) => {
    try { await dismissPaidNotice(id); loadSharing(); } catch { setError('Failed to dismiss.'); }
  };

  // A ready-to-send message listing what's outstanding with one person.
  const copyRequest = async () => {
    if (!person) return;
    const all = await getIous({ personId: person.id });
    const open = all.entries.filter(e => e.paidOffDate == null);
    const theyOwe = person.balance > 0;
    const lines = open
      .filter(e => (theyOwe ? e.amount > 0 : e.amount < 0))
      .sort((a, b) => (a.date < b.date ? -1 : 1))
      .map(e => {
        const paid = e.covered > 0 ? ` (${money(e.covered)} paid, ${money(e.remaining)} left)` : '';
        return `• ${e.description || 'IOU'} (${shortDate(e.date)}): ${money(Math.abs(e.amount))}${paid}`;
      });
    const text = theyOwe
      ? `Hi ${person.name}, here's what's outstanding:\n${lines.join('\n')}\nTotal: ${money(person.balance)}. Thanks!`
      : `Hi ${person.name}, here's what I owe you:\n${lines.join('\n')}\nTotal: ${money(-person.balance)}.`;
    try {
      await navigator.clipboard.writeText(text);
      setNotice('Copied to the clipboard — paste it into a text, email, or payment request.');
    } catch {
      setNotice('');
      await promptDialog('Copy this message:', { title: 'Copy message', defaultValue: text, copyOnly: true });
    }
  };

  const downloadCsv = async () => {
    try {
      const blob = await exportIous(person?.id);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = person ? `ious-${person.name}.csv` : 'ious.csv';
      a.click();
      URL.revokeObjectURL(url);
    } catch { setError('Failed to export.'); }
  };

  const openEntries = data.entries.filter(e => e.paidOffDate == null);

  // Everyone, with balances first (the summary's order), then people who are all square.
  const inSummary = new Set(data.summary.map(p => p.id));
  const everyone = [...data.summary, ...data.people.filter(p => !inSummary.has(p.id)).map(p => ({ ...p, balance: 0, openCount: 0, oldestOpen: null }))];

  // Per-person buttons: copy link, copy message, text, email, edit.
  const personIcons = (p: IouPerson) => {
    const owes = p.balance > 0;
    const busy = linkBusy !== null;
    const noOwe = `${p.name} doesn't owe you anything right now`;
    const emailTip = !owes ? noOwe
      : !p.email ? `No email address for ${p.name} — add one with Edit`
      : `Write an email to ${p.email} in your email app, with what they owe and the payment link`;
    return (
      <span className={styles.iconRow}>
        <button type="button" className={styles.iconBtn} disabled={!owes || busy} onClick={() => void copyPayLink(p, 'url')}
          title={owes ? 'Copy payment link' : noOwe} aria-label={`Copy payment link for ${p.name}`}>
          {linkBusy === `url:${p.id}` ? '…' : <Icon d="M10 13a5 5 0 0 0 7.5.5l3-3a5 5 0 0 0-7-7l-1.7 1.7M14 11a5 5 0 0 0-7.5-.5l-3 3a5 5 0 0 0 7 7l1.7-1.7" />}
        </button>
        <button type="button" className={styles.iconBtn} disabled={!owes || busy} onClick={() => void copyPayLink(p, 'message')}
          title={owes ? 'Copy a text message with what they owe and the link' : noOwe} aria-label={`Copy text message for ${p.name}`}>
          {linkBusy === `message:${p.id}` ? '…' : <Icon d="M9 3h6v3H9zM8 4.5H6a1 1 0 0 0-1 1V20a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V5.5a1 1 0 0 0-1-1h-2M8.5 11h7M8.5 14.5h7M8.5 18h4" />}
        </button>
        <button type="button" className={styles.iconBtn} disabled={!owes || busy} onClick={() => void textPayLinkTo(p)}
          title={owes ? `Text ${p.name}: opens your messaging app with what they owe and the link (pick them as the recipient)` : noOwe}
          aria-label={`Text payment request to ${p.name}`}>
          {linkBusy === `sms:${p.id}` ? '…' : <Icon d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" />}
        </button>
        <button type="button" className={styles.iconBtn} disabled={!owes || !p.email || busy}
          onClick={() => void emailPayLinkTo(p)} title={emailTip} aria-label={`Email payment request to ${p.name}`}>
          {linkBusy === `email:${p.id}` ? '…' : <Icon d="M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM22 6l-10 7L2 6" />}
        </button>
        <button type="button" className={styles.iconBtn} onClick={() => setEditPerson(p)} title={`Edit or delete ${p.name}`} aria-label={`Edit ${p.name}`}>
          <Icon d="M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z" />
        </button>
      </span>
    );
  };

  return (
    <div className={styles.page}>
      <div className={styles.pageHeader}>
        <h2 className={styles.pageTitle}>IOUs</h2>
        <div className={styles.headerActions}>
          <button className={styles.btnPrimary} onClick={() => setDialog({ type: 'iou' })}>+ New IOU</button>
          <button className={styles.btnSecondary} onClick={() => setDialog({ type: 'payment', personId: person?.id })}
            disabled={data.people.length === 0}>+ Record payment</button>
          <button className={styles.btnSecondary} onClick={downloadCsv}>Export CSV</button>
          <button className={styles.btnSecondary} onClick={() => setShowDetails(true)} disabled={!sharing}>Payment details</button>
        </div>
      </div>

      {error && <p className={styles.error}>{error}</p>}
      {notice && <p className={styles.notice} role="status">{notice}</p>}

      {/* "I've sent it" from payment pages. */}
      {sharing?.notices.map(n => (
        <div key={n.id} className={styles.paidNotice} role="status">
          <span>💸 <strong>{n.personName}</strong> says they've sent you {money(n.amount)} with {n.method}
            <span className={styles.muted}> · {new Date(n.createdAt).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}</span></span>
          <span style={{ flex: 1 }} />
          <button className={styles.btnSecondary} onClick={() => setDialog({ type: 'payment', personId: n.personId })}>Record payment</button>
          <button className={styles.btnLink} onClick={() => void dismissNotice(n.id)}>Dismiss</button>
        </div>
      ))}

      {/* ── Summary ── */}
      <section className={styles.summary} aria-label="Balances">
        <div className={styles.totals}>
          <div><span className={styles.totalLabel}>Owed to you</span>
            <strong className={styles.pos}>{money(data.totals.owedToMe)}</strong>
            <span className={styles.muted}> ({data.totals.peopleOwingMe} {data.totals.peopleOwingMe === 1 ? 'person' : 'people'})</span></div>
          <div><span className={styles.totalLabel}>You owe</span>
            <strong className={styles.neg}>{money(Math.abs(data.totals.iOwe))}</strong>
            <span className={styles.muted}> ({data.totals.peopleIOwe} {data.totals.peopleIOwe === 1 ? 'person' : 'people'})</span></div>
          <div><span className={styles.totalLabel}>Net</span>
            <strong className={data.totals.owedToMe + data.totals.iOwe >= 0 ? styles.pos : styles.neg}>
              {data.totals.owedToMe + data.totals.iOwe >= 0 ? '+' : '−'}{money(Math.abs(data.totals.owedToMe + data.totals.iOwe))}
            </strong></div>
        </div>
        {everyone.length === 0 ? (
          <p className={styles.muted}>No people yet — add someone with + New IOU.</p>
        ) : (
          <div className={styles.summaryScroll}><table className={styles.table}>
            <thead>
              <tr><th>Person</th><th className={styles.right}>Balance</th><th className={styles.narrowHide}>Open IOUs</th><th className={styles.narrowHide}>Oldest open</th><th><span className={styles.srOnly}>Actions</span></th></tr>
            </thead>
            <tbody>
              {everyone.map(p => (
                <tr key={p.id} className={`${styles.clickable} ${p.id === personId ? styles.selected : ''}`}
                  onClick={() => setPersonId(p.id === personId ? '' : p.id)} title="Show only this person">
                  <td>{p.name}</td>
                  <td className={`${styles.right} ${p.balance > 0 ? styles.pos : p.balance < 0 ? styles.neg : styles.muted}`}>
                    {p.balance === 0 ? 'All square' : <>{p.balance > 0 ? '+' : '−'}{money(Math.abs(p.balance))}
                      <span className={styles.balanceWord}>{p.balance > 0 ? 'owes you' : 'you owe'}</span></>}
                  </td>
                  <td className={styles.narrowHide}>{p.openCount || '—'}</td>
                  <td className={styles.narrowHide}>{p.oldestOpen ? fmtDate(p.oldestOpen) : '—'}</td>
                  <td className={styles.iconCell} onClick={e => e.stopPropagation()}>{personIcons(p)}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
        )}
      </section>

      {/* ── Filters ── */}
      <div className={styles.filters}>
        <label>
          Person{' '}
          <select value={personId} onChange={e => setPersonId(e.target.value === '' ? '' : Number(e.target.value))}>
            <option value="">Everyone</option>
            {data.people.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
        </label>
        <label className={styles.check}>
          <input type="checkbox" checked={showPaid} onChange={e => setShowPaid(e.target.checked)} /> Show paid off
        </label>
        {person && (
          <span className={styles.personActions}>
            {person.balance !== 0 && <button className={styles.btnSecondary} onClick={copyRequest}>Copy request</button>}
            {linkFor(person.id) && (
              <span className={styles.muted}>
                Link active until {fmtDate(linkFor(person.id)!.expiresAt.slice(0, 10))}{' '}
                <button className={styles.btnLinkDanger} onClick={() => void revokeLinks()} disabled={linkBusy !== null}>Turn off</button>
              </span>
            )}
          </span>
        )}
      </div>

      {/* ── Register ── */}
      {rows.length === 0 ? (
        <p className={styles.muted}>
          {showPaid ? 'Nothing recorded yet.' : 'Nothing open. Tick “Show paid off” to see settled IOUs.'}
        </p>
      ) : (
        <div className={styles.tableWrap}>
          <table className={styles.table}>
            <thead>
              <tr>
                <th>Date</th>
                {!person && <th>Person</th>}
                <th>Type</th>
                <th>Description / notes</th>
                <th>Linked transaction</th>
                <th className={styles.right}>Amount</th>
                <th>Paid back</th>
                <th>Paid off</th>
                {showRunning && <th className={styles.right}>Balance</th>}
                <th></th>
              </tr>
            </thead>
            <tbody>
              {rows.map(r => r.kind === 'iou' ? (
                <tr key={`i${r.id}`} className={r.e.paidOffDate ? styles.settled : undefined}>
                  <td className={styles.nowrap}>{fmtDate(r.e.date)}</td>
                  {!person && <td>{r.e.personName}</td>}
                  <td className={`${styles.nowrap} ${r.e.amount > 0 ? styles.pos : styles.neg}`}>{r.e.amount > 0 ? 'Owes you' : 'You owe'}</td>
                  <td>
                    {r.e.description || <span className={styles.muted}>—</span>}
                    {r.e.notes && <div className={styles.notes}>{r.e.notes}</div>}
                  </td>
                  <td><TxLink tx={r.e.transaction} /></td>
                  <td className={`${styles.right} ${styles.nowrap} ${r.e.amount > 0 ? styles.pos : styles.neg}`}>
                    {r.e.amount > 0 ? '+' : '−'}{money(Math.abs(r.e.amount))}
                  </td>
                  <td className={styles.nowrap}>
                    {r.e.paidOffManually ? <span className={styles.muted}>settled by hand</span>
                      : r.e.covered > 0 ? <>{money(r.e.covered)} of {money(Math.abs(r.e.amount))}</>
                        : <span className={styles.muted}>—</span>}
                  </td>
                  <td className={styles.nowrap}>{r.e.paidOffDate ? fmtDate(r.e.paidOffDate) : <span className={styles.open}>Open</span>}</td>
                  {showRunning && <td className={`${styles.right} ${styles.nowrap}`}><Signed n={running.get(`iou${r.id}`) ?? 0} /></td>}
                  <td className={styles.rowActions}>
                    {!r.e.paidOffDate && (
                      <button className={styles.btnLink} onClick={() => setDialog({
                        type: 'payment', personId: r.e.personId, appliesToEntryId: r.e.id,
                      })} title={r.e.amount > 0 ? 'Record money received for this IOU' : 'Record paying this IOU'}>
                        {r.e.amount > 0 ? 'Received' : 'Paid'}
                      </button>
                    )}
                    <button className={styles.btnLink} onClick={() => setDialog({ type: 'iou', entry: r.e })}>Edit</button>
                    <button className={styles.btnLinkDanger} onClick={() => removeIou(r.e)}>Delete</button>
                  </td>
                </tr>
              ) : (
                <tr key={`p${r.id}`} className={styles.paymentRow}>
                  <td className={styles.nowrap}>{fmtDate(r.p.date)}</td>
                  {!person && <td>{r.p.personName}</td>}
                  <td>{r.p.direction === 'Received' ? 'Received' : 'Paid'} · {methodLabel(r.p.method)}</td>
                  <td>{r.p.notes || <span className={styles.muted}>—</span>}</td>
                  <td><TxLink tx={r.p.transaction} /></td>
                  <td className={`${styles.right} ${styles.nowrap}`}>
                    {r.p.direction === 'Received' ? '−' : '+'}{money(r.p.amount)}
                  </td>
                  <td className={styles.nowrap}>
                    {r.p.allocated < r.p.amount
                      ? <span title="Not needed by any open IOU; kept as credit">{money(r.p.amount - r.p.allocated)} credit</span>
                      : <span className={styles.muted}>applied</span>}
                  </td>
                  <td></td>
                  {showRunning && <td className={`${styles.right} ${styles.nowrap}`}><Signed n={running.get(`payment${r.id}`) ?? 0} /></td>}
                  <td className={styles.rowActions}>
                    <button className={styles.btnLink} onClick={() => setDialog({ type: 'payment', payment: r.p })}>Edit</button>
                    <button className={styles.btnLinkDanger} onClick={() => removePayment(r.p)}>Delete</button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {dialog?.type === 'iou' && (
        <IouDialog people={data.people} entry={dialog.entry} onSaved={() => closeDialog(true)} onClose={() => closeDialog(false)} />
      )}
      {showDetails && sharing && (
        <PaymentDetailsModal details={sharing.details} onClose={() => setShowDetails(false)}
          onSaved={d => { setSharing({ ...sharing, details: d }); setShowDetails(false); setError(''); setNotice('Payment details saved.'); }} />
      )}
      {dialog?.type === 'payment' && (
        <IouPaymentDialog
          people={data.people}
          openEntries={openEntries}
          payment={dialog.payment}
          personId={dialog.personId}
          appliesToEntryId={dialog.appliesToEntryId}
          onSaved={() => closeDialog(true)}
          onClose={() => closeDialog(false)}
        />
      )}
      {editPerson && (
        <IouPersonDialog person={editPerson} onClose={() => setEditPerson(null)}
          onDone={what => {
            setEditPerson(null);
            if (what === 'deleted') { setNotice(`${editPerson.name} was deleted.`); if (personId === editPerson.id) setPersonId(''); }
            load(); loadSharing();
          }} />
      )}
    </div>
  );
}
