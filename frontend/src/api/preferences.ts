import api from './client';

export interface Preferences {
  defaultRegisterSortBy?: string | null;
  defaultRegisterSortDir?: 'asc' | 'desc' | null;
  defaultFutureDays?: number | null;
}

export const getPreferences = (): Promise<Preferences> =>
  api.get('/preferences').then(r => r.data);

export const updatePreferences = (prefs: Preferences): Promise<Preferences> =>
  api.put('/preferences', prefs).then(r => r.data);
