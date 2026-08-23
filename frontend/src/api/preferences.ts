import api from './client';
import type { TokenResponse } from '../types/auth';

export interface Preferences {
  defaultRegisterSortBy?: string | null;
  defaultRegisterSortDir?: 'asc' | 'desc' | null;
  defaultFutureDays?: number | null;
  // Opt-in (default false): pre-create recurring transactions as real,
  // editable transactions once within autoCreateFutureDays of their due
  // date, instead of only materializing them when actually due.
  autoCreateFutureTransactions?: boolean;
  autoCreateFutureDays?: number | null;
  // IANA timezone id ("America/Chicago"). null/undefined means UTC. Decides
  // what counts as "today" for balances, report period shortcuts, and when
  // scheduled bills post.
  timeZoneId?: string | null;
}

export interface TimeZoneOption {
  id: string;
  displayName: string;
}

export const getTimeZones = (): Promise<TimeZoneOption[]> =>
  api.get('/preferences/timezones').then(r => r.data);

// Sets only the timezone and returns a fresh access token carrying the updated
// "tz" claim — the one write allowed while the mandatory-timezone gate is up.
export const setTimeZone = (timeZoneId: string): Promise<TokenResponse> =>
  api.put('/preferences/timezone', { timeZoneId }).then(r => r.data);

export const getPreferences = (): Promise<Preferences> =>
  api.get('/preferences').then(r => r.data);

export const updatePreferences = (prefs: Preferences): Promise<Preferences> =>
  api.put('/preferences', prefs).then(r => r.data);
