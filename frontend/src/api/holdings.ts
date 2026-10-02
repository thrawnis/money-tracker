import api from './client';

export interface Holding {
  id: number;
  symbol?: string | null;
  description?: string | null;
  /** Held on the latest recorded day (false: sold since; history only). */
  current: boolean;
  shares?: number | null;
  marketValue?: number | null;
  costBasis?: number | null;
  /** Market value minus cost basis; null when the brokerage didn't report a cost basis. */
  gain?: number | null;
  gainPct?: number | null;
  /** Market value on each of `dates`; null where not held/recorded. */
  values: (number | null)[];
}

export interface Holdings {
  asOf: string | null;
  dates?: string[];
  totals?: {
    marketValue: number;
    costBasis?: number | null;
    gain?: number | null;
    gainPct?: number | null;
    missingCostBasis: number;
  };
  holdings: Holding[];
}

export const getHoldings = (accountId: number): Promise<Holdings> =>
  api.get(`/accounts/${accountId}/holdings`).then(r => r.data);
