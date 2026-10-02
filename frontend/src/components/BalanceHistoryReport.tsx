import { useEffect, useState } from 'react';
import { getBalanceHistory, type BalanceHistory, type BalanceRange } from '../api/reports';
import LineChart, { type LineSeries } from './LineChart';
import styles from '../pages/Reports.module.css';

const RANGES: [BalanceRange, string][] = [['3m', '3 months'], ['1y', '1 year'], ['5y', '5 years'], ['all', 'All time']];

const money = (n: number) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);

const fmtDay = (d: string) =>
  new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });

interface Props {
  /** Show one account; omitted shows net worth across all accounts. */
  accountId?: number;
  /** Compact mode for the Dashboard: no controls beyond the range, no table. */
  compact?: boolean;
  defaultRange?: BalanceRange;
}

/**
 * Balances over time: net worth (optionally split into what you own and what
 * you owe) or a single account, with the change over the chosen period.
 */
export default function BalanceHistoryReport({ accountId, compact = false, defaultRange = '1y' }: Props) {
  const [range, setRange] = useState<BalanceRange>(defaultRange);
  const [selected, setSelected] = useState<number | ''>(accountId ?? '');
  const [breakdown, setBreakdown] = useState(false);
  const [showTable, setShowTable] = useState(false);
  const [data, setData] = useState<BalanceHistory | null>(null);
  // Every account, for the picker — loaded once from the all-accounts view.
  const [accounts, setAccounts] = useState<{ id: number; name: string }[]>([]);
  // Which request the current data/error belongs to; loading is derived from it.
  const requestKey = `${range}|${selected}`;
  const [settled, setSettled] = useState<{ key: string; error: string } | null>(null);
  const loading = settled?.key !== requestKey;
  const error = loading ? '' : settled?.error ?? '';

  useEffect(() => {
    let cancelled = false;
    const key = `${range}|${selected}`;
    getBalanceHistory(range, selected === '' ? undefined : selected)
      .then(d => {
        if (cancelled) return;
        setData(d);
        if (selected === '') setAccounts(d.accounts.map(a => ({ id: a.id, name: a.name })));
        setSettled({ key, error: '' });
      })
      .catch(() => { if (!cancelled) setSettled({ key, error: 'Failed to load balance history.' }); });
    return () => { cancelled = true; };
  }, [range, selected]);

  const single = selected === '' ? null : data?.accounts.find(a => a.id === selected) ?? null;

  let series: LineSeries[] = [];
  if (data) {
    if (single) {
      series = [{ key: 'acct', label: single.name, slot: 1, values: single.values }];
    } else if (breakdown) {
      series = [
        { key: 'net', label: 'Net worth', slot: 1, values: data.netWorth },
        { key: 'debts', label: 'Owe', slot: 2, values: data.debts },
        { key: 'assets', label: 'Own', slot: 3, values: data.assets },
      ];
    } else {
      series = [{ key: 'net', label: 'Net worth', slot: 1, values: data.netWorth }];
    }
  }

  // Change over the period for the headline series: first to last value.
  const headline = series[0];
  const firstIdx = headline ? headline.values.findIndex(v => v != null) : -1;
  const start = firstIdx >= 0 ? headline.values[firstIdx] as number : null;
  const end = headline?.values[headline.values.length - 1] ?? null;
  const change = start != null && end != null ? end - start : null;
  const pct = change != null && start ? (change / Math.abs(start)) * 100 : null;

  // Bank-reported accounts only have history from when recording began.
  const lateStarters = (data?.accounts ?? []).filter(a =>
    a.reported && a.since && data && a.since > data.dates[0] && (selected === '' || a.id === selected));

  return (
    <div>
      <div className={styles.controls}>
        <div className={styles.controlRow}>
          {!compact && (
            <>
              <label className={styles.controlLabel} htmlFor="bh-account">Show:</label>
              <select id="bh-account" value={selected} onChange={e => setSelected(e.target.value === '' ? '' : Number(e.target.value))}>
                <option value="">Net worth (all accounts)</option>
                {accounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
              </select>
            </>
          )}
          {RANGES.map(([k, l]) => (
            <button
              key={k}
              className={styles.presetBtn}
              aria-pressed={range === k}
              style={range === k ? { fontWeight: 700, textDecoration: 'underline' } : undefined}
              onClick={() => setRange(k)}
            >
              {l}
            </button>
          ))}
          {!compact && selected === '' && (
            <label className={styles.controlLabel}>
              <input type="checkbox" checked={breakdown} onChange={e => setBreakdown(e.target.checked)} /> Show what you own and owe
            </label>
          )}
          {!compact && (
            <label className={styles.controlLabel}>
              <input type="checkbox" checked={showTable} onChange={e => setShowTable(e.target.checked)} /> Table
            </label>
          )}
        </div>
      </div>

      {error && <div className={styles.errorMsg}>{error}</div>}
      {loading && !data && <p>Loading…</p>}

      {data && headline && end != null && (
        <p style={{ margin: '4px 0 10px', fontSize: 13 }}>
          <strong style={{ fontSize: 18 }}>{money(end)}</strong>
          {change != null && firstIdx < data.dates.length - 1 && (
            <> &nbsp;{change >= 0 ? 'up' : 'down'} <strong>{money(Math.abs(change))}</strong>
              {pct != null && <> ({Math.abs(pct).toFixed(1)}%)</>} since {fmtDay(data.dates[firstIdx])}</>
          )}
        </p>
      )}

      {data && (
        <LineChart
          dates={data.dates}
          series={series}
          height={compact ? 200 : 300}
          label={`${headline?.label ?? 'Balance'} over time`}
        />
      )}

      {lateStarters.length > 0 && (
        <p style={{ fontSize: 12, opacity: 0.8, margin: '8px 0 0' }}>
          {lateStarters.map(a => a.name).join(', ')}: value history starts {fmtDay(lateStarters[0].since!)}
          {lateStarters.length > 1 && ' or later'}, when daily recording from your bank began
          {selected === '' && ' — net worth before that leaves out these accounts'}.
        </p>
      )}

      {showTable && data && (
        <div className={styles.tableWrapper} style={{ maxHeight: 360, overflowY: 'auto', marginTop: 12 }}>
          <table className={styles.reportTable}>
            <thead>
              <tr>
                <th>Date</th>
                {series.map(s => <th key={s.key} style={{ textAlign: 'right' }}>{s.label}</th>)}
              </tr>
            </thead>
            <tbody>
              {data.dates.map((d, i) => ({ d, i })).reverse().map(({ d, i }) => (
                <tr key={d}>
                  <td>{fmtDay(d)}</td>
                  {series.map(s => (
                    <td key={s.key} style={{ textAlign: 'right' }}>{s.values[i] == null ? '—' : money(s.values[i] as number)}</td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
