import api from './client';

export interface SearchTransaction {
  id: number;
  accountId: number;
  accountName: string;
  date: string;
  payee?: string;
  category?: string;
  categoryParentId?: number;
  memo?: string;
  amount: number;
  status: string;
}

export interface SearchResult {
  total: number;
  page: number;
  pageSize: number;
  items: SearchTransaction[];
}

export interface SearchParams {
  categoryId?: number;
  payeeId?: number;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export const searchTransactions = (params: SearchParams) =>
  api.get<SearchResult>('/transactions', { params }).then(r => r.data);
