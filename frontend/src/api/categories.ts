import api from './client';
import type { Category } from '../types';

export const getCategories = () =>
  api.get<Category[]>('/categories').then(r => r.data);

export const createCategory = (data: Omit<Category, 'id' | 'subCategories'>) =>
  api.post<Category>('/categories', data).then(r => r.data);

export const updateCategory = (id: number, data: Partial<Category>) =>
  api.put<Category>(`/categories/${id}`, data).then(r => r.data);

export const deleteCategory = (id: number) =>
  api.delete(`/categories/${id}`);
