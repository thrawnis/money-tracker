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
  currentBalance?: number;
  lastTransactionDate?: string;
  transactionCount?: number;
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
  postDate?: string;      // Date transaction posted to statement (optional)
  checkNumber?: string;
  payeeId?: number;
  payee?: Payee;
  categoryId?: number;
  category?: Category;
  memo?: string;
  amount: number;
  status: TransactionStatus;
  transferTransactionId?: number;
  transferAccountId?: number;
  runningBalance?: number; // server-computed over full account history (date order)
  createdAt: string;
  updatedAt: string;
}

export interface TransactionPage {
  total: number;
  page: number;
  pageSize: number;
  currentBalance: number; // server-computed: opening balance + all transactions
  items: Transaction[];
}

export interface Category {
  id: number;
  name: string;
  parentId?: number;
  subCategories?: Category[];
  lastUsed?: string;
}

export interface Payee {
  id: number;
  name: string;
  defaultCategoryId?: number;
  lastUsed?: string;
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
  transferAccountId?: number;
}
