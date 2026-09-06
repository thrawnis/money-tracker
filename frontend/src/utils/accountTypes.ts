import type { AccountType } from '../types';

/**
 * Display text for each AccountType value. Kept in one place after a real
 * bug: Dashboard.tsx rendered the raw enum value ("CreditCard") as a heading
 * instead of a proper label, because the label map already existed in
 * AccountsList.tsx/AccountForm.tsx/AccountEditModal.tsx but Dashboard grew
 * its own account-type grouping later and nobody reused it.
 *
 * Singular form, for a single account (form dropdowns, detail views).
 */
export const ACCOUNT_TYPE_LABELS: Record<AccountType, string> = {
  Checking:   'Checking',
  Savings:    'Savings',
  CreditCard: 'Credit Card',
  Loan:       'Loan',
  Investment: 'Investment',
  Cash:       'Cash',
  Other:      'Other',
};

/** Plural form, for a heading grouping multiple accounts of that type. */
export const ACCOUNT_TYPE_GROUP_LABELS: Record<AccountType, string> = {
  Checking:   'Checking',
  Savings:    'Savings',
  CreditCard: 'Credit Cards',
  Loan:       'Loans',
  Investment: 'Investments',
  Cash:       'Cash',
  Other:      'Other',
};
