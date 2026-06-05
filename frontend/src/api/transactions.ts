import client from './client';
import type { Transaction, TransactionPage } from '../types';

interface GetTransactionsParams {
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export const getTransactions = (accountId: number, params?: GetTransactionsParams) =>
  client
    .get<TransactionPage>(`/api/accounts/${accountId}/transactions`, { params })
    .then(r => r.data);

export const createTransaction = (accountId: number, data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt'>) =>
  client.post<Transaction>(`/api/accounts/${accountId}/transactions`, data).then(r => r.data);

export const updateTransaction = (accountId: number, id: number, data: Partial<Transaction>) =>
  client.put<Transaction>(`/api/accounts/${accountId}/transactions/${id}`, data).then(r => r.data);

export const deleteTransaction = (accountId: number, id: number) =>
  client.delete(`/api/accounts/${accountId}/transactions/${id}`);
