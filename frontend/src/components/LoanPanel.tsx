import { useEffect, useMemo, useState } from 'react';
import { getLoan, type LoanInfo } from '../api/loans';
import { payoffSchedule } from '../utils/loanMath';
import LoanDetailsModal from './LoanDetailsModal';
import type { Category } from '../types';
import styles from './LoanPanel.module.css';

const money = (n: number) => n.toLocaleString('en-US', { style: 'currency', currency: 'USD' });
const monthYear = (iso: string) => new Date(iso + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', year: 'numeric' });
const day = (iso: string) => new Date(iso + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
const span = (months: number) => {
  const y = Math.floor(months / 12), m = months % 12;
  return [y && `${y} yr`, m && `${m} mo`].filter(Boolean).join(' ') || '0 mo';
};

/**
 * Above a Loan account's register: what's owed, the payment, payoff date and
 * interest still to pay, an "extra each month" what-if, and the schedule.
 * Uses the bank's balance for loans synced as "balance only".
 */
export default function LoanPanel({ accountId, accountName, categories, onChanged }: {
  accountId: number; accountName: string; categories: Category[]; onChanged: () => void;
}) {
  const [info, setInfo] = useState<LoanInfo | null>(null);
  const [error, setError] = useState('');
  const [editing, setEditing] = useState(false);
  const [extra, setExtra] = useState('');
  const [showSchedule, setShowSchedule] = useState(false);
  const [allRows, setAllRows] = useState(false);

  useEffect(() => {
    let stale = false;
    getLoan(accountId).then(i => { if (!stale) setInfo(i); }).catch(() => { if (!stale) setError('Couldn\'t load the loan details.'); });
    return () => { stale = true; };
  }, [accountId]);

  const d = info?.details;
  const pay = info?.monthlyPayment ?? 0;
  const first = info?.nextPaymentDate ?? null;
  const base = useMemo(() => (info && d && pay > 0 ? payoffSchedule(info.owed, d.annualRate, pay, 0, first) : null), [info, d, pay, first]);
  const extraNum = Math.max(0, Number(extra.replace(/[$,]/g, '')) || 0);
  const what = useMemo(() => (info && d && pay > 0 && extraNum > 0 ? payoffSchedule(info.owed, d.annualRate, pay, extraNum, first) : null),
    [info, d, pay, extraNum, first]);

  if (error) return <div className={styles.panel}><p className={styles.muted}>{error}</p></div>;
  if (!info) return null;

  const modal = editing && (
    <LoanDetailsModal accountId={accountId} accountName={accountName} info={info} categories={categories}
      onClose={() => setEditing(false)}
      onSaved={i => { setEditing(false); if (i) setInfo(i); else getLoan(accountId).then(setInfo).catch(() => {}); onChanged(); }} />
  );

  if (!d) {
    return (
      <div className={styles.panel}>
        <div className={styles.empty}>
          <div>
            <strong>Track this loan's payoff</strong>
            <p className={styles.muted}>Add the interest rate and payment to see your payoff date and the interest still to pay — and to have each payment's interest recorded automatically.</p>
          </div>
          <button className={styles.btnPrimary} onClick={() => setEditing(true)}>Add loan details</button>
        </div>
        {modal}
      </div>
    );
  }

  const rows = base?.rows ?? [];
  const shown = allRows ? rows : rows.slice(0, 12);
  return (
    <div className={styles.panel} aria-label="Loan summary">
      <div className={styles.head}>
        <h3 className={styles.title}>Loan</h3>
        <span className={styles.muted}>
          {info.balanceSource === 'bank' ? `Balance reported by your bank on ${day(info.balanceDate)}` : 'Balance from this register'}
          {d.autoCharges && info.balanceSource === 'register' && ' · interest added automatically'}
        </span>
        <span style={{ flex: 1 }} />
        <button className={styles.btnLink} onClick={() => setEditing(true)}>Edit loan details</button>
      </div>

      <div className={styles.stats}>
        <div><span className={styles.label}>Owed</span><strong>{money(info.owed)}</strong></div>
        <div><span className={styles.label}>Rate</span><strong>{d.annualRate}%</strong></div>
        <div>
          <span className={styles.label}>Monthly payment</span>
          <strong>{pay > 0 ? money(pay + d.escrowAmount) : '—'}</strong>
          {d.escrowAmount > 0 && pay > 0 && <span className={styles.sub}>{money(pay)} P&amp;I + {money(d.escrowAmount)} escrow</span>}
        </div>
        {first && <div><span className={styles.label}>Next payment</span><strong>{day(first)}</strong></div>}
        <div>
          <span className={styles.label}>Paid off</span>
          <strong>{base?.covers ? (base.payoffDate ? monthYear(base.payoffDate) : span(base.months)) : '—'}</strong>
          {base?.covers && <span className={styles.sub}>{span(base.months)} to go</span>}
        </div>
        <div><span className={styles.label}>Interest still to pay</span><strong>{base?.covers ? money(base.totalInterest) : '—'}</strong></div>
        {info.balanceSource === 'register' && info.interestThisYear > 0 && (
          <div><span className={styles.label}>Interest this year</span><strong>{money(info.interestThisYear)}</strong></div>
        )}
      </div>

      {pay <= 0 && <p className={styles.warn}>Add the monthly payment (or the amount borrowed and term) to see the payoff date.</p>}
      {base && !base.covers && <p className={styles.warn}>The payment doesn't cover the monthly interest, so this loan won't be paid off at this rate.</p>}

      {base?.covers && (
        <div className={styles.whatIf}>
          <label>
            What if I paid an extra{' '}
            <input inputMode="decimal" value={extra} onChange={e => setExtra(e.target.value)} placeholder="$100" aria-label="Extra each month" />
            {' '}a month?
          </label>
          {what?.covers && (
            <span className={styles.whatIfResult}>
              Paid off {what.payoffDate ? monthYear(what.payoffDate) : `in ${span(what.months)}`} —{' '}
              <strong>{span(base.months - what.months)} sooner</strong>, saving <strong>{money(base.totalInterest - what.totalInterest)}</strong> in interest.
            </span>
          )}
        </div>
      )}

      {base?.covers && rows.length > 0 && (
        <>
          <button className={styles.btnLink} onClick={() => setShowSchedule(s => !s)}>
            {showSchedule ? 'Hide payment schedule' : 'Show payment schedule'}
          </button>
          {showSchedule && (
            <div className={styles.tableWrap}>
              <table className={styles.table}>
                <thead><tr><th>#</th>{first && <th>Date</th>}<th className={styles.r}>Payment</th><th className={styles.r}>Interest</th><th className={styles.r}>Principal</th><th className={styles.r}>Balance</th></tr></thead>
                <tbody>
                  {shown.map(r => (
                    <tr key={r.n}>
                      <td>{r.n}</td>{first && <td>{r.date ? day(r.date) : ''}</td>}
                      <td className={styles.r}>{money(r.payment)}</td><td className={styles.r}>{money(r.interest)}</td>
                      <td className={styles.r}>{money(r.principal)}</td><td className={styles.r}>{money(r.balance)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {rows.length > 12 && (
                <button className={styles.btnLink} onClick={() => setAllRows(a => !a)}>{allRows ? 'Show the next 12 only' : `Show all ${rows.length} payments`}</button>
              )}
              <p className={styles.muted}>Principal and interest only{d.escrowAmount > 0 ? '; escrow is on top' : ''}. Assumes the rate and payment stay the same.</p>
            </div>
          )}
        </>
      )}
      {modal}
    </div>
  );
}
