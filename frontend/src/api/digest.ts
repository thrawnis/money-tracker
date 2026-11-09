import api from './client';

export interface DigestSettings {
  enabled: boolean;
  /** Hour of day (0-23) in the user's time zone. */
  hour: number;
  includeAmounts: boolean;
  onlyWhenNews: boolean;
  email?: string | null;
  /** Whether the server has SMTP set up at all. */
  emailConfigured: boolean;
  lastSentOn?: string | null;
  /** Email on every sign-in. */
  loginAlerts: boolean;
}

export const getDigestSettings = (): Promise<DigestSettings> =>
  api.get('/digest/settings').then(r => r.data);

export const updateDigestSettings = (s: Pick<DigestSettings, 'enabled' | 'hour' | 'includeAmounts' | 'onlyWhenNews' | 'loginAlerts'>): Promise<DigestSettings> =>
  api.put('/digest/settings', s).then(r => r.data);

export const sendTestDigest = (): Promise<{ sentTo: string }> =>
  api.post('/digest/test').then(r => r.data);
