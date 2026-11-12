import api from './client';

export interface UncategorizedTx {
  id: number;
  accountId: number;
  accountName: string;
  date: string;
  amount: number;
  memo?: string | null;
}

export interface UncategorizedGroup {
  /** null = transactions with no payee. */
  payeeId: number | null;
  payeeName: string | null;
  hasDefaultCategory: boolean;
  count: number;
  total: number;
  /** Newest first. */
  transactions: UncategorizedTx[];
}

export const getUncategorizedByPayee = (): Promise<{ total: number; groups: UncategorizedGroup[] }> =>
  api.get('/uncategorized').then(r => r.data);

/**
 * Sets a category on the given transactions and/or every uncategorized
 * transaction of the given payees (as of now, on the server). setPayeeDefault
 * also makes it those payees' default category.
 */
export const assignUncategorized = (data: {
  categoryId: number;
  transactionIds?: number[];
  payeeIds?: number[];
  includeNoPayee?: boolean;
  setPayeeDefault?: boolean;
}): Promise<{ updated: number; defaultsSet: number }> =>
  api.post('/uncategorized/assign', data).then(r => r.data);
