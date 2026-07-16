import api from './client';
import type { TransactionStatus } from '../types';

export interface DuplicateTransaction {
  id: number;
  payee?: string;
  category?: string;
  memo?: string;
  checkNumber?: string;
  status: TransactionStatus;
  createdAt: string;
}

export interface DuplicateGroup {
  accountId: number;
  accountName: string;
  date: string;
  amount: number;
  transactions: DuplicateTransaction[];
}

export interface GetDuplicatesOptions {
  accountId?: number;
  includeMemo?: boolean;
  includeCategory?: boolean;
}

export const getDuplicates = (opts: GetDuplicatesOptions = {}): Promise<DuplicateGroup[]> =>
  api.get('/duplicates', {
    params: {
      accountId: opts.accountId,
      includeMemo: opts.includeMemo || undefined,
      includeCategory: opts.includeCategory || undefined,
    },
  }).then(r => r.data);
