import api from './client';

export interface DuplicatePayee { id: number; name: string; transactionCount: number; hasDefaultCategory: boolean }
export interface DuplicateCategory { id: number; name: string; parentName?: string | null; transactionCount: number; subcategoryCount: number }

/** Suggested groups of probably-the-same names, most-used first within each group. */
export interface NameDuplicates { payees: DuplicatePayee[][]; categories: DuplicateCategory[][] }

export const findNameDuplicates = (): Promise<NameDuplicates> =>
  api.get('/name-duplicates').then(r => r.data);

export const mergePayees = (keepId: number, mergeIds: number[]) =>
  api.post<{ merged: number }>('/name-duplicates/payees/merge', { keepId, mergeIds }).then(r => r.data);

export const mergeCategories = (keepId: number, mergeIds: number[]) =>
  api.post<{ merged: number }>('/name-duplicates/categories/merge', { keepId, mergeIds }).then(r => r.data);
