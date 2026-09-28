import api from './client';

export interface SimpleFinAccount {
  id: number;
  name: string;
  orgName?: string | null;
  balance?: number | null;
  currency?: string | null;
  linkedAccountId?: number | null;
  linkedAccountName?: string | null;
  /** Newest transaction date already imported from this bank account (YYYY-MM-DD). */
  syncedThrough?: string | null;
  /** An unreviewed sync draft for this account, if one is waiting. */
  pendingDraftId?: number | null;
  pendingDraftRows?: number | null;
}

export type SimpleFinStatus =
  | { connected: false }
  | {
      connected: true;
      createdAt: string;
      lastSyncAt?: string | null;
      lastErrors: string[];
      accounts: SimpleFinAccount[];
    };

export interface SimpleFinSyncAccountResult {
  simpleFinAccountId: number;
  name: string;
  linkedAccountId?: number | null;
  linkedAccountName?: string | null;
  from?: string | null;
  newTransactions: number;
  draftId?: number | null;
  note?: string | null;
  /** Nothing was checked for this account (see note), as opposed to "checked, nothing new". */
  skipped?: boolean;
}

export interface SimpleFinSyncResult {
  accounts: SimpleFinSyncAccountResult[];
  errors: string[];
}

export const getSimpleFinStatus = (): Promise<SimpleFinStatus> =>
  api.get('/simplefin').then(r => r.data);

export const connectSimpleFin = (setupToken: string): Promise<SimpleFinStatus> =>
  api.post('/simplefin/connect', { setupToken }).then(r => r.data);

export const disconnectSimpleFin = () =>
  api.delete('/simplefin');

export const linkSimpleFinAccount = (simpleFinAccountId: number, accountId: number | null): Promise<SimpleFinStatus> =>
  api.put(`/simplefin/accounts/${simpleFinAccountId}/link`, { accountId }).then(r => r.data);

export const syncSimpleFin = (): Promise<SimpleFinSyncResult> =>
  api.post('/simplefin/sync').then(r => r.data);
