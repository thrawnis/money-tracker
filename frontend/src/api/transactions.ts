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
  // Renamed from the mismatched `uncategorizedOnly` this was previously sent
  // as — the backend's query param has always been named `uncategorized`, so
  // the "Uncategorized only" checkbox silently did nothing before this fix.
  uncategorized?: boolean;
  status?: string;
  voided?: boolean;
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

export const createTransaction = (accountId: number, data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt' | 'splits' | 'isVoided'> & { splits?: SplitInput[] }) =>
  api.post<Transaction>(`/accounts/${accountId}/transactions`, data).then(r => r.data);

// Full PUT — the backend overwrites every field from the body, so partial
// objects would null out whatever they omit. Status-only changes go through
// updateTransactionStatus instead.
export const updateTransaction = (accountId: number, id: number, data: Omit<Transaction, 'id' | 'accountId' | 'createdAt' | 'updatedAt' | 'splits' | 'isVoided'> & { targetAccountId?: number; transferDestAccountId?: number; splits?: SplitInput[] }) =>
  api.put<Transaction>(`/accounts/${accountId}/transactions/${id}`, data).then(r => r.data);

export const updateTransactionStatus = (accountId: number, id: number, status: Transaction['status']) =>
  api.patch(`/accounts/${accountId}/transactions/${id}/status`, { status });

export const updateTransactionVoided = (accountId: number, id: number, isVoided: boolean) =>
  api.patch(`/accounts/${accountId}/transactions/${id}/void`, { isVoided });

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
