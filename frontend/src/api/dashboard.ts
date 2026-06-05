import api from './client';

export interface DashboardAccount {
  id: number;
  name: string;
  type: string;
  institution?: string;
  openingBalance: number;
  currentBalance: number;
}

export interface UpcomingBill {
  id: number;
  name: string;
  accountId: number;
  payeeName?: string;
  amount: number;
  nextDueDate: string;
  daysUntilDue: number;
  isOverdue: boolean;
}

export interface UncategorizedSample {
  id: number;
  date: string;
  payee?: string;
  amount: number;
  accountName: string;
  accountId: number;
}

export interface DashboardData {
  accounts: DashboardAccount[];
  upcomingBills: UpcomingBill[];
  uncategorizedCount: number;
  uncategorizedSamples: UncategorizedSample[];
}

export const getDashboard = () => api.get<DashboardData>('/dashboard').then(r => r.data);
