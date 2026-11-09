import api from './client';

export interface ForecastEvent {
  date: string;
  description: string;
  amount: number;
  /** Balance after this event. */
  balance: number;
  /** From a scheduled bill/transfer not yet created as a transaction. */
  scheduled: boolean;
}

export interface AccountForecast {
  accountId: number;
  name: string;
  type: string;
  currentBalance: number;
  /** Warn below this; null = no warning (default for credit cards and other accounts). */
  threshold: number | null;
  lowestBalance: number;
  lowestDate: string;
  /** First date the projected balance is below the threshold, if any. */
  firstBelowThreshold?: string | null;
  endBalance: number;
  events: ForecastEvent[];
}

export interface Forecast {
  asOf: string;
  days: number;
  accounts: AccountForecast[];
}

export const getForecast = (days = 30, watchedOnly = false): Promise<Forecast> =>
  api.get('/forecast', { params: { days, watchedOnly } }).then(r => r.data);

/** null resets to the default ($0 for checking/savings/cash, no warning otherwise). */
export const setLowBalanceThreshold = (accountId: number, threshold: number | null) =>
  api.put(`/forecast/accounts/${accountId}/threshold`, { threshold });
