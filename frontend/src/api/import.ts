import api from './client';

export const getTemplate = (format: 'csv' | 'xlsx') =>
  api.get(`/import/template/${format}`, { responseType: 'blob' }).then(r => r.data);

export interface DuplicateRow {
  date: string;
  payee: string;
  amount: number;
  memo?: string;
  matchedTransactionId: number;
}

export interface UnmatchedPayeeSuggestion {
  id: number;
  name: string;
}

export interface UnmatchedPayee {
  rawText: string;
  suggestions: UnmatchedPayeeSuggestion[];
}

export interface PreviewResult {
  total: number;
  duplicates: DuplicateRow[];
  newTransactions: number;
  transferMatches: number;
  warnings?: string[];
  error?: string;
  unmatchedPayees?: UnmatchedPayee[];
  draftId?: number;
}

// accountId is required for a QIF file that has no embedded account section
// (the common single-account Money-Sunset export); harmless to omit otherwise.
// Previewing a single-account file also stages it as an ImportDraft server-side
// (see ResumeImportDraft) so an interruption before committing isn't lost.
export const previewImport = (file: File, accountId?: number): Promise<PreviewResult> => {
  const fd = new FormData();
  fd.append('file', file);
  if (accountId != null) fd.append('accountId', String(accountId));
  return api.post<PreviewResult>('/import/preview', fd).then(r => r.data);
};

export interface ImportOptions {
  file?: File;
  draftId?: number;
  accountId?: number;
  includeDuplicateIds: number[];
  payeeOverrides?: Record<string, number>;
  rememberPayeeMappings?: string[];
}

export interface ImportResult {
  imported: number;
  transfersLinked: number;
  errors?: string[];
  newPayeesCreated?: { id: number; name: string; rawText: string }[];
}

export const importWithDuplicates = (opts: ImportOptions): Promise<ImportResult> => {
  const fd = new FormData();
  if (opts.file) fd.append('file', opts.file);
  if (opts.draftId != null) fd.append('draftId', String(opts.draftId));
  fd.append('includeDuplicateIds', JSON.stringify(opts.includeDuplicateIds));
  if (opts.accountId != null) fd.append('accountId', String(opts.accountId));
  if (opts.payeeOverrides) fd.append('payeeOverrides', JSON.stringify(opts.payeeOverrides));
  if (opts.rememberPayeeMappings) fd.append('rememberPayeeMappings', JSON.stringify(opts.rememberPayeeMappings));
  return api.post<ImportResult>('/import', fd).then(r => r.data);
};

// ── Import drafts (staged, uncommitted imports) ──

export interface ImportDraftSummary {
  id: number;
  accountId: number;
  accountName: string;
  fileName: string;
  rowCount: number;
  createdAt: string;
  updatedAt: string;
}

export const getImportDrafts = (): Promise<ImportDraftSummary[]> =>
  api.get('/import/drafts').then(r => r.data);

export interface ResumedImportDraft {
  draftId: number;
  accountId: number;
  fileName: string;
  includeDuplicateIds: number[];
  payeeOverrides: Record<string, number>;
  preview: PreviewResult;
}

export const resumeImportDraft = (id: number): Promise<ResumedImportDraft> =>
  api.get(`/import/drafts/${id}/resume`).then(r => r.data);

export const updateImportDraft = (
  id: number,
  data: { includeDuplicateIds?: number[]; payeeOverrides?: Record<string, number> },
): Promise<void> =>
  api.put(`/import/drafts/${id}`, data).then(() => undefined);

export const deleteImportDraft = (id: number): Promise<void> =>
  api.delete(`/import/drafts/${id}`).then(() => undefined);
