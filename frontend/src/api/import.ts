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

/** A row that would be imported, keyed by its position in the file. */
export interface NewRow {
  row: number;
  date: string;
  payee: string;
  /** null when the row can't be parsed (it's reported as an error on import). */
  amount: number | null;
  memo?: string | null;
  /** Destination account name (multi-account files). */
  account?: string | null;
  /** Category it will get: the file's, else the default of the payee its name resolves to. */
  category?: string | null;
  /** The category comes from the file itself, so payee choices don't change it. */
  fileCategory?: boolean;
  /** Looks like the other side of a transfer and will be linked. */
  transfer: boolean;
  invalid: boolean;
}

export interface PreviewResult {
  total: number;
  duplicates: DuplicateRow[];
  newTransactions: number;
  newRows?: NewRow[];
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
  /** Row positions (NewRow.row) the user unticked. */
  excludeRows?: number[];
  /** Raw payee text -> name of a new payee to create for it ("create new payee"). */
  payeeNewNames?: Record<string, string>;
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
  if (opts.excludeRows?.length) fd.append('excludeRows', JSON.stringify(opts.excludeRows));
  if (opts.payeeNewNames) fd.append('payeeNewNames', JSON.stringify(opts.payeeNewNames));
  return api.post<ImportResult>('/import', fd).then(r => r.data);
};

// ── Import drafts (staged, uncommitted imports) ──

export interface ImportDraftSummary {
  id: number;
  accountId: number;
  accountName: string;
  fileName: string;
  rowCount: number;
  /** Staged by a SimpleFIN bank sync rather than a file upload. */
  fromBankSync?: boolean;
  /** Bank sync: the day this account was last approved through (YYYY-MM-DD). */
  syncPendingSince?: string | null;
  createdAt: string;
  updatedAt: string;
}

/** Window event fired after a draft is committed or discarded without a page change. */
export const IMPORT_DRAFTS_CHANGED = 'import-drafts-changed';

export const getImportDrafts = (): Promise<ImportDraftSummary[]> =>
  api.get('/import/drafts').then(r => r.data);

export interface ResumedImportDraft {
  draftId: number;
  accountId: number;
  fileName: string;
  includeDuplicateIds: number[];
  payeeOverrides: Record<string, number>;
  payeeNewNames?: Record<string, string>;
  excludedRows: number[];
  fromBankSync: boolean;
  preview: PreviewResult;
}

export const resumeImportDraft = (id: number): Promise<ResumedImportDraft> =>
  api.get(`/import/drafts/${id}/resume`).then(r => r.data);

export const updateImportDraft = (
  id: number,
  data: { includeDuplicateIds?: number[]; payeeOverrides?: Record<string, number>; excludedRows?: number[]; payeeNewNames?: Record<string, string> },
): Promise<void> =>
  api.put(`/import/drafts/${id}`, data).then(() => undefined);

export const deleteImportDraft = (id: number): Promise<void> =>
  api.delete(`/import/drafts/${id}`).then(() => undefined);
