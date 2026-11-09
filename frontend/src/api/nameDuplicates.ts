import api from './client';

export interface DuplicatePayee { id: number; name: string; transactionCount: number; hasDefaultCategory: boolean }
export interface DuplicateCategory { id: number; name: string; parentName?: string | null; transactionCount: number; subcategoryCount: number }

/** A suggested group of probably-the-same names, most-used first. */
export interface NameGroup<T> { dismissed: boolean; items: T[] }
export interface NameDuplicates { payees: NameGroup<DuplicatePayee>[]; categories: NameGroup<DuplicateCategory>[] }

export const findNameDuplicates = (showDismissed = false): Promise<NameDuplicates> =>
  api.get('/name-duplicates', { params: { showDismissed } }).then(r => r.data);

/** "Not duplicates": stays hidden while the group's members stay the same. */
export const dismissNameGroup = (kind: 'payee' | 'category', ids: number[]) =>
  api.post('/name-duplicates/dismiss', { kind, ids });

export const undismissNameGroup = (kind: 'payee' | 'category', ids: number[]) =>
  api.post('/name-duplicates/undismiss', { kind, ids });

export const mergePayees = (keepId: number, mergeIds: number[]) =>
  api.post<{ merged: number }>('/name-duplicates/payees/merge', { keepId, mergeIds }).then(r => r.data);

export const mergeCategories = (keepId: number, mergeIds: number[]) =>
  api.post<{ merged: number }>('/name-duplicates/categories/merge', { keepId, mergeIds }).then(r => r.data);
