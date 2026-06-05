import client from './client';
import type { ScheduledTransaction } from '../types';

export const getScheduledTransactions = () =>
  client.get<ScheduledTransaction[]>('/api/scheduledtransactions').then(r => r.data);

export const getUpcoming = (days = 14) =>
  client.get<ScheduledTransaction[]>('/api/scheduledtransactions/upcoming', { params: { days } }).then(r => r.data);

export const createScheduledTransaction = (data: Omit<ScheduledTransaction, 'id'>) =>
  client.post<ScheduledTransaction>('/api/scheduledtransactions', data).then(r => r.data);

export const updateScheduledTransaction = (id: number, data: Partial<ScheduledTransaction>) =>
  client.put<ScheduledTransaction>(`/api/scheduledtransactions/${id}`, data).then(r => r.data);

export const deleteScheduledTransaction = (id: number) =>
  client.delete(`/api/scheduledtransactions/${id}`);
