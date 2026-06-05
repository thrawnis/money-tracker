export interface Institution {
  id: number;
  name: string;
}

export type AccountType =
  | 'Checking'
  | 'Savings'
  | 'CreditCard'
  | 'Cash'
  | 'Loan'
  | 'Investment'
  | 'Other';

export interface Account {
  id: number;
  name: string;
  type: AccountType;
  openingBalance: number;
  institutionId?: number;
  institution?: Institution;
  accountNumber?: string;
  notes?: string;
  isActive: boolean;
  createdAt: string;
}

export type TransactionStatus = 'Uncleared' | 'Cleared' | 'Reconciled';

export interface Transaction {
  id: number;
  accountId: number;
  date: string;           // ISO date string YYYY-MM-DD
  checkNumber?: string;
  payeeId?: number;
  payee?: Payee;
  categoryId?: number;
  category?: Category;
  memo?: string;
  amount: number;
  status: TransactionStatus;
  transferTransactionId?: number;
  createdAt: string;
  updatedAt: string;
}

export interface TransactionPage {
  total: number;
  page: number;
  pageSize: number;
  items: Transaction[];
}

export interface Category {
  id: number;
  name: string;
  parentId?: number;
  subCategories?: Category[];
}

export interface Payee {
  id: number;
  name: string;
  defaultCategoryId?: number;
}

export type FrequencyUnit = 'Days' | 'Weeks' | 'Months' | 'Years';

export interface ScheduledTransaction {
  id: number;
  name: string;
  accountId: number;
  account?: Account;
  payeeId?: number;
  payee?: Payee;
  categoryId?: number;
  category?: Category;
  memo?: string;
  amount: number;
  frequencyInterval: number;
  frequencyUnit: FrequencyUnit;
  nextDueDate: string;
  reminderDays: number;
  isActive: boolean;
}
