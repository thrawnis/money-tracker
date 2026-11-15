import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  findTransferMatches, linkTransferMatches, dismissTransferMatch, restoreDismissedTransferMatches,
  type TransferMatch, type TransferSide,
} from '../api/transferMatches';
import styles from '../pages/Settings.module.css';
import anim from './PossibleTransfersTab.module.css';

type ApiError = { response?: { data?: { message?: string } } };
const errMsg = (err: unknown, fallback: string) => (err as ApiError)?.response?.data?.message ?? fallback;

const money = (n: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
const day = (d: string) => new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
const pairKey = (m: TransferMatch) => `${m.from.id}-${m.to.id}`;

function SideRow({ side }: { side: TransferSide }) {
  const out = side.amount < 0;
  return (
    <tr>
      <td className={styles.stackInline} style={{ whiteSpace: 'nowrap' }}>{day(side.date)}</td>
      <td data-label="Account">
        {/* Money out of this account (red) or into it (green). */}
        <strong style={{ color: out ? 'var(--c-cc0000)' : 'var(--c-1a7a40)', marginRight: 6 }}>{out ? 'Out' : 'In'}</strong>
        <Link to={`/accounts/${side.accountId}`}>{side.accountName}</Link>
      </td>
      <td data-label="Payee">{side.payee || '—'}</td>
      <td data-label="Memo">{side.memo || '—'}</td>
      <td data-label="Category">{side.category ?? <span className={styles.hint} style={{ margin: 0 }}>Uncategorized</span>}</td>
      <td data-label="Amount" style={{ textAlign: 'right', whiteSpace: 'nowrap', color: side.amount < 0 ? 'var(--c-cc0000)' : 'var(--c-1a7a40)' }}>{money(side.amount)}</td>
    </tr>
  );
}

/**
 * Utilities → Possible Transfers: pairs of ordinary transactions that look like
 * the two sides of a transfer between your accounts (money out of one, the same
 * amount into another within a few days). Link them as a transfer, or mark
 * them "Not a transfer" so they aren't suggested again.
 */
export default function PossibleTransfersTab() {
  const [days, setDays] = useState(3);
  const [matches, setMatches] = useState<TransferMatch[] | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  // The action in progress ('restore', or a pair key + ':link'/':dismiss');
  // held until the list has reloaded, so a pair doesn't reappear clickable.
  const [busy, setBusy] = useState<string | null>(null);
  // The pair animating out after a successful link / dismiss.
  const [leaving, setLeaving] = useState<{ key: string; kind: 'linked' | 'dismissed' } | null>(null);

  const reload = async () => {
    const d = await findTransferMatches(days);
    setMatches(d.matches);
  };

  useEffect(() => {
    let cancelled = false;
    findTransferMatches(days)
      .then(d => { if (!cancelled) { setMatches(d.matches); setError(''); } })
      .catch(() => { if (!cancelled) setError('Failed to look for possible transfers.'); });
    return () => { cancelled = true; };
  }, [days]);

  // leave: animate that pair out (after the action succeeds) before the list reloads.
  const run = async (key: string, action: () => Promise<string>, leave?: { key: string; kind: 'linked' | 'dismissed' }) => {
    setBusy(key); setError(''); setNotice('');
    try {
      const message = await action();
      if (leave) {
        setLeaving(leave);
        const reduced = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
        await new Promise(r => setTimeout(r, reduced ? 300 : 900));
      }
      await reload();
      setLeaving(null);
      setNotice(message);
    } catch (err) {
      setError(errMsg(err, 'Something went wrong.'));
      setLeaving(null);
    } finally {
      setBusy(null);
    }
  };

  const link = (m: TransferMatch) => run(`${pairKey(m)}:link`, async () => {
    const r = await linkTransferMatches([{ outId: m.from.id, inId: m.to.id }]);
    return `Linked ${r.linked} transfer${r.linked === 1 ? '' : 's'}.`;
  }, { key: pairKey(m), kind: 'linked' });

  const dismiss = (m: TransferMatch) => run(`${pairKey(m)}:dismiss`, async () => {
    await dismissTransferMatch(m.from.id, m.to.id);
    return 'Marked as not a transfer.';
  }, { key: pairKey(m), kind: 'dismissed' });

  const restore = () => {
    if (!confirm('Suggest again every pair you marked "Not a transfer"?')) return;
    void run('restore', async () => {
      const r = await restoreDismissedTransferMatches();
      return `${r.restored} dismissed pair${r.restored === 1 ? '' : 's'} will be suggested again (if they still match).`;
    });
  };

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Pairs of transactions that look like the two sides of a transfer between your own accounts but were entered
        or imported as separate transactions: money out of one account and the same amount into another within a few
        days (e.g. a credit card payment, or moving money to savings). <strong>Link as transfer</strong> joins them
        like a transfer you'd entered yourself — their categories are cleared, everything else is kept.
        <strong> Not a transfer</strong> stops suggesting that pair. Transfers, voided and split transactions aren't
        considered, and each transaction is suggested in at most one pair (the closest dates first).
      </p>

      <div style={{ display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap', marginBottom: 12 }}>
        <label style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}>
          Dates within
          <select value={days} onChange={e => setDays(Number(e.target.value))} aria-label="Days apart">
            {[0, 1, 2, 3, 4, 5].map(d => <option key={d} value={d}>{d === 0 ? 'the same day' : `${d} day${d === 1 ? '' : 's'}`}</option>)}
          </select>
        </label>
        <span style={{ flex: 1 }} />
        <button className={styles.btnSecondary} onClick={restore} disabled={busy !== null}>
          {busy === 'restore' ? 'Restoring…' : 'Suggest dismissed pairs again'}
        </button>
      </div>

      {error && <div className={styles.error}>{error}</div>}
      {notice && <p className={styles.hint} style={{ color: '#1a7a40' }}>{notice}</p>}
      {!matches && !error && <p className={styles.hint}>Looking for possible transfers…</p>}
      {matches && matches.length === 0 && <p className={styles.hint}>No possible transfers found.</p>}

      {matches && matches.length > 0 && (
        <>
          <p style={{ margin: '0 0 10px' }}>
            <strong>{matches.length} possible transfer{matches.length === 1 ? '' : 's'}</strong>
          </p>
          {matches.map(m => {
            const k = pairKey(m);
            const out = leaving?.key === k ? leaving.kind : null;
            return (
              <div key={k} style={{ marginBottom: 10 }}
                className={`${styles.prefGroup} ${anim.pairCard} ${out ? `${anim.leaving} ${out === 'linked' ? anim.linked : anim.dismissed}` : ''}`}>
                {out && (
                  <div className={`${anim.stamp} ${out === 'linked' ? anim.stampLinked : anim.stampDismissed}`} role="status">
                    {out === 'linked' ? '✓ Linked as transfer' : 'Hidden — not a transfer'}
                  </div>
                )}
                <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap', marginBottom: 6 }}>
                  <strong>{money(Math.abs(m.from.amount))}</strong>
                  <span>{m.from.accountName} → {m.to.accountName}</span>
                  <span className={styles.hint} style={{ margin: 0 }}>
                    {m.dayGap === 0 ? 'same day' : `${m.dayGap} day${m.dayGap === 1 ? '' : 's'} apart`}
                  </span>
                  {m.alternatives > 0 && (
                    <span style={{ color: 'var(--c-b8860b)', fontSize: 13 }}>
                      ⚠ {m.alternatives} other possible match{m.alternatives === 1 ? '' : 'es'} — check it's the right pair
                    </span>
                  )}
                  <span style={{ flex: 1 }} />
                  <button className={styles.btnPrimary} disabled={busy !== null} onClick={() => void link(m)}>
                    {busy === `${k}:link` ? <><span className={anim.spinner} aria-hidden="true" />Linking…</> : 'Link as transfer'}
                  </button>
                  <button className={styles.btnSecondary} disabled={busy !== null} onClick={() => void dismiss(m)}>
                    {busy === `${k}:dismiss` ? <><span className={anim.spinner} aria-hidden="true" />Hiding…</> : 'Not a transfer'}
                  </button>
                </div>
                <table className={`${styles.dupTable} ${styles.stackOnPhone}`}>
                  <thead>
                    <tr><th>Date</th><th>Account</th><th>Payee</th><th>Memo</th><th>Category</th><th style={{ textAlign: 'right' }}>Amount</th></tr>
                  </thead>
                  <tbody>
                    <SideRow side={m.from} />
                    <SideRow side={m.to} />
                  </tbody>
                </table>
              </div>
            );
          })}
        </>
      )}
    </div>
  );
}
