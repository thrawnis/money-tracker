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

/** from = money out, to = money in. alternatives: other transactions either side could have paired with. */
export interface TransferMatch { from: TransferSide; to: TransferSide; dayGap: number; alternatives: number }

export const findTransferMatches = (days: number, includeCategorized: boolean): Promise<{ days: number; matches: TransferMatch[] }> =>
  api.get('/transfer-matches', { params: { days, includeCategorized } }).then(r => r.data);

export const linkTransferMatches = (pairs: { outId: number; inId: number }[]): Promise<{ linked: number; problems: string[] }> =>
  api.post('/transfer-matches/link', { pairs }).then(r => r.data);

/** "Not a transfer": stop suggesting this pair. */
export const dismissTransferMatch = (outId: number, inId: number) =>
  api.post('/transfer-matches/dismiss', { outId, inId });

export const restoreDismissedTransferMatches = (): Promise<{ restored: number }> =>
  api.post('/transfer-matches/undismiss-all').then(r => r.data);
