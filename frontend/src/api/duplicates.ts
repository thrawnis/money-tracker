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

export const getDuplicates = (accountId?: number): Promise<DuplicateGroup[]> =>
  api.get('/duplicates', { params: accountId ? { accountId } : undefined }).then(r => r.data);
