import api from './client';

export interface IouPerson {
  id: number;
  name: string;
  notes?: string | null;
  /** Positive: they owe you. Negative: you owe them (or they've overpaid). */
  balance: number;
  openCount: number;
  oldestOpen?: string | null;
}

/** The purchase or bank transaction an IOU or payment is linked to. */
export interface IouLinkedTransaction {
  id: number;
  accountId: number;
  accountName: string;
  date: string;
  payee?: string | null;
  amount: number;
}

export interface IouEntry {
  id: number;
  personId: number;
  personName: string;
  date: string;
  /** Positive: they owe you. Negative: you owe them. */
  amount: number;
  description?: string | null;
  notes?: string | null;
  transaction?: IouLinkedTransaction | null;
  /** How much payments have covered so far. */
  covered: number;
  remaining: number;
  paidOffDate?: string | null;
  /** The paid off date was set by hand (settled outside the app or forgiven). */
  paidOffManually: boolean;
}

export type IouDirection = 'Received' | 'Paid';
export const IOU_METHODS = ['Cash', 'Zelle', 'Venmo', 'PayPal', 'Check', 'BankTransfer', 'Other'] as const;
export type IouMethod = typeof IOU_METHODS[number];
export const methodLabel = (m: string) => (m === 'BankTransfer' ? 'Bank transfer' : m);

export interface IouPayment {
  id: number;
  personId: number;
  personName: string;
  date: string;
  /** Always positive; direction gives the sense. */
  amount: number;
  direction: IouDirection;
  method: IouMethod;
  notes?: string | null;
  transaction?: IouLinkedTransaction | null;
  appliesToEntryId?: number | null;
  /** How much of it has gone to IOUs; the rest is credit. */
  allocated: number;
}

export interface IouPage {
  people: IouPerson[];
  /** People with a non-zero balance. */
  summary: IouPerson[];
  totals: { owedToMe: number; iOwe: number; peopleOwingMe: number; peopleIOwe: number };
  entries: IouEntry[];
  payments: IouPayment[];
}

export const getIous = (opts: { personId?: number; showPaid?: boolean } = {}): Promise<IouPage> =>
  api.get('/ious', { params: opts }).then(r => r.data);

export interface IouLine { personId?: number; newPersonName?: string; amount: number }

export const createIous = (data: {
  date: string; description?: string; notes?: string; transactionId?: number; lines: IouLine[];
}): Promise<{ ids: number[] }> => api.post('/ious/entries', data).then(r => r.data);

export const updateIou = (id: number, data: {
  personId: number; date: string; amount: number; description?: string; notes?: string;
  transactionId?: number | null; paidOffDate?: string | null; paidOffManually: boolean;
}) => api.put(`/ious/entries/${id}`, data);

export const deleteIou = (id: number) => api.delete(`/ious/entries/${id}`);

export interface IouPaymentInput {
  personId: number; date: string; amount: number; direction: IouDirection; method: IouMethod;
  notes?: string; transactionId?: number | null; appliesToEntryId?: number | null;
}
export const createIouPayment = (data: IouPaymentInput): Promise<{ id: number }> =>
  api.post('/ious/payments', data).then(r => r.data);
export const updateIouPayment = (id: number, data: IouPaymentInput) => api.put(`/ious/payments/${id}`, data);
export const deleteIouPayment = (id: number) => api.delete(`/ious/payments/${id}`);

export const createIouPerson = (name: string): Promise<{ id: number; name: string }> =>
  api.post('/ious/people', { name }).then(r => r.data);
export const updateIouPerson = (id: number, name: string, notes?: string) => api.put(`/ious/people/${id}`, { name, notes });
export const deleteIouPerson = (id: number) => api.delete(`/ious/people/${id}`);

export interface IouTransactionView {
  entries: (Omit<IouEntry, 'personName' | 'transaction' | 'remaining'> & { personName: string })[];
  owedToMe: number;
  receivedSoFar: number;
  iOwe: number;
  paidSoFar: number;
}
export const getIousForTransaction = (transactionId: number): Promise<IouTransactionView> =>
  api.get(`/ious/transactions/${transactionId}`).then(r => r.data);

export const exportIous = (personId?: number): Promise<Blob> =>
  api.get('/ious/export', { params: { personId }, responseType: 'blob' }).then(r => r.data);

/** What the register shows on a transaction tagged with IOUs or linked to an IOU payment. */
export interface IouBadge {
  transactionId: number;
  people: string[];
  owedToMe: number;
  receivedSoFar: number;
  iOwe: number;
  paidSoFar: number;
  payments: { person: string; amount: number; direction: IouDirection }[];
}

export const getIouBadges = (transactionIds: number[]): Promise<IouBadge[]> =>
  transactionIds.length === 0
    ? Promise.resolve([])
    : api.get('/ious/by-transactions', { params: { ids: transactionIds.join(',') } }).then(r => r.data);
