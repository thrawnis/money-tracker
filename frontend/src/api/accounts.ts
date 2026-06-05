import client from './client';
import type { Account } from '../types';

export const getAccounts = () =>
  client.get<Account[]>('/api/accounts').then(r => r.data);

export const getAccount = (id: number) =>
  client.get<Account>(`/api/accounts/${id}`).then(r => r.data);

export const createAccount = (data: Omit<Account, 'id' | 'createdAt'>) =>
  client.post<Account>('/api/accounts', data).then(r => r.data);

export const updateAccount = (id: number, data: Partial<Account>) =>
  client.put<Account>(`/api/accounts/${id}`, data).then(r => r.data);

export const deleteAccount = (id: number) =>
  client.delete(`/api/accounts/${id}`);
