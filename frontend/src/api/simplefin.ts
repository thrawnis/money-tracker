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
  /** Bank transactions unticked on an earlier review, newest first. Later syncs leave these out. */
  skipped: SimpleFinSkippedTransaction[];
}

export interface SimpleFinSkippedTransaction {
  id: number;
  date: string;
  amount: number;
  payee?: string | null;
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

/** accountIds: SimpleFIN accounts to sync this time; omit to sync every linked account. */
export const syncSimpleFin = (accountIds?: number[]): Promise<SimpleFinSyncResult> =>
  api.post('/simplefin/sync', { accountIds: accountIds ?? null }).then(r => r.data);

/** Forget a skip, so the next sync of that account offers the transaction again. */
export const restoreSkippedTransaction = (skippedId: number): Promise<SimpleFinStatus> =>
  api.delete(`/simplefin/skipped/${skippedId}`).then(r => r.data);
