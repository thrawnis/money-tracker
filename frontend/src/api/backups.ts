import api from './client';

export interface BackupSectionInfo { id: string; label: string; included: boolean }
export interface BackupInfo {
  fileName: string;
  createdAt: string;
  /** daily | manual | before-restore */
  kind: string;
  sections: string[];
  counts: Record<string, number>;
  sizeBytes: number;
}
export interface BackupsOverview {
  sections: BackupSectionInfo[];
  backups: BackupInfo[];
  keep: number;
  dailyAt: string;
  lastDailyBackupOn: string | null;
  lastError: string | null;
  available: boolean;
}
export interface BackupSummary {
  fileName: string;
  createdAt: string;
  kind: string;
  sections: string[];
  counts: Record<string, number>;
  accounts: { id: number; name: string; type: string; isActive: boolean; transactions: number; balance: number; current: string | null }[];
  /** Current accounts a whole restore would remove. */
  notInBackup: string[];
}
export interface RestoreAccountChoice { backupAccountId: number; mode: 'replace' | 'copy' }
export interface RestoreResult { lines: string[]; warnings: string[]; safetyBackup: string }

export const getBackups = (): Promise<BackupsOverview> => api.get('/backups').then(r => r.data);
export const saveBackupSettings = (includedSections: string[]): Promise<BackupsOverview> =>
  api.put('/backups/settings', { includedSections }).then(r => r.data);
export const backUpNow = (): Promise<{ fileName: string }> => api.post('/backups', null, { timeout: 0 }).then(r => r.data);
export const getBackupSummary = (fileName: string): Promise<BackupSummary> =>
  api.get(`/backups/${encodeURIComponent(fileName)}`).then(r => r.data);
export const restoreBackup = (fileName: string, body: { sections: string[]; accounts: RestoreAccountChoice[]; removeOtherAccounts: boolean }, exportToken: string): Promise<RestoreResult> =>
  api.post(`/backups/${encodeURIComponent(fileName)}/restore`, body, { headers: { 'X-Export-Token': exportToken }, timeout: 0 }).then(r => r.data);
export const downloadBackup = (fileName: string, password: string | null, exportToken: string): Promise<Blob> =>
  api.post(`/backups/${encodeURIComponent(fileName)}/download`, { password }, { headers: { 'X-Export-Token': exportToken }, responseType: 'blob', timeout: 0 })
    .then(r => r.data);
