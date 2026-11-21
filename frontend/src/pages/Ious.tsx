import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { usePageTitle } from '../hooks/usePageTitle';
import {
  getIous, deleteIou, deleteIouPayment, exportIous, updateIouPerson, deleteIouPerson, methodLabel,
  type IouEntry, type IouLinkedTransaction, type IouPage, type IouPayment,
} from '../api/ious';
import IouDialog from '../components/IouDialog';
import IouPaymentDialog from '../components/IouPaymentDialog';
import PaymentDetailsModal from '../components/PaymentDetailsModal';
import {
  getIouSharing, createPayLink, emailPayLink, revokePayLinks, dismissPaidNotice, type IouSharing,
} from '../api/ious';
import styles from './Ious.module.css';
import { confirmDialog, promptDialog } from '../utils/dialogs';

type ApiError = { response?: { data?: { message?: string } } };

const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
const fmtDate = (d: string) =>
  new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
const shortDate = (d: string) => new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric' });

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
  const [linkBusy, setLinkBusy] = useState<'copy' | 'email' | 'revoke' | null>(null);
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

  const renamePerson = async () => {
    if (!person) return;
    const name = (await promptDialog('Rename this person:', { title: 'Rename', defaultValue: person.name, confirmLabel: 'Save' }))?.trim();
    if (!name || name === person.name) return;
    try { await updateIouPerson(person.id, name, person.notes ?? undefined); load(); }
    catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to rename.'); }
  };

  const setEmail = async () => {
    if (!person) return;
    const email = await promptDialog(`Email address for ${person.name} (for sending payment links):`,
      { title: 'Email address', defaultValue: person.email ?? '', placeholder: 'name@example.com', confirmLabel: 'Save' });
    if (email == null || email.trim() === (person.email ?? '')) return;
    try { await updateIouPerson(person.id, person.name, person.notes ?? undefined, email.trim()); load(); }
    catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to save the email address.'); }
  };

  const linkFor = (id: number) => sharing?.links.find(l => l.personId === id);
  const needDetails = () => {
    if (sharing && sharing.details.methods.length > 0) return false;
    setError('Add at least one way to pay you first (Payment details).');
    setShowDetails(true);
    return true;
  };

  // A private 30-day link to a page showing what they owe and how to pay you.
  const copyPayLink = async () => {
    if (!person || needDetails()) return;
    setLinkBusy('copy'); setError(''); setNotice('');
    try {
      const { message, expiresAt } = await createPayLink(person.id);
      const until = fmtDate(expiresAt.slice(0, 10));
      // The itemized message with the link, ready to paste into a text or chat.
      try { await navigator.clipboard.writeText(message); setNotice(`Payment request for ${person.name} copied — paste it into a text or message. The link works until ${until}.`); }
      catch { await promptDialog(`Payment request for ${person.name} (link works until ${until}):`, { title: 'Payment request', defaultValue: message, copyOnly: true }); }
      loadSharing();
    } catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to create the link.'); }
    finally { setLinkBusy(null); }
  };

  const emailPayLinkTo = async () => {
    if (!person || needDetails()) return;
    if (!person.email) { await setEmail(); return; }
    if (!(await confirmDialog(`Email ${person.name} (${person.email}) a link to see what they owe you (${money(person.balance)}) and pay you?`,
      { title: 'Email payment link', confirmLabel: 'Send' }))) return;
    setLinkBusy('email'); setError(''); setNotice('');
    try {
      const r = await emailPayLink(person.id);
      setNotice(`Sent to ${r.sentTo}. The link works until ${fmtDate(r.expiresAt.slice(0, 10))}.`);
      loadSharing();
    } catch (err) { setError((err as ApiError)?.response?.data?.message ?? 'Failed to send the email.'); }
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

  const removePerson = async () => {
    if (!person) return;
    if (!(await confirmDialog(`Delete ${person.name} and all of their IOUs and payments? This can't be undone.`))) return;
    try { await deleteIouPerson(person.id); setPersonId(''); } catch { setError('Failed to delete that person.'); }
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
        {data.summary.length === 0 ? (
          <p className={styles.muted}>All square — nobody owes anything right now.</p>
        ) : (
          <table className={styles.table}>
            <thead>
              <tr><th>Person</th><th className={styles.right}>Balance</th><th>Open IOUs</th><th>Oldest open</th></tr>
            </thead>
            <tbody>
              {data.summary.map(p => (
                <tr key={p.id} className={`${styles.clickable} ${p.id === personId ? styles.selected : ''}`}
                  onClick={() => setPersonId(p.id === personId ? '' : p.id)} title="Show only this person">
                  <td>{p.name}</td>
                  <td className={`${styles.right} ${p.balance > 0 ? styles.pos : styles.neg}`}>
                    {p.balance > 0 ? '+' : '−'}{money(Math.abs(p.balance))}
                    <span className={styles.balanceWord}>{p.balance > 0 ? 'owes you' : 'you owe'}</span>
                  </td>
                  <td>{p.openCount}</td>
                  <td>{p.oldestOpen ? fmtDate(p.oldestOpen) : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
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
            {person.balance > 0 && <>
              <button className={styles.btnSecondary} onClick={() => void copyPayLink()} disabled={linkBusy !== null}
                title="A private page showing what they owe and how to pay you (works for 30 days)">
                {linkBusy === 'copy' ? 'Creating…' : 'Copy pay link'}
              </button>
              <button className={styles.btnSecondary} onClick={() => void emailPayLinkTo()} disabled={linkBusy !== null || (sharing != null && !sharing.canEmail)}
                title={sharing && !sharing.canEmail ? 'Email isn\'t set up on the server' : person.email ? `Email the link to ${person.email}` : 'Add their email address first'}>
                {linkBusy === 'email' ? 'Sending…' : 'Email pay link'}
              </button>
            </>}
            {linkFor(person.id) && (
              <span className={styles.muted}>
                Link active until {fmtDate(linkFor(person.id)!.expiresAt.slice(0, 10))}{' '}
                <button className={styles.btnLinkDanger} onClick={() => void revokeLinks()} disabled={linkBusy !== null}>Turn off</button>
              </span>
            )}
            <button className={styles.btnLink} onClick={() => void setEmail()}>{person.email ? `Email: ${person.email}` : 'Add email'}</button>
            <button className={styles.btnLink} onClick={renamePerson}>Rename</button>
            <button className={styles.btnLinkDanger} onClick={removePerson}>Delete person</button>
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
    </div>
  );
}
