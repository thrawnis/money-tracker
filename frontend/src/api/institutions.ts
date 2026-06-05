import api from './client';
import type { Institution } from '../types';

export const getInstitutions = () =>
  api.get<Institution[]>('/institutions').then(r => r.data);

export const createInstitution = (name: string) =>
  api.post<Institution>('/institutions', { name }).then(r => r.data);

export const updateInstitution = (id: number, name: string) =>
  api.put<Institution>(`/institutions/${id}`, { id, name }).then(r => r.data);

export const deleteInstitution = (id: number) =>
  api.delete(`/institutions/${id}`);
