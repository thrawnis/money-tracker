import api from './client';
import type { Transaction, TransactionPage } from '../types';
import type { SplitInput } from '../components/TransactionForm';

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

export const getTransaction = (accountId: number, id: number) =>
  api.get<Transaction>(`/accounts/${accountId}/transactions/${id}`).then(r => r.data);

export const createTransaction = (accountId: number, data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt' | 'splits'> & { splits?: SplitInput[] }) =>
  api.post<Transaction>(`/accounts/${accountId}/transactions`, data).then(r => r.data);

export const updateTransaction = (accountId: number, id: number, data: Omit<Partial<Transaction>, 'splits'> & { targetAccountId?: number; splits?: SplitInput[] }) =>
  api.put<Transaction>(`/accounts/${accountId}/transactions/${id}`, data).then(r => r.data);

export const deleteTransaction = (accountId: number, id: number) =>
  api.delete(`/accounts/${accountId}/transactions/${id}`);

export interface TransferDto {
  sourceAccountId: number;
  destinationAccountId: number;
  date: string;
  postDate?: string;
  amount: number;
  memo?: string;
}

export const createTransfer = (data: TransferDto) =>
  api.post<{ debit: Transaction; credit: Transaction }>('/transfers', data).then(r => r.data);
