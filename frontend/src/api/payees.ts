import api from './client';
import type { Payee } from '../types';

export const getPayees = () =>
  api.get<Payee[]>('/payees').then(r => r.data);

// defaultCategoryId, when given, seeds the payee's default category from the
// transaction that's creating it — only meaningful the first time a payee is
// created (an existing payee's default is only changed via updatePayee).
export const createPayee = (name: string, defaultCategoryId?: number) =>
  api.post<Payee>('/payees', { name, defaultCategoryId }).then(r => r.data);

// Full PUT — all three fields are required by the backend and always
// overwritten. Setting blockAutoDefaultCategory true also clears
// defaultCategoryId server-side, regardless of what's passed here.
export const updatePayee = (id: number, data: { name: string; defaultCategoryId: number | null; blockAutoDefaultCategory?: boolean }) =>
  api.put<Payee>(`/payees/${id}`, data).then(r => r.data);

export const deletePayee = (id: number) =>
  api.delete(`/payees/${id}`);

/** Makes a category the payee's default; optionally sets it on the payee's uncategorized transactions too. */
export const setPayeeDefaultCategory = (payeeId: number, categoryId: number, applyToUncategorized: boolean) =>
  api.post<{ defaultCategoryId: number; applied: number }>(`/payees/${payeeId}/default-category`, { categoryId, applyToUncategorized })
    .then(r => r.data);
