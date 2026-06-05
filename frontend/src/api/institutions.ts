import client from './client';
import type { Institution } from '../types';

export const getInstitutions = () =>
  client.get<Institution[]>('/api/institutions').then(r => r.data);

export const createInstitution = (name: string) =>
  client.post<Institution>('/api/institutions', { name }).then(r => r.data);

export const updateInstitution = (id: number, name: string) =>
  client.put<Institution>(`/api/institutions/${id}`, { id, name }).then(r => r.data);

export const deleteInstitution = (id: number) =>
  client.delete(`/api/institutions/${id}`);
