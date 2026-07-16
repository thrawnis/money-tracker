import api from './client';
import type { Account } from '../types';

export const getAccounts = (includeInactive = false) =>
  api.get<Account[]>('/accounts', { params: includeInactive ? { includeInactive: true } : {} }).then(r => r.data);

export const getAccount = (id: number) =>
  api.get<Account>(`/accounts/${id}`).then(r => r.data);

export const createAccount = (data: Omit<Account, 'id' | 'createdAt'>) =>
  api.post<Account>('/accounts', data).then(r => r.data);

export const updateAccount = (id: number, data: Partial<Account>) =>
  api.put<Account>(`/accounts/${id}`, data).then(r => r.data);

// exportToken travels in a header, not the query string, so it never lands in
// server access logs or browser history.
export const deleteAccount = (id: number, exportToken: string, note?: string) =>
  api.delete(`/accounts/${id}`, {
    headers: { 'X-Export-Token': exportToken },
    params: note ? { note } : undefined,
  });
