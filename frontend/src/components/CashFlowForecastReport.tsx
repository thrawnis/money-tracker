import { useEffect, useState } from 'react';
import { getForecast, setLowBalanceThreshold, type Forecast, type AccountForecast } from '../api/forecast';
import LineChart from './LineChart';
import styles from '../pages/Reports.module.css';

const money = (n: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
const day = (d: string) => new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric' });

/** Daily balances from today through the horizon, for the chart. */
function dailySeries(f: AccountForecast, asOf: string, days: number) {
  const dates: string[] = [];
  const values: number[] = [];
  let balance = f.currentBalance;
  let i = 0;
  const start = new Date(asOf + 'T00:00:00');
  for (let n = 0; n <= days; n++) {
    const d = new Date(start); d.setDate(start.getDate() + n);
    const iso = d.toLocaleDateString('en-CA');
    while (i < f.events.length && f.events[i].date <= iso) balance = f.events[i++].balance;
    dates.push(iso);
    values.push(balance);
  }
  return { dates, values };
}

/**
 * Projected balances from future-dated transactions and scheduled bills, with a
 * per-account low-balance warning level. Click an account for its chart and the
 * upcoming items behind it.
 */
export default function CashFlowForecastReport() {
  const [days, setDays] = useState(30);
  const [data, setData] = useState<Forecast | null>(null);
  const [error, setError] = useState('');
  const [selected, setSelected] = useState<number | null>(null);
  const [editing, setEditing] = useState<{ id: number; value: string } | null>(null);
  const [reload, setReload] = useState(0);

  useEffect(() => {
    let cancelled = false;
    getForecast(days)
      .then(d => {
        if (cancelled) return;
        setData(d);
        setError('');
        setSelected(prev => prev ?? d.accounts.find(a => a.firstBelowThreshold)?.accountId ?? d.accounts[0]?.accountId ?? null);
      })
      .catch(() => { if (!cancelled) setError('Failed to load the forecast.'); });
    return () => { cancelled = true; };
  }, [days, reload]);

  const saveThreshold = async () => {
    if (!editing) return;
    const v = editing.value.trim();
    const n = v === '' ? null : Number(v);
    if (n !== null && !Number.isFinite(n)) { setError('Enter a valid amount.'); return; }
    try {
      await setLowBalanceThreshold(editing.id, n);
      setEditing(null);
      setReload(r => r + 1);
    } catch {
      setError('Failed to save the warning level.');
    }
  };

  const sel = data?.accounts.find(a => a.accountId === selected) ?? null;
  const series = sel && data ? dailySeries(sel, data.asOf, data.days) : null;

  return (
    <>
      <div className={styles.controls}>
        <div className={styles.controlRow}>
          <span className={styles.controlLabel}>Look ahead</span>
          <select value={days} onChange={e => setDays(Number(e.target.value))} aria-label="Days to forecast">
            {[30, 60, 90].map(d => <option key={d} value={d}>{d} days</option>)}
          </select>
        </div>
        <p style={{ margin: '6px 0 0', fontSize: 13, opacity: 0.8 }}>
          Starts from today's balance and adds transactions already dated in the future plus upcoming occurrences of your
          {' '}bills and scheduled transfers. Everyday spending that isn't scheduled isn't included, so real balances will
          {' '}usually run a little lower. Bank-reported (balance-only) accounts, investments and loans aren't forecast.
        </p>
      </div>

      {error && <p className={styles.errorMsg}>{error}</p>}
      {data && data.accounts.length === 0 && <p style={{ fontSize: 14 }}>No accounts to forecast.</p>}

      {data && data.accounts.length > 0 && (
        <div className={styles.tableWrapper}>
          <table className={styles.reportTable}>
            <thead>
              <tr>
                <th>Account</th>
                <th className={styles.right}>Today</th>
                <th className={styles.right}>Lowest</th>
                <th>On</th>
                <th className={styles.right}>In {data.days} days</th>
                <th className={styles.right}>Warn below</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {data.accounts.map(a => (
                <tr key={a.accountId} onClick={() => setSelected(a.accountId)}
                  style={{ cursor: 'pointer', fontWeight: selected === a.accountId ? 600 : undefined }}>
                  <td>{a.name}</td>
                  <td className={styles.right}>{money(a.currentBalance)}</td>
                  <td className={`${styles.right} ${a.threshold !== null && a.lowestBalance < a.threshold ? styles.negative : ''}`}>{money(a.lowestBalance)}</td>
                  <td>{day(a.lowestDate)}</td>
                  <td className={styles.right}>{money(a.endBalance)}</td>
                  <td className={styles.right} onClick={e => e.stopPropagation()}>
                    {editing?.id === a.accountId ? (
                      <form onSubmit={e => { e.preventDefault(); saveThreshold(); }} style={{ display: 'inline-flex', gap: 4 }}>
                        <input type="number" step="0.01" value={editing.value} autoFocus aria-label={`Warn below for ${a.name}`}
                          onChange={e => setEditing({ id: a.accountId, value: e.target.value })} style={{ width: 90 }} />
                        <button type="submit" className={styles.btnSecondary}>Save</button>
                        <button type="button" className={styles.btnSecondary} onClick={() => setEditing(null)}>Cancel</button>
                      </form>
                    ) : (
                      <button type="button" className={styles.btnSecondary} title="Change the warning level (leave empty for the default)"
                        onClick={() => setEditing({ id: a.accountId, value: a.threshold === null ? '' : String(a.threshold) })}>
                        {a.threshold === null ? 'None' : money(a.threshold)}
                      </button>
                    )}
                  </td>
                  <td>
                    {a.firstBelowThreshold && a.threshold !== null
                      ? <span className={styles.negative}>⚠ Below {money(a.threshold)} from {day(a.firstBelowThreshold)}</span>
                      : a.threshold === null ? '—' : 'OK'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {sel && series && (
        <div style={{ marginTop: 16 }}>
          <h3 style={{ margin: '0 0 6px', fontSize: 15 }}>{sel.name} — projected balance</h3>
          <LineChart
            label={`${sel.name} projected balance over ${data!.days} days`}
            dates={series.dates}
            series={[
              { key: 'bal', label: 'Balance', slot: 1, values: series.values },
              ...(sel.threshold !== null
                ? [{ key: 'warn', label: 'Warn level', slot: 2 as const, values: series.dates.map(() => sel.threshold) }]
                : []),
            ]}
            height={220}
          />
          {sel.events.length === 0 ? (
            <p style={{ fontSize: 14 }}>Nothing scheduled for this account in the next {data!.days} days.</p>
          ) : (
            <div className={styles.tableWrapper}>
              <table className={styles.reportTable}>
                <thead>
                  <tr><th>Date</th><th>What</th><th className={styles.right}>Amount</th><th className={styles.right}>Balance after</th></tr>
                </thead>
                <tbody>
                  {sel.events.map((e, i) => (
                    <tr key={i}>
                      <td>{day(e.date)}</td>
                      <td>{e.description}{e.scheduled && <span style={{ opacity: 0.6 }}> · scheduled</span>}</td>
                      <td className={`${styles.right} ${e.amount < 0 ? styles.negative : ''}`}>{money(e.amount)}</td>
                      <td className={`${styles.right} ${sel.threshold !== null && e.balance < sel.threshold ? styles.negative : ''}`}>{money(e.balance)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </>
  );
}
