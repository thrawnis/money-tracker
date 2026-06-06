import { createContext, useContext, useEffect, useState, useCallback, type ReactNode } from 'react';
import { setAccessToken } from '../api/client';
import * as authApi from '../api/auth';
import type { AuthUser } from '../types/auth';

interface AuthContextValue {
  user: AuthUser | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<{ mfaRequired?: boolean; mfaSetupRequired?: boolean }>;
  logout: () => Promise<void>;
  refreshToken: () => Promise<void>;
  setTokenAndUser: (token: string, user: AuthUser) => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

function parseJwt(token: string): { sub: string; email: string; role: string } | null {
  try {
    const payload = token.split('.')[1];
    return JSON.parse(atob(payload.replace(/-/g, '+').replace(/_/g, '/')));
  } catch {
    return null;
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);

  const applyToken = useCallback((token: string) => {
    setAccessToken(token);
    const payload = parseJwt(token);
    if (payload) {
      setUser({
        id: payload.sub,
        email: payload.email,
        role: payload.role as 'Admin' | 'Standard',
      });
    }
  }, []);

  const setTokenAndUser = useCallback((token: string, _user: AuthUser) => {
    setAccessToken(token);
    setUser(_user);
  }, []);

  const refreshToken = useCallback(async () => {
    try {
      const data = await authApi.refreshTokens();
      applyToken(data.accessToken);
    } catch (err: unknown) {
      // Only clear the session on an explicit 401 (invalid/expired token).
      // Network errors or 5xx during a deployment restart should not log the
      // user out — the cookie is still valid and will work once the API recovers.
      const status = (err as { response?: { status?: number } })?.response?.status;
      if (status === 401) {
        setAccessToken(null);
        setUser(null);
      }
    }
  }, [applyToken]);

  // Silent refresh on mount
  useEffect(() => {
    refreshToken().finally(() => setLoading(false));
  }, [refreshToken]);

  // Re-authenticate when the tab becomes visible again after being hidden
  // (access token is in-memory only; this restores it after long idle or tab switch)
  useEffect(() => {
    const handleVisibility = () => {
      if (document.visibilityState === 'visible') {
        refreshToken();
      }
    };
    document.addEventListener('visibilitychange', handleVisibility);
    return () => document.removeEventListener('visibilitychange', handleVisibility);
  }, [refreshToken]);

  const login = useCallback(async (email: string, password: string) => {
    const res = await authApi.login(email, password);
    if (res.requiresMfa) {
      return { mfaRequired: true };
    }
    if (res.requiresMfaSetup) {
      return { mfaSetupRequired: true };
    }
    // If backend returns token directly on login (no MFA)
    return {};
  }, []);

  const logout = useCallback(async () => {
    try { await authApi.logout(); } catch { /* ignore */ }
    setAccessToken(null);
    setUser(null);
  }, []);

  return (
    <AuthContext.Provider value={{ user, loading, login, logout, refreshToken, setTokenAndUser }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used within AuthProvider');
  return ctx;
}
