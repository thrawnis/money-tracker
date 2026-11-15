import api from './client';

export interface TransferSide {
  id: number;
  accountId: number;
  accountName: string;
  date: string;
  amount: number;
  payee?: string | null;
  memo?: string | null;
  category?: string | null;
}

/** from = money out, to = money in. */
export interface TransferMatch { from: TransferSide; to: TransferSide; dayGap: number }

/** Possible pairs that share transactions, closest dates first. One option = a plain suggestion. */
export interface TransferGroup { options: TransferMatch[] }

export const findTransferMatches = (days: number): Promise<{ days: number; groups: TransferGroup[] }> =>
  api.get('/transfer-matches', { params: { days } }).then(r => r.data);

export const linkTransferMatches = (pairs: { outId: number; inId: number }[]): Promise<{ linked: number; problems: string[] }> =>
  api.post('/transfer-matches/link', { pairs }).then(r => r.data);

/** "Not a transfer": stop suggesting this pair. */
export const dismissTransferMatch = (outId: number, inId: number) =>
  api.post('/transfer-matches/dismiss', { outId, inId });

export const restoreDismissedTransferMatches = (): Promise<{ restored: number }> =>
  api.post('/transfer-matches/undismiss-all').then(r => r.data);
