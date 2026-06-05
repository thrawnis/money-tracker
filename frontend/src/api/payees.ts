import api from './client';
import type { Payee } from '../types';

export const getPayees = () =>
  api.get<Payee[]>('/payees').then(r => r.data);

export const createPayee = (name: string) =>
  api.post<Payee>('/payees', { name }).then(r => r.data);
