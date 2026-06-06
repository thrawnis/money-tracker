import { useEffect, useState } from 'react';
import { BrowserRouter, Routes, Route, Navigate, Outlet } from 'react-router-dom';
import { AuthProvider, useAuth } from './contexts/AuthContext';
import Layout from './components/Layout';
import Dashboard from './pages/Dashboard';
import AccountRegister from './pages/AccountRegister';
import AccountForm from './pages/AccountForm';
import BillsReminders from './pages/BillsReminders';
import Reports from './pages/Reports';
import Settings from './pages/Settings';
import Categories from './pages/Categories';
import Login from './pages/auth/Login';
import Register from './pages/auth/Register';
import SetupTotp from './pages/auth/SetupTotp';
import TotpVerify from './pages/auth/TotpVerify';
import { getAccounts } from './api/accounts';
import type { Account } from './types';

function PrivateRoute() {
  const { user, loading } = useAuth();
  if (loading) return <div style={{ padding: 40, textAlign: 'center' }}>Loading…</div>;
  if (!user) return <Navigate to="/auth/login" replace />;
  return <Outlet />;
}

function AppRoutes() {
  const { user, logout } = useAuth();
  const [accounts, setAccounts] = useState<Account[]>([]);

  useEffect(() => {
    if (user) {
      getAccounts().then(setAccounts).catch(console.error);
    } else {
      setAccounts([]);
    }
  }, [user]);

  return (
    <Routes>
      <Route path="/auth/login" element={<Login />} />
      <Route path="/auth/register" element={<Register />} />
      <Route path="/auth/setup-totp" element={<SetupTotp />} />
      <Route path="/auth/totp" element={<TotpVerify />} />
      <Route element={<PrivateRoute />}>
        <Route element={<Layout accounts={accounts} onLogout={logout} />}>
          <Route index element={<Dashboard />} />
          <Route path="accounts/new" element={<AccountForm onCreated={() => getAccounts().then(setAccounts)} />} />
          <Route path="accounts/:id" element={<AccountRegister />} />
          <Route path="bills" element={<BillsReminders />} />
          <Route path="reports" element={<Reports />} />
          <Route path="categories" element={<Categories />} />
          <Route path="settings" element={<Settings />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Route>
    </Routes>
  );
}

export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter>
        <AppRoutes />
      </BrowserRouter>
    </AuthProvider>
  );
}
