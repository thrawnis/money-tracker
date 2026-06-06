import api from './client';

export const importFile = (file: File) => {
  const fd = new FormData();
  fd.append('file', file);
  return api.post('/import', fd).then(r => r.data);
};

export const getTemplate = (format: 'csv' | 'xlsx') =>
  api.get(`/import/template/${format}`, { responseType: 'blob' }).then(r => r.data);

export interface DuplicateRow {
  date: string;
  payee: string;
  amount: number;
  memo?: string;
  matchedTransactionId: number;
}

export interface PreviewResult {
  total: number;
  duplicates: DuplicateRow[];
  newTransactions: number;
  error?: string;
}

export const previewImport = (file: File): Promise<PreviewResult> => {
  const fd = new FormData();
  fd.append('file', file);
  return api.post<PreviewResult>('/import/preview', fd).then(r => r.data);
};

export const importWithDuplicates = (
  file: File,
  includeDuplicateIds: number[],
): Promise<{ imported: number; errors?: string[] }> => {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('includeDuplicateIds', JSON.stringify(includeDuplicateIds));
  return api.post<{ imported: number; errors?: string[] }>('/import', fd).then(r => r.data);
};
