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
  firstTransactionDate?: string;
  transactionCount?: number;
  institutionId?: number;
  institution?: Institution;
  accountNumber?: string;
  notes?: string;
  isActive: boolean;
  createdAt: string;
}

export type TransactionStatus = 'Uncleared' | 'Cleared' | 'Reconciled';

export interface TransactionSplit {
  id: number;
  categoryId?: number;
  category?: Category;
  amount: number;
  memo?: string;
}

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
  // Present only when split across multiple categories; categoryId/category
  // are null/undefined in that case — the splits carry the breakdown.
  splits?: TransactionSplit[];
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
  firstUsed?: string;
  lastUsed?: string;
  transactionCount?: number;
}

export interface Payee {
  id: number;
  name: string;
  defaultCategoryId?: number;
  firstUsed?: string;
  lastUsed?: string;
  transactionCount?: number;
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
  /** Only meaningful when frequencyUnit is 'Weeks': bitmask of specific
   *  weekdays (bit N = JS/CSS Date.getDay() value N, Sunday=1, ... Saturday=64).
   *  Undefined/0 means the plain frequencyInterval-weeks behavior applies. */
  daysOfWeekMask?: number;
  nextDueDate: string;
  reminderDays: number;
  isActive: boolean;
  transferAccountId?: number;
}
