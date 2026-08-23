import { createBrowserRouter, createRoutesFromElements, RouterProvider, Route, Navigate } from 'react-router-dom';
import { AuthProvider } from './contexts/AuthContext';
import { useAuth } from './contexts/auth-context';
import Layout from './components/Layout';
import Dashboard from './pages/Dashboard';
import AccountsList from './pages/AccountsList';
import AccountRegister from './pages/AccountRegister';
import AccountForm from './pages/AccountForm';
import BillsReminders from './pages/BillsReminders';
import Reports from './pages/Reports';
import Settings from './pages/Settings';
import Utilities from './pages/Utilities';
import Categories from './pages/Categories';
import Payees from './pages/Payees';
import TransactionSearch from './pages/TransactionSearch';
import AllTransactions from './pages/AllTransactions';
import Login from './pages/auth/Login';
import Register from './pages/auth/Register';
import SetupTotp from './pages/auth/SetupTotp';
import TotpVerify from './pages/auth/TotpVerify';
import RequireTimeZone from './pages/auth/RequireTimeZone';

// Combined auth guard + layout — useAuth is available because AuthProvider wraps RouterProvider
function AppShell() {
  const { user, loading, logout } = useAuth();
  if (loading) return <div style={{ padding: 40, textAlign: 'center' }}>Loading…</div>;
  if (!user) return <Navigate to="/auth/login" replace />;
  // Accounts predating the timezone requirement are held here before anything
  // else renders. Deliberately not a route — there'd be a URL around it — and
  // deliberately not the only enforcement: the API returns 428 for these users
  // regardless of what the client does (see RequireTimeZoneFilter).
  if (!user.timeZoneId) return <RequireTimeZone />;
  return <Layout onLogout={logout} />;
}

const router = createBrowserRouter(
  createRoutesFromElements(
    <>
      <Route path="/auth/login"      element={<Login />} />
      <Route path="/auth/register"   element={<Register />} />
      <Route path="/auth/setup-totp" element={<SetupTotp />} />
      <Route path="/auth/totp"       element={<TotpVerify />} />
      <Route element={<AppShell />}>
        <Route index                          element={<Dashboard />} />
        <Route path="accounts"                element={<AccountsList />} />
        <Route path="accounts/new"            element={<AccountForm />} />
        <Route path="accounts/:id"            element={<AccountRegister />} />
        <Route path="bills"                   element={<BillsReminders />} />
        <Route path="reports"                 element={<Reports />} />
        <Route path="categories"              element={<Categories />} />
        <Route path="payees"                  element={<Payees />} />
        <Route path="transactions"            element={<TransactionSearch />} />
        <Route path="all-transactions"        element={<AllTransactions />} />
        <Route path="utilities"               element={<Utilities />} />
        <Route path="settings"                element={<Settings />} />
        <Route path="*"                       element={<Navigate to="/" replace />} />
      </Route>
    </>
  )
);

export default function App() {
  return (
    <AuthProvider>
      <RouterProvider router={router} />
    </AuthProvider>
  );
}
