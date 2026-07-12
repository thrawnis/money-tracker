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
  transferMatches: number;
  warnings?: string[];
  error?: string;
}

// accountId is required for a QIF file that has no embedded account section
// (the common single-account Money-Sunset export); harmless to omit otherwise.
export const previewImport = (file: File, accountId?: number): Promise<PreviewResult> => {
  const fd = new FormData();
  fd.append('file', file);
  if (accountId != null) fd.append('accountId', String(accountId));
  return api.post<PreviewResult>('/import/preview', fd).then(r => r.data);
};

export const importWithDuplicates = (
  file: File,
  includeDuplicateIds: number[],
  accountId?: number,
): Promise<{ imported: number; transfersLinked: number; errors?: string[] }> => {
  const fd = new FormData();
  fd.append('file', file);
  fd.append('includeDuplicateIds', JSON.stringify(includeDuplicateIds));
  if (accountId != null) fd.append('accountId', String(accountId));
  return api.post<{ imported: number; transfersLinked: number; errors?: string[] }>('/import', fd).then(r => r.data);
};
