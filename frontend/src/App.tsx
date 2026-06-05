import { useEffect, useState } from 'react';
import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import Layout from './components/Layout';
import Dashboard from './pages/Dashboard';
import AccountRegister from './pages/AccountRegister';
import BillsReminders from './pages/BillsReminders';
import Reports from './pages/Reports';
import Import from './pages/Import';
import { getAccounts } from './api/accounts';
import type { Account } from './types';

export default function App() {
  const [accounts, setAccounts] = useState<Account[]>([]);

  useEffect(() => {
    getAccounts().then(setAccounts).catch(console.error);
  }, []);

  return (
    <BrowserRouter>
      <Routes>
        <Route element={<Layout accounts={accounts} />}>
          <Route index element={<Dashboard />} />
          <Route path="accounts/:id" element={<AccountRegister />} />
          <Route path="bills" element={<BillsReminders />} />
          <Route path="reports" element={<Reports />} />
          <Route path="import" element={<Import />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </BrowserRouter>
  );
}
