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
  /** After the nightly bank sync, what arrived per account. */
  syncAlerts: boolean;
  /** Any sync bringing in a transaction of at least largeTxThreshold (in or out). */
  largeTxAlerts: boolean;
  largeTxThreshold: number;
  weeklyEnabled: boolean;
  /** 0 = Sunday … 6 = Saturday. */
  weeklyDay: number;
  weeklyHour: number;
}

export type DigestSettingsUpdate = Pick<DigestSettings,
  'enabled' | 'hour' | 'includeAmounts' | 'onlyWhenNews' | 'loginAlerts'
  | 'syncAlerts' | 'largeTxAlerts' | 'largeTxThreshold' | 'weeklyEnabled' | 'weeklyDay' | 'weeklyHour'>;

export const getDigestSettings = (): Promise<DigestSettings> =>
  api.get('/digest/settings').then(r => r.data);

export const updateDigestSettings = (s: DigestSettingsUpdate): Promise<DigestSettings> =>
  api.put('/digest/settings', s).then(r => r.data);

export const sendTestDigest = (kind: 'daily' | 'weekly' = 'daily'): Promise<{ sentTo: string }> =>
  api.post('/digest/test', null, { params: { kind } }).then(r => r.data);
