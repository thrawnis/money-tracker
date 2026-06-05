import api from './client';
import type { ScheduledTransaction } from '../types';

export const getScheduledTransactions = () =>
  api.get<ScheduledTransaction[]>('/scheduledtransactions').then(r => r.data);

export const getUpcoming = (days = 14) =>
  api.get<ScheduledTransaction[]>('/scheduledtransactions/upcoming', { params: { days } }).then(r => r.data);

export const createScheduledTransaction = (data: Omit<ScheduledTransaction, 'id'>) =>
  api.post<ScheduledTransaction>('/scheduledtransactions', data).then(r => r.data);

export const updateScheduledTransaction = (id: number, data: Partial<ScheduledTransaction>) =>
  api.put<ScheduledTransaction>(`/scheduledtransactions/${id}`, data).then(r => r.data);

export const deleteScheduledTransaction = (id: number) =>
  api.delete(`/scheduledtransactions/${id}`);
