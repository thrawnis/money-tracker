import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  findTransferMatches, linkTransferMatches, dismissTransferMatch, restoreDismissedTransferMatches,
  type TransferGroup, type TransferMatch, type TransferSide,
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

type Status = 'linked' | 'dismissed' | 'conflict';

/** The two rows of one possible pair. */
function PairTable({ m }: { m: TransferMatch }) {
  return (
    <table className={`${styles.dupTable} ${styles.stackOnPhone}`}>
      <thead>
        <tr><th>Date</th><th>Account</th><th>Payee</th><th>Memo</th><th>Category</th><th style={{ textAlign: 'right' }}>Amount</th></tr>
      </thead>
      <tbody>
        <SideRow side={m.from} />
        <SideRow side={m.to} />
      </tbody>
    </table>
  );
}

const gapText = (m: TransferMatch) => m.dayGap === 0 ? 'same day' : `${m.dayGap} day${m.dayGap === 1 ? '' : 's'} apart`;

function StatusBadge({ status }: { status: Status }) {
  const text = status === 'linked' ? '✓ Linked as transfer' : status === 'dismissed' ? 'Not a transfer' : 'Not used — matched elsewhere';
  return (
    <span role="status" className={`${anim.badge} ${status === 'linked' ? anim.badgeLinked : anim.badgeDismissed}`}>{text}</span>
  );
}

const Spin = ({ label }: { label: string }) => <><span className={anim.spinner} aria-hidden="true" />{label}</>;

/**
 * Utilities → Possible Transfers: pairs of ordinary transactions that look like
 * the two sides of a transfer between your accounts (money out of one, the same
 * amount into another within a few days). Link them as a transfer, or mark
 * them "Not a transfer" so they aren't suggested again. When a transaction
 * could pair with more than one other, the options are shown together and the
 * user picks the right one (if any). Handled pairs stay in place, dimmed, so
 * the list doesn't move while you work down it.
 */
export default function PossibleTransfersTab() {
  const [days, setDays] = useState(3);
  const [groups, setGroups] = useState<TransferGroup[] | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  // The action in progress ('restore', or a key + ':link'/':dismiss').
  const [busy, setBusy] = useState<string | null>(null);
  // Pairs handled on this screen (by pair key); cleared when the list is fetched again.
  const [done, setDone] = useState<Record<string, 'linked' | 'dismissed'>>({});

  const reload = async () => {
    const d = await findTransferMatches(days);
    setGroups(d.groups);
    setDone({});
  };

  useEffect(() => {
    let cancelled = false;
    findTransferMatches(days)
      .then(d => { if (!cancelled) { setGroups(d.groups); setDone({}); setError(''); } })
      .catch(() => { if (!cancelled) setError('Failed to look for possible transfers.'); });
    return () => { cancelled = true; };
  }, [days]);

  // An option's state: handled, or ruled out because one of its transactions
  // was linked in another option of the same group.
  const statusOf = (g: TransferGroup, m: TransferMatch): Status | null => {
    const own = done[pairKey(m)];
    if (own) return own;
    const ids = [m.from.id, m.to.id];
    return g.options.some(o => done[pairKey(o)] === 'linked' && (ids.includes(o.from.id) || ids.includes(o.to.id)))
      ? 'conflict' : null;
  };
  const pending = (g: TransferGroup) => g.options.filter(o => statusOf(g, o) === null);

  const run = async (key: string, action: () => Promise<void>) => {
    setBusy(key); setError(''); setNotice('');
    try { await action(); }
    catch (err) { setError(errMsg(err, 'Something went wrong.')); }
    finally { setBusy(null); }
  };

  const link = (m: TransferMatch) => run(`${pairKey(m)}:link`, async () => {
    await linkTransferMatches([{ outId: m.from.id, inId: m.to.id }]);
    setDone(d => ({ ...d, [pairKey(m)]: 'linked' }));
  });

  // Marks every given pair "Not a transfer" (one, or the rest of a group).
  const dismiss = (key: string, pairs: TransferMatch[]) => run(`${key}:dismiss`, async () => {
    for (const m of pairs) {
      await dismissTransferMatch(m.from.id, m.to.id);
      setDone(d => ({ ...d, [pairKey(m)]: 'dismissed' }));
    }
  });

  const restore = () => {
    if (!confirm('Suggest again every pair you marked "Not a transfer"?')) return;
    void run('restore', async () => {
      const r = await restoreDismissedTransferMatches();
      await reload();
      setNotice(`${r.restored} dismissed pair${r.restored === 1 ? '' : 's'} will be suggested again (if they still match).`);
    });
  };

  const handled = groups?.filter(g => pending(g).length === 0).length ?? 0;
  const linkedCount = Object.values(done).filter(k => k === 'linked').length;
  const groupKey = (g: TransferGroup) => g.options.map(pairKey).join('_');

  return (
    <div className={styles.tabSection}>
      <p className={styles.hint}>
        Pairs of transactions that look like the two sides of a transfer between your own accounts but were entered
        or imported as separate transactions: money out of one account and the same amount into another within a few
        days (e.g. a credit card payment, or moving money to savings). <strong>Link as transfer</strong> joins them
        like a transfer you'd entered yourself — their categories are cleared, everything else is kept.
        <strong> Not a transfer</strong> stops suggesting that pair. When a transaction could pair with more than one
        other, the possible matches are shown together so you can pick the right one, if any. Transfers, voided and
        split transactions aren't considered.
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
      {!groups && !error && <p className={styles.hint}>Looking for possible transfers…</p>}
      {groups && groups.length === 0 && <p className={styles.hint}>No possible transfers found.</p>}

      {groups && groups.length > 0 && (
        <>
          <p style={{ margin: '0 0 10px' }}>
            <strong>{groups.length} possible transfer{groups.length === 1 ? '' : 's'}</strong>
            {handled > 0 && (
              <span className={styles.hint} style={{ margin: '0 0 0 8px' }}>
                {handled} done ({linkedCount} linked), {groups.length - handled} left
              </span>
            )}
          </p>
          {groups.map(g => {
            const gk = groupKey(g);
            const left = pending(g);

            // One possible pair: link it or not.
            if (g.options.length === 1) {
              const m = g.options[0];
              const k = pairKey(m);
              const state = statusOf(g, m);
              return (
                <div key={gk} style={{ marginBottom: 10 }} className={`${styles.prefGroup} ${anim.pairCard} ${state ? anim.done : ''}`}>
                  <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap', marginBottom: 6 }}>
                    <strong>{money(Math.abs(m.from.amount))}</strong>
                    <span>{m.from.accountName} → {m.to.accountName}</span>
                    <span className={styles.hint} style={{ margin: 0 }}>{gapText(m)}</span>
                    <span style={{ flex: 1 }} />
                    {state ? <StatusBadge status={state} /> : <>
                      <button className={styles.btnPrimary} disabled={busy !== null} onClick={() => void link(m)}>
                        {busy === `${k}:link` ? <Spin label="Linking…" /> : 'Link as transfer'}
                      </button>
                      <button className={styles.btnSecondary} disabled={busy !== null} onClick={() => void dismiss(k, [m])}>
                        {busy === `${k}:dismiss` ? <Spin label="Saving…" /> : 'Not a transfer'}
                      </button>
                    </>}
                  </div>
                  <PairTable m={m} />
                </div>
              );
            }

            // Several possible matches: pick the right one(s), or none.
            const anyLinked = g.options.some(o => done[pairKey(o)] === 'linked');
            return (
              <div key={gk} style={{ marginBottom: 10 }} className={`${styles.prefGroup} ${anim.pairCard} ${left.length === 0 ? anim.done : ''}`}>
                <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap', marginBottom: 8 }}>
                  <strong>{money(Math.abs(g.options[0].from.amount))}</strong>
                  <span style={{ color: 'var(--c-b8860b)' }}>
                    {g.options.length} possible matches — link the one that's the actual transfer, if any
                  </span>
                  <span style={{ flex: 1 }} />
                  {left.length === 0
                    ? <StatusBadge status={anyLinked ? 'linked' : 'dismissed'} />
                    : (
                      <button className={styles.btnSecondary} disabled={busy !== null} onClick={() => void dismiss(gk, left)}>
                        {busy === `${gk}:dismiss` ? <Spin label="Saving…" /> : anyLinked ? 'None of the rest' : 'None of these'}
                      </button>
                    )}
                </div>
                {g.options.map((m, i) => {
                  const k = pairKey(m);
                  const state = statusOf(g, m);
                  return (
                    <div key={k} className={`${anim.option} ${state && left.length > 0 ? anim.done : ''}`}>
                      <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap', marginBottom: 4 }}>
                        <span style={{ fontWeight: 600 }}>Option {i + 1}</span>
                        <span>{m.from.accountName} → {m.to.accountName}</span>
                        <span className={styles.hint} style={{ margin: 0 }}>{gapText(m)}</span>
                        <span style={{ flex: 1 }} />
                        {state ? <StatusBadge status={state} /> : (
                          <button className={styles.btnPrimary} disabled={busy !== null} onClick={() => void link(m)}>
                            {busy === `${k}:link` ? <Spin label="Linking…" /> : 'Link this pair'}
                          </button>
                        )}
                      </div>
                      <PairTable m={m} />
                    </div>
                  );
                })}
              </div>
            );
          })}
        </>
      )}
    </div>
  );
}
