import api from './client';
import type { Payee } from '../types';

export const getPayees = () =>
  api.get<Payee[]>('/payees').then(r => r.data);

export const createPayee = (name: string) =>
  api.post<Payee>('/payees', { name }).then(r => r.data);

export const updatePayee = (id: number, data: { name?: string; defaultCategoryId?: number | null }) =>
  api.put<Payee>(`/payees/${id}`, data).then(r => r.data);

export const deletePayee = (id: number) =>
  api.delete(`/payees/${id}`);
