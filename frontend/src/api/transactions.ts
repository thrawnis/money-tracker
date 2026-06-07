import api from './client';
import type { Transaction, TransactionPage } from '../types';

export interface GetTransactionsParams {
  from?: string;
  to?: string;
  minAmount?: number;
  maxAmount?: number;
  payeeName?: string;
  categoryId?: number;
  memo?: string;
  uncategorizedOnly?: boolean;
  sortBy?: string;
  sortDir?: 'asc' | 'desc';
  page?: number;
  pageSize?: number;
}

export const getTransactions = (accountId: number, params?: GetTransactionsParams) =>
  api
    .get<TransactionPage>(`/accounts/${accountId}/transactions`, { params })
    .then(r => r.data);

export const createTransaction = (accountId: number, data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt'>) =>
  api.post<Transaction>(`/accounts/${accountId}/transactions`, data).then(r => r.data);

export const updateTransaction = (accountId: number, id: number, data: Partial<Transaction> & { targetAccountId?: number }) =>
  api.put<Transaction>(`/accounts/${accountId}/transactions/${id}`, data).then(r => r.data);

export const deleteTransaction = (accountId: number, id: number) =>
  api.delete(`/accounts/${accountId}/transactions/${id}`);
