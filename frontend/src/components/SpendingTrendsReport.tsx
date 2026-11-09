import { useEffect, useState } from 'react';
import { getSpendingTrends, type SpendingTrends } from '../api/reports';
import { paceText } from '../utils/spendingTrends';
import LineChart from './LineChart';
import styles from '../pages/Reports.module.css';

const money = (n: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);
const monthLabel = (ym: string) =>
  new Date(ym + '-01T00:00:00').toLocaleDateString('en-US', { month: 'short', year: '2-digit' });

/**
 * Spending per category this month so far vs the average of previous months,
 * paced to today's point in the month. Click a category for its monthly chart.
 */
export default function SpendingTrendsReport() {
  const [months, setMonths] = useState(6);
  const [data, setData] = useState<SpendingTrends | null>(null);
  const [error, setError] = useState('');
  const [selected, setSelected] = useState<number | null | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;
    getSpendingTrends(months)
      .then(d => { if (!cancelled) { setData(d); setError(''); } })
      .catch(() => { if (!cancelled) setError('Failed to load spending trends.'); });
    return () => { cancelled = true; };
  }, [months]);

  const sel = data?.categories.find(c => c.categoryId === selected) ?? null;
  const dayPct = data ? Math.round(data.monthFraction * 100) : 0;

  return (
    <>
      <div className={styles.controls}>
        <div className={styles.controlRow}>
          <span className={styles.controlLabel}>Compare with the average of the last</span>
          <select value={months} onChange={e => setMonths(Number(e.target.value))} aria-label="Months to average">
            {[3, 6, 12].map(m => <option key={m} value={m}>{m} months</option>)}
          </select>
        </div>
        {data && (
          <p style={{ margin: '6px 0 0', fontSize: 13, opacity: 0.8 }}>
            This month so far ({dayPct}% of the way through): <strong>{money(data.totalThisMonth)}</strong> spent
            {' '}vs <strong>{money(data.totalExpectedSoFar)}</strong> by this day in a usual month
            {' '}({money(data.totalAverage)} for a whole month). "Usual by now" is what you spent by this day of the month
            {' '}in previous months, on average, so bills paid early in the month count as expected.
            {' '}Transfers aren't counted; subcategories roll up into their category.
          </p>
        )}
      </div>

      {error && <p className={styles.errorMsg}>{error}</p>}

      {sel && data && (
        <div style={{ margin: '12px 0' }}>
          <h3 style={{ margin: '0 0 6px', fontSize: 15 }}>{sel.name} — spent per month</h3>
          <LineChart
            label={`${sel.name} spending per month`}
            dates={data.months.map(m => m + '-01')}
            series={[
              { key: 'spent', label: 'Spent', slot: 1, values: sel.monthly },
              { key: 'avg', label: 'Average', slot: 2, values: data.months.map(() => sel.average) },
            ]}
            height={200}
          />
        </div>
      )}

      {data && data.categories.length === 0 && <p style={{ fontSize: 14 }}>No spending in this period.</p>}
      {data && data.categories.length > 0 && (
        <div className={styles.tableWrapper}>
          <table className={styles.reportTable}>
            <thead>
              <tr>
                <th>Category</th>
                <th className={styles.right}>Average / month</th>
                <th className={styles.right}>Usual by now</th>
                <th className={styles.right}>This month</th>
                <th className={styles.right}>vs usual pace</th>
                {data.months.slice(0, -1).map(m => <th key={m} className={styles.right}>{monthLabel(m)}</th>)}
              </tr>
            </thead>
            <tbody>
              {data.categories.map(c => (
                <tr
                  key={c.categoryId ?? 'none'}
                  onClick={() => setSelected(selected === c.categoryId ? undefined : c.categoryId)}
                  style={{ cursor: 'pointer', fontWeight: selected === c.categoryId ? 600 : undefined }}
                  title="Show this category's monthly chart"
                >
                  <td>{c.name}</td>
                  <td className={styles.right}>{money(c.average)}</td>
                  <td className={styles.right}>{money(c.expectedSoFar)}</td>
                  <td className={styles.right}>{money(c.thisMonth)}</td>
                  <td className={`${styles.right} ${c.aheadOfPace > 0 ? styles.negative : ''}`}>{paceText(c)}</td>
                  {c.monthly.slice(0, -1).map((v, i) => <td key={i} className={styles.right}>{money(v)}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
