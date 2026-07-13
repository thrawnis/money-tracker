import api from './client';

export interface AccountBackupSummary {
  fileName: string;
  accountId: number;
  accountName: string;
  backedUpAt: string;
  note?: string;
  sizeBytes: number;
}

export const listAccountBackups = () =>
  api.get<AccountBackupSummary[]>('/account-backups').then(r => r.data);

export const downloadAccountBackup = (fileName: string, exportToken: string) =>
  api.get(`/account-backups/${encodeURIComponent(fileName)}/download`, {
    params: { exportToken },
    responseType: 'blob',
  }).then(r => r.data as Blob);
