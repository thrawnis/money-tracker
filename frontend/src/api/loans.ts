import api from './client';

export interface LoanDetails {
  annualRate: number;
  originalAmount?: number | null;
  termMonths?: number | null;
  firstPaymentDate?: string | null;
  /** Monthly principal + interest; blank = worked out from amount, rate and term. */
  monthlyPayment?: number | null;
  escrowAmount: number;
  autoCharges: boolean;
  autoChargesFrom?: string | null;
  interestCategoryId?: number | null;
  escrowCategoryId?: number | null;
}

export interface LoanInfo {
  details: LoanDetails | null;
  /** The payment the details imply (entered, or worked out). */
  monthlyPayment: number | null;
  /** What's owed now, as a positive number. */
  owed: number;
  /** "bank": the lender's reported balance (balance-only sync); "register": from the transactions. */
  balanceSource: 'bank' | 'register';
  balanceDate: string;
  nextPaymentDate: string | null;
  interestThisYear: number;
  today: string;
}

export const getLoan = (accountId: number): Promise<LoanInfo> => api.get(`/accounts/${accountId}/loan`).then(r => r.data);
export const saveLoan = (accountId: number, d: LoanDetails): Promise<LoanInfo> =>
  api.put(`/accounts/${accountId}/loan`, d).then(r => r.data);
export const removeLoan = (accountId: number) => api.delete(`/accounts/${accountId}/loan`);
