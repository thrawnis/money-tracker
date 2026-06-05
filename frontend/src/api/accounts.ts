import api from './client';
import type { Account } from '../types';

export const getAccounts = () =>
  api.get<Account[]>('/accounts').then(r => r.data);

export const getAccount = (id: number) =>
  api.get<Account>(`/accounts/${id}`).then(r => r.data);

export const createAccount = (data: Omit<Account, 'id' | 'createdAt'>) =>
  api.post<Account>('/accounts', data).then(r => r.data);

export const updateAccount = (id: number, data: Partial<Account>) =>
  api.put<Account>(`/accounts/${id}`, data).then(r => r.data);

export const deleteAccount = (id: number) =>
  api.delete(`/accounts/${id}`);
