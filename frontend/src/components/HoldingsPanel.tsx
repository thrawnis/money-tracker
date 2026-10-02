import { useEffect, useState } from 'react';
import { getHoldings, type Holdings, type Holding } from '../api/holdings';
import LineChart from './LineChart';
import styles from '../pages/Settings.module.css';

const money = (n?: number | null) =>
  n == null ? '—' : new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);

const fmtDay = (d: string) =>
  new Date(d + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });

// Gain/loss text: the sign is spelled out, so it never relies on color alone.
function Gain({ value, pct }: { value?: number | null; pct?: number | null }) {
  if (value == null) return <span title="Your brokerage didn't report a cost basis">—</span>;
  const up = value >= 0;
  return (
    <span style={{ color: up ? 'var(--c-006600)' : 'var(--c-cc0000)', whiteSpace: 'nowrap' }}>
      {up ? '▲ +' : '▼ −'}{money(Math.abs(value))}
      {pct != null && <> ({up ? '+' : '−'}{Math.abs(pct).toFixed(1)}%)</>}
    </span>
  );
}

const label = (h: Holding) => h.symbol || h.description || 'Position';

/**
 * Positions in an investment account, as last reported by the brokerage
 * through bank sync, with gain/loss against what was paid (cost basis).
 * Pick a position to chart its value over the days recorded so far.
 */
export default function HoldingsPanel({ accountId }: { accountId: number }) {
  const [data, setData] = useState<Holdings | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [showSold, setShowSold] = useState(false);

  useEffect(() => {
    let cancelled = false;
    getHoldings(accountId).then(d => { if (!cancelled) setData(d); }).catch(() => {});
    return () => { cancelled = true; };
  }, [accountId]);

  if (!data || !data.asOf || data.holdings.length === 0 || !data.totals) return null;

  const current = data.holdings.filter(h => h.current);
  const sold = data.holdings.filter(h => !h.current);
  const chosen = data.holdings.find(h => h.id === selected) ?? null;
  const rows = showSold ? [...current, ...sold] : current;

  return (
    <div className={styles.prefGroup} style={{ margin: '0 0 16px' }}>
      <p className={styles.prefGroupTitle}>Holdings as of {fmtDay(data.asOf)}</p>
      <p style={{ margin: '0 0 10px', fontSize: 13 }}>
        Value <strong>{money(data.totals.marketValue)}</strong>
        {data.totals.gain != null && (
          <> · {data.totals.gain >= 0 ? 'up' : 'down'} vs. what you paid: <Gain value={data.totals.gain} pct={data.totals.gainPct} /></>
        )}
        {data.totals.missingCostBasis > 0 && (
          <span className={styles.hint}> ({data.totals.missingCostBasis} position{data.totals.missingCostBasis === 1 ? '' : 's'} without a cost basis left out)</span>
        )}
      </p>

      <div style={{ overflowX: 'auto' }}>
        <table className={styles.dupTable}>
          <thead>
            <tr>
              <th>Holding</th>
              <th style={{ textAlign: 'right' }}>Shares</th>
              <th style={{ textAlign: 'right' }}>Value</th>
              <th style={{ textAlign: 'right' }}>Cost basis</th>
              <th style={{ textAlign: 'right' }}>Gain / loss</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(h => (
              <tr
                key={h.id}
                onClick={() => setSelected(s => (s === h.id ? null : h.id))}
                style={{ cursor: 'pointer', opacity: h.current ? 1 : 0.6, fontWeight: h.id === selected ? 700 : undefined }}
                title="Show this holding's value over time"
              >
                <td>
                  <strong>{h.symbol ?? ''}</strong>
                  {h.description && <span className={styles.hint} style={{ marginLeft: h.symbol ? 6 : 0 }}>{h.description}</span>}
                  {!h.current && <span className={styles.hint}> (sold)</span>}
                </td>
                <td style={{ textAlign: 'right' }}>{h.shares == null ? '—' : h.shares.toLocaleString('en-US', { maximumFractionDigits: 4 })}</td>
                <td style={{ textAlign: 'right' }}>{money(h.marketValue)}</td>
                <td style={{ textAlign: 'right' }}>{money(h.costBasis)}</td>
                <td style={{ textAlign: 'right' }}>{h.current ? <Gain value={h.gain} pct={h.gainPct} /> : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div style={{ display: 'flex', gap: 12, alignItems: 'center', marginTop: 6 }}>
        <span className={styles.hint}>Click a holding to chart its value.</span>
        {sold.length > 0 && (
          <button className={styles.btnLink} onClick={() => setShowSold(s => !s)}>
            {showSold ? 'Hide' : 'Show'} {sold.length} sold
          </button>
        )}
      </div>

      {chosen && data.dates && (
        <div style={{ marginTop: 12 }}>
          <LineChart
            dates={data.dates}
            series={[{ key: 'h', label: label(chosen), slot: 1, values: chosen.values }]}
            height={220}
            label={`${label(chosen)} value over time`}
          />
          {data.dates.length < 2 && (
            <p className={styles.hint}>History builds up one day at a time from the daily balance update.</p>
          )}
        </div>
      )}
    </div>
  );
}
