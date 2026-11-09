import type { SpendingTrendCategory } from '../api/reports';

const money = (n: number) => new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(n);

/** "+$80 (25%)" / "−$40 (10%)" relative to the usual pace for this point in the month. */
export function paceText(c: SpendingTrendCategory, withAmount = true): string {
  const sign = c.aheadOfPace > 0 ? '+' : c.aheadOfPace < 0 ? '−' : '';
  const pct = c.expectedSoFar > 0 ? ` (${Math.round(Math.abs(c.aheadOfPace) / c.expectedSoFar * 100)}%)` : '';
  if (!withAmount) return c.expectedSoFar > 0 ? `${sign}${Math.round(Math.abs(c.aheadOfPace) / c.expectedSoFar * 100)}%` : 'new';
  return `${sign}${money(Math.abs(c.aheadOfPace))}${pct}`;
}

