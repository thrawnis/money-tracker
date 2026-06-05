import api from './client';

export interface MonthlyReportParams {
  fromYear: number;
  fromMonth: number;
  toYear: number;
  toMonth: number;
  accountIds?: number[];
  categoryIds?: number[];
}

export interface MonthlyReportRow {
  categoryId?: number;
  categoryName: string;
  months: Record<string, number>;
  total: number;
}

export interface MonthlyReport {
  months: string[];
  rows: MonthlyReportRow[];
  totals: Record<string, number>;
}

export interface CategoryReportParams {
  categoryId?: number;
  from?: string;
  to?: string;
  accountIds?: number[];
  payeeIds?: number[];
}

export interface CategoryReportItem {
  id: number;
  date: string;
  accountId: number;
  accountName: string;
  payee?: string;
  memo?: string;
  amount: number;
}

export interface CategoryReport {
  items: CategoryReportItem[];
  total: number;
  count: number;
}

export interface SavedReport {
  id: number;
  name: string;
  type: 'monthly' | 'category';
  params: object;
  createdAt: string;
}

export const getMonthlyReport = (params: MonthlyReportParams) =>
  api.get<MonthlyReport>('/reports/monthly', { params }).then(r => r.data);

export const getCategoryReport = (params: CategoryReportParams) =>
  api.get<CategoryReport>('/reports/category', { params }).then(r => r.data);

export const getSavedReports = () =>
  api.get<SavedReport[]>('/reports/saved').then(r => r.data);

export const createSavedReport = (data: Omit<SavedReport, 'id' | 'createdAt'>) =>
  api.post<SavedReport>('/reports/saved', data).then(r => r.data);

export const updateSavedReport = (id: number, data: Partial<SavedReport>) =>
  api.put<SavedReport>(`/reports/saved/${id}`, data).then(r => r.data);

export const deleteSavedReport = (id: number) =>
  api.delete(`/reports/saved/${id}`);
