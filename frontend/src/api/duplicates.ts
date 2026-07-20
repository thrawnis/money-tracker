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
  ignored: boolean;
  transactions: DuplicateTransaction[];
}

export interface GetDuplicatesOptions {
  accountId?: number;
  includeMemo?: boolean;
  includeCategory?: boolean;
  showIgnored?: boolean;
}

export interface DuplicateGroupKey {
  accountId: number;
  date: string;
  amount: number;
}

export const getDuplicates = (opts: GetDuplicatesOptions = {}): Promise<DuplicateGroup[]> =>
  api.get('/duplicates', {
    params: {
      accountId: opts.accountId,
      includeMemo: opts.includeMemo || undefined,
      includeCategory: opts.includeCategory || undefined,
      showIgnored: opts.showIgnored || undefined,
    },
  }).then(r => r.data);

export const ignoreDuplicateGroup = (key: DuplicateGroupKey): Promise<void> =>
  api.post('/duplicates/ignore', key).then(() => undefined);

export const unignoreDuplicateGroup = (key: DuplicateGroupKey): Promise<void> =>
  api.post('/duplicates/unignore', key).then(() => undefined);
