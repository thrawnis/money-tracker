import client from './client';
import type { Category } from '../types';

export const getCategories = () =>
  client.get<Category[]>('/api/categories').then(r => r.data);

export const createCategory = (data: Omit<Category, 'id' | 'subCategories'>) =>
  client.post<Category>('/api/categories', data).then(r => r.data);

export const updateCategory = (id: number, data: Partial<Category>) =>
  client.put<Category>(`/api/categories/${id}`, data).then(r => r.data);

export const deleteCategory = (id: number) =>
  client.delete(`/api/categories/${id}`);
