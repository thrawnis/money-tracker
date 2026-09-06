import api from './client';

export interface SearchTransaction {
  id: number;
  accountId: number;
  accountName: string;
  date: string;
  payee?: string;
  category?: string;
  categoryParentId?: number;
  splitCount?: number;
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
  accountIds?: number[];
  payeeName?: string;
  memo?: string;
  uncategorized?: boolean;
  page?: number;
  pageSize?: number;
}

export const searchTransactions = (params: SearchParams) => {
  const { accountIds, ...rest } = params;
  return api.get<SearchResult>('/transactions', {
    params: { ...rest, ...(accountIds?.length ? { accountIds } : {}) },
    paramsSerializer: p => {
      const parts: string[] = [];
      for (const [k, v] of Object.entries(p)) {
        if (v === undefined || v === null) continue;
        if (Array.isArray(v)) {
          for (const item of v) parts.push(`${k}=${encodeURIComponent(item)}`);
        } else {
          parts.push(`${k}=${encodeURIComponent(String(v))}`);
        }
      }
      return parts.join('&');
    },
  }).then(r => r.data);
};
